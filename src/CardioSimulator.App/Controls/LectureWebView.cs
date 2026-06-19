using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CardioSimulator.App.Data;
using CardioSimulator.App.Rendering;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace CardioSimulator.App.Controls;

/// <summary>A request, raised from an <c>&lt;ecg&gt;</c> embed's "open on monitor" button, to show
/// the given pathology on the live monitor with the embed's lead count + layout.</summary>
public sealed record EcgMonitorRequest(string PathologyId, IReadOnlyList<Lead> Leads, SeriesScheme Scheme);

/// <summary>
/// Renders a whole <see cref="Lecture"/> as one HTML document in a WebView2: the body
/// (<c>&lt;ecg&gt;</c> elements rewritten to inline SVG by <see cref="EcgSvgRenderer"/>),
/// KaTeX math auto-rendered, and (in constructor mode) editable quiz <c>&lt;input&gt;</c>s wired
/// through a JS message bridge. Port of the Android <c>LectureWebView</c>. KaTeX + course assets
/// are served from virtual hosts (<c>appassets</c> / <c>coursehost</c>); the document itself is
/// served from <c>lecturehost</c> so cross-origin font/CSS loads behave.
/// </summary>
public sealed class LectureWebView : Grid
{
    private static readonly string TempDir =
        Path.Combine(Path.GetTempPath(), "CardioSimulatorWeb");

    private readonly WebView2 _web = new();
    private readonly string _docFileName = $"lecture_{Guid.NewGuid():N}.html";
    private bool _ready;
    private int _navVersion;
    private string? _currentHtml;

    /// <summary>Raised (block id) when the preview is scrolled, naming the block nearest the
    /// viewport center — drives reverse scroll-sync to the block editor.</summary>
    public event Action<string>? PreviewScrolled;

    /// <summary>Raised when an <c>&lt;ecg&gt;</c> embed's "open on monitor" button is tapped.</summary>
    public event Action<EcgMonitorRequest>? EcgOpenMonitorRequested;

    private Func<string, Lead?, IReadOnlyList<EcgTrace>>? _resolveEcg;
    private Action<string, int, int, string>? _onCellEdit;
    private string? _monitorButtonLabel;
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _answers
        = new Dictionary<string, IReadOnlyDictionary<string, string>>();

    // Pending request captured before CoreWebView2 finished initializing.
    private Lecture? _pendingLecture;

