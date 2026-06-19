using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using DomainLanguage = CardioSimulator.Core.Domain.Language;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Visual block editor for lecture HTML: a reorderable list of typed blocks (header, paragraph,
/// image, KaTeX, ECG, table) that compiles to HTML on every edit. Port of the Android
/// <c>HtmlBlockEditor</c>. Raises <see cref="HtmlChanged"/> with the recompiled body.
/// </summary>
public sealed class HtmlBlockEditor : UserControl
{
    private readonly StackPanel _list = new() { Spacing = 12, Padding = new Thickness(12) };
    private readonly List<HtmlBlock> _blocks = new();
    private readonly Dictionary<string, FrameworkElement> _cards = new();
    private AppViewModel? _appVm;
    private IReadOnlyList<PathologyEntry> _rhythms = Array.Empty<PathologyEntry>();
    private Func<Task<StorageFile?>>? _pickImage;
    private bool _loading;

    /// <summary>Raised when the blocks change, carrying the recompiled HTML body.</summary>
    public event Action<string>? HtmlChanged;

    /// <summary>Raised (block id) when a block card gains focus or is tapped — drives
    /// editor → preview scroll-sync.</summary>
    public event Action<string>? BlockFocused;

    public HtmlBlockEditor()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var addBar = BuildAddBar();
        Grid.SetRow(addBar, 0);
        root.Children.Add(addBar);

        var scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        Content = root;
    }

    public void Initialize(AppViewModel appVm, IReadOnlyList<PathologyEntry> rhythms, Func<Task<StorageFile?>>? pickImage = null)
    {
        _appVm = appVm;
        _rhythms = rhythms;
        _pickImage = pickImage;
    }

    public void SetRhythms(IReadOnlyList<PathologyEntry> rhythms) => _rhythms = rhythms;

    /// <summary>Parses <paramref name="html"/> into editable blocks and rebuilds the UI.</summary>
    public void LoadHtml(string html)
    {
        _loading = true;
        try
        {
            _blocks.Clear();
            _blocks.AddRange(HtmlCompiler.Parse(html));
            Rebuild();
        }
        finally { _loading = false; }
    }

    // ── Add bar ─────────────────────────────────────────────────────────────

    private StackPanel BuildAddBar()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Thickness(12, 8, 12, 8),
        };
        bar.Children.Add(new TextBlock { Text = "Add:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        bar.Children.Add(AddButton("Text", () => new HtmlBlock.Paragraph(string.Empty)));
        bar.Children.Add(AddButton("Header", () => new HtmlBlock.Header(2, string.Empty)));
        bar.Children.Add(AddButton("Math", () => new HtmlBlock.KaTeX(string.Empty, true)));
        bar.Children.Add(AddButton("ECG", () => new HtmlBlock.Ecg(string.Empty, Array.Empty<Lead>(), SeriesScheme.OneColumn, string.Empty)));
        bar.Children.Add(AddButton("Image", () => new HtmlBlock.Image(string.Empty, string.Empty)));
        bar.Children.Add(AddButton("Table", () => new HtmlBlock.Table(new List<IReadOnlyList<string>> { new List<string> { string.Empty } })));
        return bar;
    }

    private Button AddButton(string label, Func<HtmlBlock> factory)
    {
        var btn = new Button { Content = label };
        btn.Click += (_, _) =>
        {
            _blocks.Add(factory());
            Rebuild();
            Emit();
        };
        return btn;
    }

    // ── List building ───────────────────────────────────────────────────────

    private void Rebuild()
    {
        _list.Children.Clear();
        _cards.Clear();
        foreach (var block in _blocks)
        {
            var card = BuildCard(block);
            _cards[block.Id] = card;
            _list.Children.Add(card);
        }
    }

    /// <summary>Scrolls the matching block card into view (preview → editor sync).</summary>
    public void ScrollToBlock(string blockId)
    {
        if (_cards.TryGetValue(blockId, out var card))
            card.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5 });
    }

    private void Emit()
    {
        if (_loading) return;
        HtmlChanged?.Invoke(HtmlCompiler.Compile(_blocks));
    }

    private void Replace(string id, HtmlBlock updated)
    {
        var idx = _blocks.FindIndex(b => b.Id == id);
        if (idx < 0) return;
        _blocks[idx] = updated;
        Emit();
    }

    private void ReplaceAndRebuild(string id, HtmlBlock updated)
    {
        var idx = _blocks.FindIndex(b => b.Id == id);
        if (idx < 0) return;
        _blocks[idx] = updated;
        Rebuild();
        Emit();
    }

    private T? Cur<T>(string id) where T : HtmlBlock => _blocks.FirstOrDefault(b => b.Id == id) as T;

    private void Move(string id, int delta)
    {
        var idx = _blocks.FindIndex(b => b.Id == id);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= _blocks.Count) return;
        (_blocks[idx], _blocks[target]) = (_blocks[target], _blocks[idx]);
        Rebuild();
        Emit();
    }

    private void Delete(string id)
    {
        _blocks.RemoveAll(b => b.Id == id);
        Rebuild();
        Emit();
    }

    private Border BuildCard(HtmlBlock block)
    {
        var content = block switch
        {
            HtmlBlock.Header h => BuildHeaderEditor(h),
            HtmlBlock.Paragraph p => BuildParagraphEditor(p),
            HtmlBlock.Image img => BuildImageEditor(img),
            HtmlBlock.KaTeX k => BuildKaTeXEditor(k),
            HtmlBlock.Ecg e => BuildEcgEditor(e),
            HtmlBlock.Table t => BuildTableEditor(t),
            _ => new TextBlock { Text = "(unknown block)" },
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(content, 0);
        row.Children.Add(content);

        var controls = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
        controls.Children.Add(IconButton("", () => Move(block.Id, -1)));   // up
        controls.Children.Add(IconButton("", () => Move(block.Id, 1)));    // down
        controls.Children.Add(IconButton("", () => Delete(block.Id)));     // delete
        Grid.SetColumn(controls, 1);
        row.Children.Add(controls);

        var card = new Border
        {
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = row,
        };
        // Focus or tap on the card → notify for editor→preview scroll-sync (routed, so any
        // child field's focus bubbles up here).
        card.GotFocus += (_, _) => BlockFocused?.Invoke(block.Id);
        card.Tapped += (_, _) => BlockFocused?.Invoke(block.Id);
        return card;
    }

    private static Button IconButton(string glyph, Action onClick)
    {
        var btn = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(6),
            Margin = new Thickness(4, 0, 0, 0),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static TextBlock TypeLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Colors.SteelBlue),
        Margin = new Thickness(0, 0, 0, 4),
    };

    // ── Per-type editors ──────────────────────────────────────────────────────

    private FrameworkElement BuildHeaderEditor(HtmlBlock.Header block)
    {
        var stack = new StackPanel { Spacing = 4 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(TypeLabel("HEADER"));
        var level = new ComboBox { MinWidth = 64 };
        for (var i = 1; i <= 6; i++) level.Items.Add($"H{i}");
        level.SelectedIndex = Math.Clamp(block.Level - 1, 0, 5);
        level.SelectionChanged += (_, _) =>
        {
            if (Cur<HtmlBlock.Header>(block.Id) is { } cur) Replace(block.Id, cur with { Level = level.SelectedIndex + 1 });
        };
        top.Children.Add(level);
        stack.Children.Add(top);

        var text = new TextBox { Text = block.Text, PlaceholderText = "Header text…", FontSize = 18, FontWeight = FontWeights.Bold };
        text.TextChanged += (_, _) =>
        {
            if (Cur<HtmlBlock.Header>(block.Id) is { } cur) Replace(block.Id, cur with { Text = text.Text });
        };
        stack.Children.Add(text);
        return stack;
    }

    private FrameworkElement BuildParagraphEditor(HtmlBlock.Paragraph block)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(TypeLabel("PARAGRAPH"));
        var text = new TextBox
        {
            Text = block.Html,
            PlaceholderText = "Text or simple HTML…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 56,
        };
        text.TextChanged += (_, _) =>
        {
            if (Cur<HtmlBlock.Paragraph>(block.Id) is { } cur) Replace(block.Id, cur with { Html = text.Text });
        };
        stack.Children.Add(text);
        return stack;
    }

    private FrameworkElement BuildImageEditor(HtmlBlock.Image block)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(TypeLabel("IMAGE"));

        var status = new TextBlock
        {
            Text = DescribeImageSrc(block.Src),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };

        // Declare urlBox before the click handler so the closure can capture it directly.
        var urlBox = new TextBox
        {
            Header = "Or enter URL",
            Text = block.Src.StartsWith("data:") ? string.Empty : block.Src,
            PlaceholderText = "https://…",
        };
        var suppressUrlChange = false;

        var browseBtn = new Button { Content = "Browse image…", IsEnabled = _pickImage is not null };
        browseBtn.Click += async (_, _) =>
        {
            if (_pickImage is null) return;
            var file = await _pickImage();
            if (file is null) return;
            byte[] bytes;
            using (var stream = await file.OpenStreamForReadAsync())
            {
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                bytes = ms.ToArray();
            }
            var mime = ImageMimeFromExtension(file.FileType);
            var dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            if (Cur<HtmlBlock.Image>(block.Id) is { } cur)
            {
                Replace(block.Id, cur with { Src = dataUri });
                status.Text = DescribeImageSrc(dataUri);
                suppressUrlChange = true;
                urlBox.Text = string.Empty;
                suppressUrlChange = false;
            }
        };

        urlBox.TextChanged += (_, _) =>
        {
            if (suppressUrlChange) return;
            if (Cur<HtmlBlock.Image>(block.Id) is { } cur)
            {
                Replace(block.Id, cur with { Src = urlBox.Text });
                status.Text = DescribeImageSrc(urlBox.Text);
            }
        };

        var alt = new TextBox { Header = "Caption", Text = block.Caption };
        alt.TextChanged += (_, _) =>
        {
            if (Cur<HtmlBlock.Image>(block.Id) is { } cur) Replace(block.Id, cur with { Caption = alt.Text });
        };

        var browseRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        browseRow.Children.Add(browseBtn);
        browseRow.Children.Add(status);

        stack.Children.Add(browseRow);
        stack.Children.Add(urlBox);
        stack.Children.Add(alt);
        return stack;
    }

    private static string DescribeImageSrc(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return "No image";
        if (src.StartsWith("data:")) return "Image embedded (file loaded)";
        return src.Length > 60 ? src[..57] + "…" : src;
    }

    private static string ImageMimeFromExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        _ => "image/png",
    };

    /// <summary>Common math/medical symbols for the KaTeX assist toolbar: (LaTeX code, chip label).
    /// Mirrors the Android <c>HtmlBlockEditor</c> chip set.</summary>
    private static readonly (string Code, string Display)[] KatexSymbols =
    {
        (@"\alpha", "α"), (@"\beta", "β"), (@"\gamma", "γ"), (@"\delta", "δ"), (@"\theta", "θ"),
        (@"\lambda", "λ"), (@"\pi", "π"), (@"\sigma", "σ"), (@"\omega", "ω"),
        (@"\Delta", "Δ"), (@"\Sigma", "Σ"), (@"\Omega", "Ω"),
        (@"\infty", "∞"), (@"\approx", "≈"), (@"\neq", "≠"), (@"\le", "≤"), (@"\ge", "≥"), (@"\pm", "±"),
        (@"\times", "×"), (@"\div", "÷"), (@"\sqrt{}", "√"), (@"\frac{}{}", "n/m"), ("^", "xⁿ"), ("_", "xₙ"),
    };

    private FrameworkElement BuildKaTeXEditor(HtmlBlock.KaTeX block)
    {
        var stack = new StackPanel { Spacing = 4 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(TypeLabel("MATH (KaTeX)"));
        var display = new CheckBox { Content = "Display mode", IsChecked = block.DisplayMode };
        display.Checked += (_, _) => { if (Cur<HtmlBlock.KaTeX>(block.Id) is { } c) Replace(block.Id, c with { DisplayMode = true }); };
        display.Unchecked += (_, _) => { if (Cur<HtmlBlock.KaTeX>(block.Id) is { } c) Replace(block.Id, c with { DisplayMode = false }); };
        top.Children.Add(display);
        stack.Children.Add(top);

        var expr = new TextBox
        {
            Text = block.Expression,
            PlaceholderText = "e.g. E = mc^2",
            FontFamily = new FontFamily("Consolas"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
        };

        // Symbol-assist toolbar: insert LaTeX at the caret (Android's AssistChip row).
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (code, displayLabel) in KatexSymbols)
        {
            var captured = code;
            var chip = new Button
            {
                Content = new TextBlock { Text = displayLabel, FontSize = 14 },
                Padding = new Thickness(8, 2, 8, 2),
            };
            chip.Click += (_, _) =>
            {
                var sel = Math.Clamp(expr.SelectionStart, 0, expr.Text.Length);
                var len = Math.Clamp(expr.SelectionLength, 0, expr.Text.Length - sel);
                expr.Text = expr.Text.Substring(0, sel) + captured + expr.Text.Substring(sel + len);
                expr.SelectionStart = sel + captured.Length; // TextChanged below persists the edit
            };
            chipRow.Children.Add(chip);
        }
        stack.Children.Add(new ScrollViewer
        {
            Content = chipRow,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        expr.TextChanged += (_, _) =>
        {
            if (Cur<HtmlBlock.KaTeX>(block.Id) is { } cur) Replace(block.Id, cur with { Expression = expr.Text });
        };
        stack.Children.Add(expr);
        return stack;
    }

    /// <summary>Lead-count presets offered by the "Number of leads" flyout (matches the monitor).</summary>
    private static readonly int[] LeadCountPresets = { 1, 2, 3, 4, 6, 12 };

    private FrameworkElement BuildEcgEditor(HtmlBlock.Ecg block)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(TypeLabel("ECG REFERENCE"));

        // ── Rhythm (pathology) picker ─────────────────────────────────────────
        var rhythmPick = new Button { HorizontalAlignment = HorizontalAlignment.Stretch };
        UpdateRhythmLabel(rhythmPick, block.Pathology);
        rhythmPick.Click += async (_, _) =>
        {
            var id = await PickPathologyAsync(Cur<HtmlBlock.Ecg>(block.Id)?.Pathology);
            if (id is not null && Cur<HtmlBlock.Ecg>(block.Id) is { } cur)
            {
                Replace(block.Id, cur with { Pathology = id });
                UpdateRhythmLabel(rhythmPick, id);
            }
        };
        stack.Children.Add(rhythmPick);

        // ── Lead handpick grid + count/layout controls ────────────────────────
        var suppress = false; // guards programmatic toggle/selection updates

        var countButton = new Button();
        var hint = new TextBlock
        {
            Text = "No leads selected — all 12 leads will be shown.",
            FontSize = 11,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        };

        void RefreshCountAndHint(IReadOnlyList<Lead> leads)
        {
            countButton.Content = $"Leads: {(leads.Count == 0 ? "all (12)" : leads.Count.ToString())}";
            hint.Visibility = leads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 6×2 grid of lead toggles in canonical order (I, II, III, aVR…V6).
        var leadGrid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (var c = 0; c < 6; c++) leadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leadGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leadGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toggles = new Dictionary<Lead, ToggleButton>();
        for (var i = 0; i < Leads.All.Count; i++)
        {
            var lead = Leads.All[i];
            var toggle = new ToggleButton
            {
                Content = lead.ToString(),
                MinWidth = 52,
                Padding = new Thickness(6, 2, 6, 2),
            };
            toggles[lead] = toggle;
            void OnToggle()
            {
                if (suppress || Cur<HtmlBlock.Ecg>(block.Id) is not { } cur) return;
                var set = new SortedSet<Lead>(cur.Leads);
                if (toggle.IsChecked == true) set.Add(lead); else set.Remove(lead);
                var updated = (IReadOnlyList<Lead>)set.ToList();
                Replace(block.Id, cur with { Leads = updated });
                RefreshCountAndHint(updated);
            }
            toggle.Checked += (_, _) => OnToggle();
            toggle.Unchecked += (_, _) => OnToggle();
            Grid.SetColumn(toggle, i % 6);
            Grid.SetRow(toggle, i / 6);
            leadGrid.Children.Add(toggle);
        }

        void SyncToggles(IReadOnlyList<Lead> leads)
        {
            suppress = true;
            foreach (var (lead, toggle) in toggles) toggle.IsChecked = leads.Contains(lead);
            suppress = false;
        }

        // "Number of leads" preset flyout — sets the first N canonical leads (overwrites picks).
        var countFlyout = new MenuFlyout();
        foreach (var n in LeadCountPresets)
        {
            var captured = n;
            var item = new MenuFlyoutItem { Text = captured.ToString() };
            item.Click += (_, _) =>
            {
                if (Cur<HtmlBlock.Ecg>(block.Id) is not { } cur) return;
                var updated = (IReadOnlyList<Lead>)Leads.All.Take(captured).ToList();
                Replace(block.Id, cur with { Leads = updated });
                SyncToggles(updated);
                RefreshCountAndHint(updated);
            };
            countFlyout.Items.Add(item);
        }
        countButton.Flyout = countFlyout;

        // Layout (lines / grid) flyout — mirrors the monitor's series scheme.
        var schemeButton = new Button { Content = $"Layout: {SchemeLabel(block.Scheme)}" };
        var schemeFlyout = new MenuFlyout();
        void AddSchemeItem(SeriesScheme scheme)
        {
            var item = new MenuFlyoutItem { Text = SchemeLabel(scheme) };
            item.Click += (_, _) =>
            {
                if (Cur<HtmlBlock.Ecg>(block.Id) is { } cur)
                {
                    Replace(block.Id, cur with { Scheme = scheme });
                    schemeButton.Content = $"Layout: {SchemeLabel(scheme)}";
                }
            };
            schemeFlyout.Items.Add(item);
        }
        AddSchemeItem(SeriesScheme.OneColumn);
        AddSchemeItem(SeriesScheme.TwoColumn);
        AddSchemeItem(SeriesScheme.Grid);
        schemeButton.Flyout = schemeFlyout;

        var optionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        optionsRow.Children.Add(countButton);
        optionsRow.Children.Add(schemeButton);

        stack.Children.Add(new TextBlock { Text = "Leads", FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.8 });
        stack.Children.Add(leadGrid);
        stack.Children.Add(hint);
        stack.Children.Add(optionsRow);

        // ── Caption ───────────────────────────────────────────────────────────
        var caption = new TextBox { Header = "Caption", Text = block.Caption };
        caption.TextChanged += (_, _) =>
        {
            if (Cur<HtmlBlock.Ecg>(block.Id) is { } cur) Replace(block.Id, cur with { Caption = caption.Text });
        };
        stack.Children.Add(caption);

        SyncToggles(block.Leads);
        RefreshCountAndHint(block.Leads);
        return stack;
    }

    private static string SchemeLabel(SeriesScheme scheme) => scheme switch
    {
        SeriesScheme.TwoColumn => "2 columns",
        SeriesScheme.Grid => "Grid",
        _ => "1 column",
    };

    private void UpdateRhythmLabel(Button button, string pathology)
    {
        if (string.IsNullOrWhiteSpace(pathology))
        {
            button.Content = "Select rhythm…";
            return;
        }
        var entry = _rhythms.FirstOrDefault(r => r.Id == pathology);
        button.Content = entry is null
            ? pathology
            : (_appVm?.SelectedLanguage == DomainLanguage.RU ? (entry.NameRu ?? entry.TitleEn) : entry.TitleEn);
    }

    /// <summary>Modal pathology picker (rhythm list only). Returns the chosen id, or null.</summary>
    private async Task<string?> PickPathologyAsync(string? currentId)
    {
        if (_appVm is null) return null;
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinWidth = 320,
            MaxHeight = 420,
        };
        foreach (var r in _rhythms)
        {
            var label = _appVm.SelectedLanguage == DomainLanguage.RU ? (r.NameRu ?? r.TitleEn) : r.TitleEn;
            var item = new ListViewItem { Content = label, Tag = r.Id };
            list.Items.Add(item);
            if (r.Id == currentId) list.SelectedItem = item;
        }
        var dialog = new ContentDialog
        {
            Title = "Select rhythm",
            Content = list,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = currentId is not null,
        };
        list.SelectionChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = (list.SelectedItem as ListViewItem)?.Tag is string;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return (list.SelectedItem as ListViewItem)?.Tag as string;
    }

    private FrameworkElement BuildTableEditor(HtmlBlock.Table block)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(TypeLabel("TABLE"));

        var rows = block.Rows.Select(r => r.ToList()).ToList();
        var rowCount = rows.Count;
        var colCount = rowCount > 0 ? rows[0].Count : 0;

        var ops = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addCol = new Button { Content = "+ Column" };
        addCol.Click += (_, _) =>
        {
            var newRows = rowCount == 0
                ? new List<IReadOnlyList<string>> { new List<string> { string.Empty } }
                : rows.Select(r => (IReadOnlyList<string>)r.Append(string.Empty).ToList()).ToList();
            if (Cur<HtmlBlock.Table>(block.Id) is { } cur) ReplaceAndRebuild(block.Id, cur with { Rows = newRows });
        };
        var addRow = new Button { Content = "+ Row" };
        addRow.Click += (_, _) =>
        {
            var width = Math.Max(1, colCount);
            var newRows = rows.Select(r => (IReadOnlyList<string>)r).ToList();
            newRows.Add(Enumerable.Repeat(string.Empty, width).ToList());
            if (Cur<HtmlBlock.Table>(block.Id) is { } cur) ReplaceAndRebuild(block.Id, cur with { Rows = newRows });
        };
        ops.Children.Add(addCol);
        ops.Children.Add(addRow);
        stack.Children.Add(ops);

        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (var c = 0; c < colCount; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // delete-row column
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });      // delete-column header
        for (var r = 0; r < rowCount; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header row: a delete button per column.
        for (var c = 0; c < colCount; c++)
        {
            var colIndex = c;
            var delCol = new Button
            {
                Content = new TextBlock { Text = "✕", FontSize = 12 },
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(0, 0, 0, 2),
            };
            delCol.Click += (_, _) =>
            {
                if (Cur<HtmlBlock.Table>(block.Id) is not { } cur) return;
                var newRows = cur.Rows
                    .Select(row => (IReadOnlyList<string>)row.Where((_, i) => i != colIndex).ToList())
                    .ToList();
                ReplaceAndRebuild(block.Id, cur with { Rows = newRows });
            };
            Grid.SetRow(delCol, 0);
            Grid.SetColumn(delCol, c);
            grid.Children.Add(delCol);
        }

        for (var r = 0; r < rowCount; r++)
        {
            for (var c = 0; c < colCount; c++)
            {
                var rr = r;
                var cc = c;
                var cell = new TextBox { Text = rows[r][c], AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
                cell.TextChanged += (_, _) =>
                {
                    if (Cur<HtmlBlock.Table>(block.Id) is not { } cur) return;
                    var grid2 = cur.Rows.Select(row => row.ToList()).ToList();
                    if (rr < grid2.Count && cc < grid2[rr].Count)
                    {
                        grid2[rr][cc] = cell.Text;
                        Replace(block.Id, cur with { Rows = grid2.Select(x => (IReadOnlyList<string>)x).ToList() });
                    }
                };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }

            var rowIndex = r;
            var del = IconButton("", () =>
            {
                if (Cur<HtmlBlock.Table>(block.Id) is not { } cur) return;
                var newRows = cur.Rows.Where((_, i) => i != rowIndex).ToList();
                ReplaceAndRebuild(block.Id, cur with { Rows = newRows });
            });
            Grid.SetRow(del, r + 1);
            Grid.SetColumn(del, colCount);
            grid.Children.Add(del);
        }

        stack.Children.Add(grid);
        return stack;
    }
}
