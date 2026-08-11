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
    /// <summary>
    /// Resolves a <c>coursehost</c> request path ("&lt;courseId&gt;/rel/file.ext") to raw bytes,
    /// decrypting from the in-memory course pack (or reading the file-backed dataset). Set once at
    /// app startup (see <c>AppViewModel</c>). When null, course assets are served as 404. Static
    /// because every lecture WebView shares the one app-wide course source.
    /// </summary>
    public static Func<string, byte[]?>? AssetResolver { get; set; }

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

    /// <summary>Raised (on the UI thread) when a <see cref="SetLecture"/> render begins, before any
    /// off-thread work or navigation — lets the host show a loading indicator until
    /// <see cref="LoadingCompleted"/>. Opening a lecture reads/parses its HTML, resolves inline
    /// <c>&lt;ecg&gt;</c> figures, then navigates the WebView and lays out KaTeX, so there is a
    /// perceptible gap before content paints; without this the view just sits blank (feels frozen).</summary>
    public event Action? LoadingStarted;

    /// <summary>Raised (on the UI thread) when a lecture render finishes — navigation completed,
    /// or an identical re-render short-circuited — so the host can hide its loading indicator and
    /// reveal the content.</summary>
    public event Action? LoadingCompleted;

    /// <summary>When true, clicking a rendered top-level block posts an "edit" request naming that
    /// block's id (the constructor's click-to-edit). Left false for read-only viewers (Teaching). Read
    /// at render time, so set it before the first <see cref="SetLecture"/>; changing it takes effect on
    /// the next render.</summary>
    public bool EnableEditClicks { get; set; }

    /// <summary>Raised (block id) when a rendered top-level block is clicked while
    /// <see cref="EnableEditClicks"/> is on — drives the constructor's click-to-edit.</summary>
    public event Action<string>? EditElementRequested;

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
        _web.NavigationCompleted += OnNavigationCompleted;
        Unloaded += OnUnloaded;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await _web.EnsureCoreWebView2Async();
        var core = _web.CoreWebView2;
        if (core is null) return; // control was unloaded/closed during initialization
        // KaTeX and other app-shipped web assets are not protected content, so they stay a plain
        // folder mapping. The lecture document (lecturehost) and course assets (coursehost) are
        // served from memory via WebResourceRequested instead — no plaintext HTML or course asset is
        // ever written to disk (the course dataset is an encrypted in-memory pack).
        var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        core.SetVirtualHostNameToFolderMapping("appassets", assetsDir, CoreWebView2HostResourceAccessKind.Allow);
        core.AddWebResourceRequestedFilter("https://lecturehost/*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter("https://coursehost/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
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

    /// <summary>Clears the cached rendered HTML so the next <see cref="SetLecture"/> forces a re-render.</summary>
    public void ClearCache() => _currentHtml = null;

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
        // Signal loading up front — before the off-thread render and navigation below, and even
        // while the WebView is still initializing (the render is deferred to _pendingLecture) — so
        // the host's indicator is already up for the whole gap. LoadingCompleted fires from the
        // eventual render (navigation completed, or an identical-HTML short-circuit).
        LoadingStarted?.Invoke();
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
        var editClicks = EnableEditClicks;

        // Rendering is content-driven: a still-whole <!doctype…> page is served verbatim (standalone);
        // once it is decomposed into a fragment (an embedded page block + app components), it is a body
        // fragment and lays out inside the standard app template. This also avoids BuildDocument wrapping a
        // whole document in another <body> (which hoists its <head> into the body and breaks scroll-sync).
        var standalone = HtmlCompiler.IsFullDocument(lecture.RawHtml);

        // <ecg> resolution reads pathology .dat files — build off the UI thread.
        var html = await Task.Run(() =>
        {
            var substituted = EcgSvgRenderer.SubstituteEcgTags(lecture.RawHtml, resolve, _monitorButtonLabel);
            substituted = EcgSvgRenderer.SubstituteEcgSegmentTags(substituted, resolve);
            // "All in one": a complete pasted page is served verbatim with our KaTeX / <ecg> / quiz
            // features layered on; otherwise the body fragment is wrapped in the standard document.
            return standalone
                ? BuildStandaloneDocument(substituted, lecture.CourseId, interactive, editClicks)
                : BuildDocument(substituted, lecture.CourseId, interactive, editClicks);
        });

        if (html == _currentHtml)
        {
            // Same document already shown (e.g. re-selecting the open lecture): no navigation will
            // fire, so clear the loading indicator here after re-applying saved answers.
            await InjectAnswersAsync();
            LoadingCompleted?.Invoke();
            return;
        }

        // During the awaits above the control may have been torn down (Unloaded → _web.Close()
        // nulls CoreWebView2) or re-parented before re-init. Guard the deref instead of throwing:
        // re-stash the lecture so it renders if/when initialization completes. (Don't commit
        // _currentHtml until we actually navigate, or a later identical render would short-circuit
        // to a blank view.)
        var core = _web.CoreWebView2;
        if (core is null)
        {
            _pendingLecture = lecture;
            // Torn down mid-render: clear the indicator so it can't stick if the control is re-shown.
            LoadingCompleted?.Invoke();
            return;
        }
        // The document is served from memory by OnWebResourceRequested — never written to disk.
        _currentHtml = html;
        core.Navigate($"https://lecturehost/{_docFileName}?v={++_navVersion}");
    }

    private async void OnNavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess) await InjectAnswersAsync();
        // Clear the loading indicator whether the load succeeded or failed — a failed navigation
        // must not leave a spinner up forever.
        LoadingCompleted?.Invoke();
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

    /// <summary>Scrolls the preview to a nested element addressed by a child-element index path.
    /// <paramref name="anchorId"/> names the ancestor to start from (null = <c>document.body</c>, for a
    /// standalone document); <paramref name="indices"/> are walked over <c>.children</c>. No-op if the
    /// page isn't ready or the path doesn't resolve (e.g. after a re-render).</summary>
    public async void ScrollToElement(string? anchorId, IReadOnlyList<int> indices)
    {
        if (!_ready) return;
        var anchorJs = anchorId is null
            ? "document.body"
            : "document.getElementById(" + JsonSerializer.Serialize(anchorId) + ")";
        var idxJs = JsonSerializer.Serialize(indices);
        var js = "(function(){var el=" + anchorJs + ";if(!el)return;var idx=" + idxJs +
                 ";for(var k=0;k<idx.length;k++){if(!el.children||idx[k]>=el.children.length){el=null;break;}" +
                 "el=el.children[idx[k]];}if(el)el.scrollIntoView({behavior:'smooth',block:'center'});})();";
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

            // Click-to-edit (constructor preview): jump the block editor to the clicked block.
            if (root.TryGetProperty("type", out var etype) && etype.GetString() == "edit")
            {
                if (root.TryGetProperty("blockId", out var eid) && eid.GetString() is { Length: > 0 } editId)
                    EditElementRequested?.Invoke(editId);
                return;
            }

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
        try { _web.Close(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Serves the two in-memory virtual hosts so no plaintext content touches disk:
    /// <c>lecturehost</c> returns the current generated lecture document, and <c>coursehost</c>
    /// returns course assets resolved by <see cref="AssetResolver"/> (from the encrypted pack or the
    /// file-backed dataset). Unknown/absent resources respond 404.
    /// </summary>
    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            var uri = new Uri(args.Request.Uri);
            switch (uri.Host)
            {
                case "lecturehost":
                    args.Response = _currentHtml is { } html
                        ? MakeResponse(sender, Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8")
                        : NotFound(sender);
                    break;

                case "coursehost":
                    var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
                    var bytes = AssetResolver?.Invoke(path);
                    args.Response = bytes is not null
                        ? MakeResponse(sender, bytes, GuessContentType(path))
                        : NotFound(sender);
                    break;
            }
        }
        catch
        {
            // Leave args.Response unset → WebView2 applies its default handling.
        }
    }

    private static CoreWebView2WebResourceResponse MakeResponse(CoreWebView2 core, byte[] body, string contentType)
    {
        // WinUI WebView2 wants an IRandomAccessStream; wrap the in-memory bytes without touching disk.
        var stream = new MemoryStream(body, writable: false).AsRandomAccessStream();
        var headers = $"Content-Type: {contentType}\r\nCache-Control: no-store";
        return core.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
    }

    private static CoreWebView2WebResourceResponse NotFound(CoreWebView2 core) =>
        core.Environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);

    private static string GuessContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".json" => "application/json",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            _ => "application/octet-stream",
        };
    }

    private static string BuildDocument(string body, string courseId, bool interactive, bool editClicks)
    {
        var bridge = interactive ? QuizBridgeJs : string.Empty;
        var editJs = editClicks ? EditClickJs : string.Empty;
        var editCss = editClicks ? EditClickCss : string.Empty;
        return $$"""
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<base href="https://coursehost/{{courseId}}/">
<link rel="stylesheet" href="https://appassets/katex/katex.min.css">
<style>{{ThemeCss}}{{HtmlComponents.Css}}{{editCss}}</style>
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
{{editJs}}
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
    private static string BuildStandaloneDocument(string rawHtml, string courseId, bool interactive, bool editClicks)
    {
        var head = new StringBuilder();
        if (rawHtml.IndexOf("<base", StringComparison.OrdinalIgnoreCase) < 0)
            head.Append($"<base href=\"https://coursehost/{courseId}/\">");
        head.Append("<link rel=\"stylesheet\" href=\"https://appassets/katex/katex.min.css\">");
        // App-component styles (Card/Note/List/…) so components inserted into a still-whole page render right.
        head.Append("<style>").Append(HtmlComponents.Css);
        if (editClicks) head.Append(EditClickCss);
        head.Append("</style>");

        var bridge = interactive ? QuizBridgeJs : string.Empty;
        var editJs = editClicks ? EditClickJs : string.Empty;
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
{{editJs}}
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

    private static string ThemeCss => Theming.AppTheme.IsDark ? """
html,body{margin:0;padding:0}
body{background:#1C1C1E;color:#FFFFFF;
  font-family:-apple-system,"Segoe UI",Roboto,sans-serif;
  font-size:16px;line-height:1.55;padding:16px;-webkit-text-size-adjust:100%}
h1,h2,h3{line-height:1.25;color:#FFFFFF}
a{color:#409CFF}
img{max-width:100%;height:auto}
table{border-collapse:collapse;width:100%;margin:1em 0}
th,td{border:1px solid #38383A;padding:6px 10px;text-align:left;vertical-align:top}
th{background:#2C2C2E;color:#FFFFFF}
input,textarea{font:inherit;color:#FFFFFF;background:#2C2C2E;
  border:1px solid #38383A;border-radius:4px;padding:2px 6px;width:100%;box-sizing:border-box}
figure.ecg-figure,figure.img-figure{margin:1em 0}
svg.ecg-lead{max-width:100%;height:auto;display:block;margin:2px 0}
figure.ecg-figure figcaption{font-size:.9em;color:#8E8E93;margin-top:4px}
figure.img-figure figcaption{font-size:.9em;color:#8E8E93;margin-top:4px;text-align:center}
.ecg-missing figcaption{color:#ff6961}
""" : """
html,body{margin:0;padding:0}
body{background:#FFFFFF;color:#1C1C1E;
  font-family:-apple-system,"Segoe UI",Roboto,sans-serif;
  font-size:16px;line-height:1.55;padding:16px;-webkit-text-size-adjust:100%}
h1,h2,h3{line-height:1.25;color:#1C1C1E}
a{color:#007AFF}
img{max-width:100%;height:auto}
table{border-collapse:collapse;width:100%;margin:1em 0}
th,td{border:1px solid #D1D1D6;padding:6px 10px;text-align:left;vertical-align:top}
th{background:#F2F2F7;color:#1C1C1E}
input,textarea{font:inherit;color:#1C1C1E;background:#F9F9FB;
  border:1px solid #D1D1D6;border-radius:4px;padding:2px 6px;width:100%;box-sizing:border-box}
figure.ecg-figure,figure.img-figure{margin:1em 0}
svg.ecg-lead{max-width:100%;height:auto;display:block;margin:2px 0}
figure.ecg-figure figcaption{font-size:.9em;color:#8E8E93;margin-top:4px}
figure.img-figure figcaption{font-size:.9em;color:#8E8E93;margin-top:4px;text-align:center}
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

    // Click-to-edit (constructor preview): reports the id of the nearest element the author clicked that
    // carries one — at any depth, so a nested component (e.g. an ECG inside a card/section, whose figure
    // keeps its own id) resolves, not just direct <body> children. The host maps the id to a top-level
    // block or a nested structure node. Clicks on interactive controls (quiz inputs, buttons, links) are
    // left alone. Capture phase so it fires before any in-page handler.
    private const string EditClickJs = """
  document.body.addEventListener('click', function(e){
    if (e.target.closest('input,textarea,select,button,a,label')) return;
    var el = e.target.closest('[id]');
    if (el && el.id && window.chrome && window.chrome.webview)
      window.chrome.webview.postMessage(JSON.stringify({type:"edit", blockId: el.id}));
  }, true);
""";

    // Hover affordance shown only in click-to-edit mode: a soft accent outline on the hovered block so
    // the author sees the preview is now clickable. Interactive controls keep the default cursor.
    private const string EditClickCss = """
body>*{cursor:pointer}
body>*:hover{outline:2px solid rgba(0,122,255,.35);outline-offset:2px;border-radius:4px}
input,textarea,select,button,a{cursor:auto}
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