    public LectureWebView()
    {
        Children.Add(_web);
        Directory.CreateDirectory(TempDir);
        _web.NavigationCompleted += OnNavigationCompleted;
        Unloaded += OnUnloaded;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await _web.EnsureCoreWebView2Async();
        var core = _web.CoreWebView2;
        var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        core.SetVirtualHostNameToFolderMapping("appassets", assetsDir, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping("coursehost", AppPaths.CoursesDir, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping("lecturehost", TempDir, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessageReceived;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        _ready = true;
        if (_pendingLecture is { } pending)
        {
            _pendingLecture = null;
            await RenderAsync(pending);
        }
    }

    /// <summary>
    /// Renders <paramref name="lecture"/>. <paramref name="resolveEcg"/> resolves
    /// <c>&lt;ecg&gt;</c> embeds; <paramref name="onCellEdit"/> (non-null = constructor mode)
    /// receives quiz-cell edits; <paramref name="answers"/> pre-fills saved quiz answers.
    /// </summary>
    public async void SetLecture(
        Lecture lecture,
        Func<string, Lead?, IReadOnlyList<EcgTrace>> resolveEcg,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? answers = null,
        Action<string, int, int, string>? onCellEdit = null,
        string? monitorButtonLabel = null)
    {
        _resolveEcg = resolveEcg;
        _onCellEdit = onCellEdit;
        _monitorButtonLabel = monitorButtonLabel;
        _answers = answers ?? new Dictionary<string, IReadOnlyDictionary<string, string>>();
        if (!_ready)
        {
            _pendingLecture = lecture;
            return;
        }
        await RenderAsync(lecture);
    }

    private async Task RenderAsync(Lecture lecture)
    {
        var resolve = _resolveEcg ?? ((_, _) => Array.Empty<EcgTrace>());
        var interactive = _onCellEdit is not null;

        // <ecg> resolution reads pathology .dat files — build off the UI thread.
        var html = await Task.Run(() =>
        {
            var substituted = EcgSvgRenderer.SubstituteEcgTags(lecture.RawHtml, resolve, _monitorButtonLabel);
            // "All in one": a complete pasted page is served verbatim with our KaTeX / <ecg> / quiz
            // features layered on; otherwise the body fragment is wrapped in the standard document.
            return lecture.IsStandalone
                ? BuildStandaloneDocument(substituted, lecture.CourseId, interactive)
                : BuildDocument(substituted, lecture.CourseId, interactive);
        });

        if (html == _currentHtml)
        {
            await InjectAnswersAsync();
            return;
        }
        _currentHtml = html;

        var path = Path.Combine(TempDir, _docFileName);
        await File.WriteAllTextAsync(path, html, new UTF8Encoding(false));
        _web.CoreWebView2.Navigate($"https://lecturehost/{_docFileName}?v={++_navVersion}");
    }

    private async void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess) await InjectAnswersAsync();
    }

    /// <summary>Scrolls the preview so the block with <paramref name="blockId"/> is centered
    /// (editor → preview sync). No-op if the page isn't ready or the id isn't found.</summary>
    public async void ScrollToBlock(string blockId)
    {
        if (!_ready || string.IsNullOrEmpty(blockId)) return;
        var idLiteral = JsonSerializer.Serialize(blockId);
        var js = "(function(){var e=document.getElementById(" + idLiteral +
                 ");if(e)e.scrollIntoView({behavior:'smooth',block:'center'});})();";
        try { await _web.CoreWebView2.ExecuteScriptAsync(js); }
        catch { /* page not ready / navigated away */ }
    }

    private async Task InjectAnswersAsync()
    {
        if (_answers.Count == 0) return;
        try { await _web.CoreWebView2.ExecuteScriptAsync(BuildAnswerInjectScript(_answers)); }
        catch { /* page not ready / navigated away */ }
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.TryGetWebMessageAsString());
            var root = doc.RootElement;

            // Scroll-sync notification (preview → editor).
            if (root.TryGetProperty("type", out var type) && type.GetString() == "scroll")
            {
                if (root.TryGetProperty("blockId", out var id) && id.GetString() is { Length: > 0 } blockId)
                    PreviewScrolled?.Invoke(blockId);
                return;
            }

            // "Open on monitor" button (Teaching course view).
            if (root.TryGetProperty("type", out var mtype) && mtype.GetString() == "openMonitor")
            {
                var pathology = root.TryGetProperty("pathology", out var p) ? p.GetString() ?? "" : "";
                if (pathology.Length > 0)
                {
                    var leadsCsv = root.TryGetProperty("leads", out var l) ? l.GetString() ?? "" : "";
                    var schemeTok = root.TryGetProperty("scheme", out var s) ? s.GetString() ?? "" : "";
                    EcgOpenMonitorRequested?.Invoke(
                        new EcgMonitorRequest(pathology, Leads.ParseList(leadsCsv), SeriesSchemes.Parse(schemeTok)));
                }
                return;
            }

            // Quiz-cell edit (constructor mode only).
            if (_onCellEdit is null) return;
            var quizId = root.GetProperty("quizId").GetString() ?? "";
            var rowIdx = root.GetProperty("row").GetInt32();
            var col = root.GetProperty("col").GetInt32();
            var value = root.GetProperty("value").GetString() ?? "";
            _onCellEdit(quizId, rowIdx, col, value);
        }
        catch { /* ignore malformed bridge messages */ }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try { File.Delete(Path.Combine(TempDir, _docFileName)); } catch { /* best effort */ }
        try { _web.Close(); } catch { /* ignore */ }
    }

    private static string BuildDocument(string body, string courseId, bool interactive)
    {
        var bridge = interactive ? QuizBridgeJs : string.Empty;
        return $$"""
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<base href="https://coursehost/{{courseId}}/">
<link rel="stylesheet" href="https://appassets/katex/katex.min.css">
<style>{{ThemeCss}}</style>
</head>
<body>
{{body}}
<script src="https://appassets/katex/katex.min.js"></script>
<script src="https://appassets/katex/contrib/auto-render.min.js"></script>
<script>
(function(){
  function render(){
    if (window.renderMathInElement) {
      renderMathInElement(document.body, {delimiters:[
        {left:"$$",right:"$$",display:true},
        {left:"$",right:"$",display:false},
        {left:"\\(",right:"\\)",display:false},
        {left:"\\[",right:"\\]",display:true}
      ], throwOnError:false});
    }
  }
  if (document.readyState!=="loading") render(); else document.addEventListener("DOMContentLoaded", render);
{{bridge}}
{{ScrollSyncJs}}
{{MonitorBridgeJs}}
})();
</script>
</body>
</html>
""";
    }

    /// <summary>
    /// Serves a pasted full HTML document ("All in one") verbatim, injecting the KaTeX stylesheet +
    /// a course <c>&lt;base&gt;</c> into the head, and KaTeX auto-render, the quiz bridge, and
    /// scroll-sync before <c>&lt;/body&gt;</c>. <c>&lt;ecg&gt;</c> tags are already substituted by the
    /// caller. Injection is by tolerant string insertion so it survives imperfect markup.
    /// </summary>
    private static string BuildStandaloneDocument(string rawHtml, string courseId, bool interactive)
    {
        var head = new StringBuilder();
        if (rawHtml.IndexOf("<base", StringComparison.OrdinalIgnoreCase) < 0)
            head.Append($"<base href=\"https://coursehost/{courseId}/\">");
        head.Append("<link rel=\"stylesheet\" href=\"https://appassets/katex/katex.min.css\">");

        var bridge = interactive ? QuizBridgeJs : string.Empty;
        var body = $$"""
<script src="https://appassets/katex/katex.min.js"></script>
<script src="https://appassets/katex/contrib/auto-render.min.js"></script>
<script>
(function(){
  function render(){
    if (window.renderMathInElement) {
      renderMathInElement(document.body, {delimiters:[
        {left:"$$",right:"$$",display:true},
        {left:"$",right:"$",display:false},
        {left:"\\(",right:"\\)",display:false},
        {left:"\\[",right:"\\]",display:true}
      ], throwOnError:false});
    }
  }
  if (document.readyState!=="loading") render(); else document.addEventListener("DOMContentLoaded", render);
{{bridge}}
{{ScrollSyncJs}}
{{MonitorBridgeJs}}
})();
</script>
""";

        return InsertBeforeBodyClose(InjectHead(rawHtml, head.ToString()), body);
    }

    /// <summary>Inserts <paramref name="fragment"/> before <c>&lt;/head&gt;</c>; if absent, before the
    /// body close; if that's absent too, at the very top.</summary>
    private static string InjectHead(string html, string fragment)
    {
        var headClose = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headClose >= 0) return html.Insert(headClose, fragment);
        var bodyClose = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClose >= 0) return html.Insert(bodyClose, fragment);
        return fragment + html;
    }

    /// <summary>Inserts <paramref name="fragment"/> before the last <c>&lt;/body&gt;</c>; if absent,
    /// appends to the end.</summary>
    private static string InsertBeforeBodyClose(string html, string fragment)
    {
        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return bodyClose >= 0 ? html.Insert(bodyClose, fragment) : html + fragment;
    }

    private const string ThemeCss = """
html,body{margin:0;padding:0}
body{background:#FFFFFF;color:#111111;
  font-family:-apple-system,"Segoe UI",Roboto,sans-serif;
  font-size:16px;line-height:1.55;padding:16px;-webkit-text-size-adjust:100%}
h1,h2,h3{line-height:1.25}
a{color:#1976D2}
img{max-width:100%;height:auto}
table{border-collapse:collapse;width:100%;margin:1em 0}
th,td{border:1px solid #D0D0D0;padding:6px 10px;text-align:left;vertical-align:top}
th{background:#F2F2F2}
input,textarea{font:inherit;color:inherit;background:transparent;
  border:1px solid #D0D0D0;border-radius:4px;padding:2px 6px;width:100%;box-sizing:border-box}
figure.ecg-figure,figure.img-figure{margin:1em 0}
svg.ecg-lead{max-width:100%;height:auto;display:block;margin:2px 0}
figure.ecg-figure figcaption{font-size:.9em;color:#666;margin-top:4px}
figure.img-figure figcaption{font-size:.9em;color:#555;margin-top:4px;text-align:center}
.ecg-missing figcaption{color:#b00020}
""";

    // Wires editable quiz <input>s to the host via window.chrome.webview.postMessage.
    // Keys mirror .answers.json (0-based row over data <tr>s only, 0-based col over all cells).
    private const string QuizBridgeJs = """
  document.querySelectorAll('table[data-quiz-id][data-editable="true"]').forEach(function(tbl){
    var quizId = tbl.getAttribute('data-quiz-id');
    var rows = tbl.querySelectorAll('tr'); var dataRow = -1;
    for (var r=0; r<rows.length; r++){
      if (rows[r].querySelector('th')) continue; dataRow++;
      var cells = rows[r].children;
      for (var c=0; c<cells.length; c++){
        var input = cells[c].querySelector('input, textarea');
        if (input){
          (function(qid,row,col,inp){
            inp.addEventListener('input', function(){
              if (window.chrome && window.chrome.webview)
                window.chrome.webview.postMessage(JSON.stringify({quizId:qid,row:row,col:col,value:inp.value}));
            });
          })(quizId, dataRow, c, input);
        }
      }
    }
  });
""";

    // Wires each <ecg> embed's "open on monitor" button to the host, carrying the embed's
    // pathology / leads / scheme so the live monitor can mirror the figure.
    private const string MonitorBridgeJs = """
  document.querySelectorAll('button.ecg-open-monitor').forEach(function(btn){
    btn.addEventListener('click', function(){
      if (window.chrome && window.chrome.webview)
        window.chrome.webview.postMessage(JSON.stringify({
          type:"openMonitor",
          pathology: btn.getAttribute('data-pathology') || '',
          leads: btn.getAttribute('data-leads') || '',
          scheme: btn.getAttribute('data-scheme') || ''
        }));
    });
  });
""";

    // Reports the top-level block nearest the viewport center on scroll (preview → editor sync).
    // Only body's direct children carry block ids (HtmlCompiler stamps them), so KaTeX-internal
    // ids are ignored. Throttled with a short timeout.
    private const string ScrollSyncJs = """
  var _scrollSyncTimer;
  function _reportCenteredBlock(){
    var kids = document.body.children; var cy = window.innerHeight / 2;
    var best = null, bestD = Infinity;
    for (var i=0;i<kids.length;i++){
      var e = kids[i]; if (!e.id) continue;
      var r = e.getBoundingClientRect(); var c = (r.top + r.bottom) / 2; var d = Math.abs(c - cy);
      if (d < bestD){ bestD = d; best = e.id; }
    }
    if (best && window.chrome && window.chrome.webview)
      window.chrome.webview.postMessage(JSON.stringify({type:"scroll", blockId:best}));
  }
  window.addEventListener("scroll", function(){
    clearTimeout(_scrollSyncTimer); _scrollSyncTimer = setTimeout(_reportCenteredBlock, 120);
  }, {passive:true});
""";

    private static string BuildAnswerInjectScript(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> answers)
    {
        var map = answers.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary(c => c.Key, c => c.Value));
        var json = JsonSerializer.Serialize(map);
        return $$"""
(function(){
  try {
    var a = {{json}};
    document.querySelectorAll('table[data-quiz-id]').forEach(function(tbl){
      var m = a[tbl.getAttribute('data-quiz-id')]; if (!m) return;
      var rows = tbl.querySelectorAll('tr'); var dr = -1;
      for (var r=0; r<rows.length; r++){
        if (rows[r].querySelector('th')) continue; dr++;
        var cells = rows[r].children;
        for (var c=0; c<cells.length; c++){
          var inp = cells[c].querySelector('input, textarea');
          if (inp){ var v = m[dr+','+c]; if (v !== undefined && v !== null) inp.value = v; }
        }
      }
    });
  } catch(e) {}
})();
""";
    }
}
