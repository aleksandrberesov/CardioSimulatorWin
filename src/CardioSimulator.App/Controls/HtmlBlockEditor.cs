using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
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

    /// <summary>Raised when a nested element is selected in a Raw block's structure tree, so the preview
    /// can scroll to it. <c>AnchorId</c> is the ancestor block id to start from (null = document body,
    /// for a standalone document); <c>Indices</c> is the child-element index path from that anchor.</summary>
    public event Action<string?, IReadOnlyList<int>>? ElementSelected;

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

    private FrameworkElement BuildAddBar()
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
        bar.Children.Add(AddButton("List", () => new HtmlBlock.List(new List<string> { string.Empty }, false)));
        bar.Children.Add(AddButton("Quote", () => new HtmlBlock.Quote(string.Empty)));
        bar.Children.Add(AddButton("Note", () => new HtmlBlock.Note("info", string.Empty)));
        bar.Children.Add(AddButton("Card", () => new HtmlBlock.Card(string.Empty, string.Empty)));
        bar.Children.Add(AddButton("Section", () => new HtmlBlock.Section(string.Empty, string.Empty)));
        bar.Children.Add(AddButton("Figure", () => new HtmlBlock.Figure(string.Empty, string.Empty)));
        bar.Children.Add(AddButton("Image", () => new HtmlBlock.Image(string.Empty, string.Empty)));
        bar.Children.Add(AddButton("ECG", () => new HtmlBlock.Ecg(string.Empty, Array.Empty<Lead>(), SeriesScheme.OneColumn, string.Empty)));
        bar.Children.Add(AddButton("ECG seg", () => new HtmlBlock.EcgSegment(string.Empty, Lead.II, 0, HtmlCompiler.DefaultSegmentSeconds, string.Empty)));
        bar.Children.Add(AddButton("Table", () => new HtmlBlock.Table(new List<IReadOnlyList<string>> { new List<string> { string.Empty } })));
        bar.Children.Add(AddButton("Math", () => new HtmlBlock.KaTeX(string.Empty, true)));
        bar.Children.Add(AddButton("Divider", () => new HtmlBlock.Divider()));
        bar.Children.Add(AddButton("Container", () => new HtmlBlock.Container(string.Empty)));

        // The palette is wider than the pane on a narrow window — let it scroll horizontally.
        return new ScrollViewer
        {
            Content = bar,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private Button AddButton(string label, Func<HtmlBlock> factory)
    {
        var btn = new Button { Content = label };
        btn.Click += (_, _) =>
        {
            EnsureComposable();
            _blocks.Add(factory());
            Rebuild();
            Emit();
        };
        return btn;
    }

    /// <summary>
    /// Before appending an app component, turn any still-whole pasted page (a full-document Raw block) into
    /// a composable <em>embedded-page</em> fragment (its styles scoped, its body wrapped). Otherwise the new
    /// component would compile to stray markup after the document's <c>&lt;/html&gt;</c> and render outside the
    /// page. This is the "combine both ways" step — see <see cref="HtmlCompiler.EmbedDocument"/>.
    /// </summary>
    private void EnsureComposable()
    {
        for (var i = 0; i < _blocks.Count; i++)
            if (_blocks[i] is HtmlBlock.Raw raw && HtmlCompiler.IsFullDocument(raw.Html))
                _blocks[i] = raw with { Html = HtmlCompiler.EmbedDocument(raw.Html) };
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

    /// <summary>
    /// Opens the edit surface for the element the author clicked in the preview, addressed by its DOM
    /// <paramref name="elementId"/>. A top-level block opens its own editor (the rich modal picker for an
    /// ECG / ECG segment, otherwise scroll-and-focus its card); an id belonging to a <em>nested</em>
    /// element (e.g. an ECG inside a card/section) opens that element in place via its owning block's
    /// structure node. No-op if the id resolves to nothing current.
    /// </summary>
    public async void EditElementById(string elementId)
    {
        // A top-level block carries this id directly.
        if (_blocks.FirstOrDefault(b => b.Id == elementId) is { } block)
        {
            ScrollToBlock(elementId);
            FlashCard(elementId);
            switch (block)
            {
                case HtmlBlock.Ecg ecg:
                    if (await PickEcgAsync(ecg) is { } e) ReplaceAndRebuild(elementId, e with { Id = elementId });
                    break;
                case HtmlBlock.EcgSegment seg:
                    if (await PickEcgSegmentAsync(seg) is { } s) ReplaceAndRebuild(elementId, s with { Id = elementId });
                    break;
                default:
                    DispatcherQueue.TryEnqueue(() => FocusFirstField(elementId));
                    break;
            }
            return;
        }

        // Otherwise the id belongs to a nested element inside some block's body (e.g. an ECG inside a
        // card/section): find the owning block and edit that element in place via its structure node.
        foreach (var owner in _blocks)
        {
            if (BodyHtmlOf(owner) is not { } body) continue;
            if (HtmlStructure.NodeById(body, elementId) is not { } node) continue;
            ScrollToBlock(owner.Id);
            FlashCard(owner.Id);
            await EditNodeAsync(owner.Id, node);
            return;
        }
    }

    /// <summary>Briefly outlines a block's card so a preview click visibly lands on the right editor.</summary>
    private void FlashCard(string blockId)
    {
        if (_cards.GetValueOrDefault(blockId) is not Border card) return;
        var original = card.BorderBrush;
        card.BorderBrush = new SolidColorBrush(Colors.SteelBlue);
        card.BorderThickness = new Thickness(2);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1100);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            card.BorderBrush = original;
            card.BorderThickness = new Thickness(1);
        };
        timer.Start();
    }

    /// <summary>Puts the caret in a block card's first text field, so a preview click lands ready to type.</summary>
    private void FocusFirstField(string blockId)
    {
        if (_cards.GetValueOrDefault(blockId) is { } card && FirstTextBox(card) is { } box)
            box.Focus(FocusState.Programmatic);
    }

    private static TextBox? FirstTextBox(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox tb) return tb;
            if (FirstTextBox(child) is { } found) return found;
        }
        return null;
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
            HtmlBlock.EcgSegment seg => BuildEcgSegmentEditor(seg),
            HtmlBlock.Table t => BuildTableEditor(t),
            HtmlBlock.List l => BuildListEditor(l),
            HtmlBlock.Quote q => BuildQuoteEditor(q),
            HtmlBlock.Note n => BuildNoteEditor(n),
            HtmlBlock.Card c => BuildCardEditor(c),
            HtmlBlock.Section s => BuildSectionEditor(s),
            HtmlBlock.Figure f => BuildFigureEditor(f),
            HtmlBlock.Divider => BuildDividerEditor(),
            HtmlBlock.Container ct => BuildContainerEditor(ct),
            HtmlBlock.Raw r => BuildRawEditor(r),
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

    // ── Structural component editors ────────────────────────────────────────────

    private FrameworkElement BuildListEditor(HtmlBlock.List block)
    {
        var stack = new StackPanel { Spacing = 4 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(TypeLabel("LIST"));
        var numbered = new CheckBox { Content = "Numbered", IsChecked = block.Numbered };
        numbered.Checked += (_, _) => { if (Cur<HtmlBlock.List>(block.Id) is { } c) Replace(block.Id, c with { Numbered = true }); };
        numbered.Unchecked += (_, _) => { if (Cur<HtmlBlock.List>(block.Id) is { } c) Replace(block.Id, c with { Numbered = false }); };
        top.Children.Add(numbered);
        stack.Children.Add(top);

        var items = new TextBox
        {
            Text = string.Join("\n", block.Items),
            PlaceholderText = "One item per line…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
        };
        items.TextChanged += (_, _) =>
        {
            if (Cur<HtmlBlock.List>(block.Id) is { } cur)
                Replace(block.Id, cur with { Items = items.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToList() });
        };
        stack.Children.Add(items);
        return stack;
    }

    private FrameworkElement BuildQuoteEditor(HtmlBlock.Quote block)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(TypeLabel("QUOTE"));
        var body = new TextBox { Text = block.Html, PlaceholderText = "Quote (text or simple HTML)…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56 };
        body.TextChanged += (_, _) => { if (Cur<HtmlBlock.Quote>(block.Id) is { } c) Replace(block.Id, c with { Html = body.Text }); };
        stack.Children.Add(body);
        return stack;
    }

    private FrameworkElement BuildNoteEditor(HtmlBlock.Note block)
    {
        var stack = new StackPanel { Spacing = 4 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(TypeLabel("NOTE"));
        var variant = new ComboBox { MinWidth = 120 };
        foreach (var v in HtmlComponents.NoteVariants) variant.Items.Add(v);
        variant.SelectedItem = HtmlComponents.NoteVariants.Contains(block.Variant) ? block.Variant : "info";
        variant.SelectionChanged += (_, _) =>
        {
            if (Cur<HtmlBlock.Note>(block.Id) is { } c) Replace(block.Id, c with { Variant = variant.SelectedItem as string ?? "info" });
        };
        top.Children.Add(variant);
        stack.Children.Add(top);
        stack.Children.Add(BuildBodyStructureEditor(block.Id, block.Html));
        return stack;
    }

    private FrameworkElement BuildCardEditor(HtmlBlock.Card block)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(TypeLabel("CARD"));
        var title = new TextBox { Text = block.Title, PlaceholderText = "Title (optional)", FontWeight = FontWeights.SemiBold };
        title.TextChanged += (_, _) => { if (Cur<HtmlBlock.Card>(block.Id) is { } c) Replace(block.Id, c with { Title = title.Text }); };
        stack.Children.Add(title);
        stack.Children.Add(BuildBodyStructureEditor(block.Id, block.Html));
        return stack;
    }

    private FrameworkElement BuildSectionEditor(HtmlBlock.Section block)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(TypeLabel("SECTION"));
        var title = new TextBox { Text = block.Title, PlaceholderText = "Section title (optional)", FontWeight = FontWeights.SemiBold };
        title.TextChanged += (_, _) => { if (Cur<HtmlBlock.Section>(block.Id) is { } c) Replace(block.Id, c with { Title = title.Text }); };
        stack.Children.Add(title);
        stack.Children.Add(BuildBodyStructureEditor(block.Id, block.Html));
        return stack;
    }

    private FrameworkElement BuildFigureEditor(HtmlBlock.Figure block)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(TypeLabel("FIGURE"));
        var caption = new TextBox { Header = "Caption", Text = block.Caption };
        caption.TextChanged += (_, _) => { if (Cur<HtmlBlock.Figure>(block.Id) is { } c) Replace(block.Id, c with { Caption = caption.Text }); };
        stack.Children.Add(caption);
        stack.Children.Add(BuildBodyStructureEditor(block.Id, block.Html));
        return stack;
    }

    private FrameworkElement BuildDividerEditor()
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(TypeLabel("DIVIDER"));
        stack.Children.Add(new TextBlock { Text = "Horizontal rule (no content to edit).", Opacity = 0.6, FontSize = 12 });
        return stack;
    }

    // ── Raw (opaque / nested) block ────────────────────────────────────────────

    /// <summary>How an inserted component relates to the picked structure node.</summary>
    private enum Placement { Replace, Before, After, Inside }

    /// <summary>App components that can be inserted into a Raw (HTML) block's structure.</summary>
    private enum ComponentKind { Header, Text, List, Quote, Note, Card, Section, Figure, Image, Ecg, EcgSegment, Table, Math, Divider }

    /// <summary>Component kinds in menu order, with their display labels.</summary>
    private static readonly (ComponentKind Kind, string Label)[] InsertableComponents =
    {
        (ComponentKind.Text, "Text"),
        (ComponentKind.Header, "Heading"),
        (ComponentKind.List, "List"),
        (ComponentKind.Quote, "Quote"),
        (ComponentKind.Note, "Note / callout"),
        (ComponentKind.Card, "Card"),
        (ComponentKind.Section, "Section"),
        (ComponentKind.Figure, "Figure"),
        (ComponentKind.Image, "Image"),
        (ComponentKind.Ecg, "ECG"),
        (ComponentKind.EcgSegment, "ECG segment"),
        (ComponentKind.Table, "Table"),
        (ComponentKind.Math, "Math"),
        (ComponentKind.Divider, "Divider"),
    };

    /// <summary>
    /// Editor for an opaque <see cref="HtmlBlock.Raw"/> block (nested/unknown markup or a whole pasted
    /// document): a navigable tree of the block's inner DOM where any element can be replaced with — or
    /// have an ECG inserted before/after it — plus the raw HTML for power users. This is what lets an
    /// author drill into pasted markup and swap a nested element (e.g. a hand-drawn <c>&lt;path&gt;</c>
    /// or its parent <c>&lt;svg&gt;</c>) for a real <c>&lt;ecg&gt;</c> reference.
    /// </summary>
    private FrameworkElement BuildRawEditor(HtmlBlock.Raw block)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(TypeLabel("HTML BLOCK"));
        stack.Children.Add(BuildBodyStructureEditor(block.Id, block.Html));
        return stack;
    }

    private FrameworkElement BuildContainerEditor(HtmlBlock.Container block)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(TypeLabel("CONTAINER"));
        stack.Children.Add(BuildBodyStructureEditor(block.Id, block.Html));
        return stack;
    }

    /// <summary>
    /// The shared "structure tree" editor for any block that owns an HTML body (Raw / Container / Card /
    /// Section / Note / Figure): a navigable tree of the body's DOM where any element can be replaced with —
    /// or have a component inserted inside/before/after it — via right-click, a top-level <b>＋ Insert</b>
    /// that adds a component to the body (works even when the body is empty, so you can nest from scratch).
    /// All edits route through <see cref="BodyHtmlOf(string)"/> / <see cref="SetBodyHtmlAndRebuild"/>, so they
    /// apply to whichever block type owns the body.
    /// </summary>
    private FrameworkElement BuildBodyStructureEditor(string blockId, string bodyHtml)
    {
        var stack = new StackPanel { Spacing = 6 };

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerRow.Children.Add(new TextBlock
        {
            Text = "Structure — right-click an element to insert/replace a component:",
            FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var addBtn = new Button { Content = "＋ Insert ▾", Padding = new Thickness(8, 2, 8, 2) };
        var addMenu = new MenuFlyout();
        foreach (var (kind, label) in InsertableComponents)
        {
            var captured = kind;
            var item = new MenuFlyoutItem { Text = label + "…" };
            item.Click += async (_, _) => await AppendComponentToBodyAsync(blockId, captured);
            addMenu.Items.Add(item);
        }
        addBtn.Flyout = addMenu;
        headerRow.Children.Add(addBtn);
        stack.Children.Add(headerRow);

        var treeHost = new StackPanel { Spacing = 0 };
        var outline = HtmlStructure.Outline(bodyHtml);
        if (outline.Count == 0)
        {
            treeHost.Children.Add(new TextBlock
            {
                Text = "(empty — use ＋ Insert to add a component)",
                Opacity = 0.6, FontSize = 12, Margin = new Thickness(4),
            });
        }
        else
        {
            var selection = new TreeRowSelection();
            foreach (var node in outline) AddTreeRow(treeHost, node, 0, blockId, selection);
        }
        stack.Children.Add(new ScrollViewer
        {
            Content = treeHost,
            HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 300,
        });

        return stack;
    }

    // ── Generic "body HTML" access for container-ish blocks ─────────────────────

    /// <summary>The editable HTML body of a container-ish block (one whose content is a structure tree), or
    /// null for a block with no such body.</summary>
    private static string? BodyHtmlOf(HtmlBlock block) => block switch
    {
        HtmlBlock.Raw r => r.Html,
        HtmlBlock.Container c => c.Html,
        HtmlBlock.Card c => c.Html,
        HtmlBlock.Section s => s.Html,
        HtmlBlock.Note n => n.Html,
        HtmlBlock.Figure f => f.Html,
        _ => null,
    };

    private string? BodyHtmlOf(string blockId) =>
        _blocks.FirstOrDefault(b => b.Id == blockId) is { } b ? BodyHtmlOf(b) : null;

    private static HtmlBlock WithBodyHtml(HtmlBlock block, string html) => block switch
    {
        HtmlBlock.Raw r => r with { Html = html },
        HtmlBlock.Container c => c with { Html = html },
        HtmlBlock.Card c => c with { Html = html },
        HtmlBlock.Section s => s with { Html = html },
        HtmlBlock.Note n => n with { Html = html },
        HtmlBlock.Figure f => f with { Html = html },
        _ => block,
    };

    /// <summary>Updates a block's body HTML and rebuilds its card (for tree operations that change structure).</summary>
    private void SetBodyHtmlAndRebuild(string blockId, string html)
    {
        var idx = _blocks.FindIndex(b => b.Id == blockId);
        if (idx < 0) return;
        _blocks[idx] = WithBodyHtml(_blocks[idx], html);
        Rebuild();
        Emit();
    }

    /// <summary>Adds a component at the top level of a block's body (works when empty — the entry point for
    /// building a nested structure from scratch).</summary>
    private async Task AppendComponentToBodyAsync(string blockId, ComponentKind kind)
    {
        if (BodyHtmlOf(blockId) is null) return;
        var markup = await BuildComponentMarkupAsync(kind, replacing: false);
        if (string.IsNullOrEmpty(markup)) return;
        if (BodyHtmlOf(blockId) is not { } current) return;
        SetBodyHtmlAndRebuild(blockId, HtmlStructure.AppendToRoot(current, markup));
    }

    /// <summary>Renders one component row (kind dot + friendly label + preview) and, recursively, its
    /// children under a collapsible host. The row is clickable (hover + click-to-select highlight) and
    /// right-click (ContextFlyout) offers replace / insert ECG.</summary>
    private void AddTreeRow(StackPanel host, HtmlStructure.HtmlStructureNode node, int depth, string blockId, TreeRowSelection selection)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Thickness(depth * 14 + 2, 2, 6, 2),
            // A transparent background makes the whole row hit-testable for the right-click menu.
            Background = new SolidColorBrush(Colors.Transparent),
        };

        var childHost = new StackPanel();

        if (node.Children.Count > 0)
        {
            var expanded = depth < 2; // reveal the top couple of levels; deeper stays collapsed
            var chevron = new FontIcon { Glyph = expanded ? "" : "", FontSize = 11 };
            var toggle = new Button { Content = chevron, Padding = new Thickness(4, 0, 4, 0) };
            childHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            toggle.Click += (_, _) =>
            {
                expanded = !expanded;
                chevron.Glyph = expanded ? "" : "";
                childHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            };
            row.Children.Add(toggle);
        }
        else
        {
            row.Children.Add(new Border { Width = 24 }); // align leaves under the chevron column
        }

        // Kind indicator (colour) + friendly component label + short content preview.
        row.Children.Add(new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(KindColor(node.Kind)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = node.Label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        });
        if (!string.IsNullOrEmpty(node.Preview))
        {
            row.Children.Add(new TextBlock
            {
                Text = node.Preview,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                Opacity = 0.6,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        // Wrap the row so it can be clicked/highlighted and carry the right-click menu (insert/replace with
        // any app component, at this element — bubbles up via ContextFlyout).
        var rowBorder = new Border
        {
            Child = row,
            Background = TreeRowSelection.Idle,
            CornerRadius = new CornerRadius(4),
        };
        // Build the (large) component menu lazily on right-click / long-press, not eagerly for every row.
        rowBorder.ContextRequested += (_, e) =>
        {
            var menu = BuildComponentMenu(blockId, node);
            var opts = new FlyoutShowOptions();
            if (e.TryGetPosition(rowBorder, out var pos)) opts.Position = pos;
            menu.ShowAt(rowBorder, opts);
            e.Handled = true;
        };
        rowBorder.PointerEntered += (_, _) => selection.SetHover(rowBorder, true);
        rowBorder.PointerExited += (_, _) => selection.SetHover(rowBorder, false);
        rowBorder.Tapped += (_, e) =>
        {
            selection.Select(rowBorder);
            RaiseElementSelected(blockId, node.Path);
            e.Handled = true; // element-level scroll wins over the card-level BlockFocused sync
        };

        host.Children.Add(rowBorder);

        foreach (var child in node.Children) AddTreeRow(childHost, child, depth + 1, blockId, selection);
        host.Children.Add(childHost);
    }

    /// <summary>Translates a selected tree node into a preview scroll target, always walked from
    /// <c>document.body</c>. For a Raw block (whose body <b>is</b> the rendered top-level element) the scroll
    /// is exact: a standalone document's path already indexes <c>body.children</c>; a fragment Raw's root is
    /// <c>body.children[blockIndex]</c>, so its local root index (always 0) is dropped and the block index
    /// prepended. For a typed container block (Card/Section/…, whose body is nested inside a wrapper) the DOM
    /// path isn't 1:1, so we scroll to the block itself.</summary>
    private void RaiseElementSelected(string blockId, IReadOnlyList<int> path)
    {
        if (ElementSelected is null) return;
        var idx = _blocks.FindIndex(b => b.Id == blockId);
        if (idx < 0) return;

        if (_blocks[idx] is HtmlBlock.Raw raw)
        {
            IReadOnlyList<int> indices;
            if (HtmlCompiler.IsFullDocument(raw.Html))
            {
                indices = path; // path already indexes body.children of the standalone document
            }
            else
            {
                var walk = new List<int>(path.Count) { idx };
                walk.AddRange(path.Skip(1)); // drop the block-local root index (always 0)
                indices = walk;
            }
            ElementSelected.Invoke(null, indices);
        }
        else
        {
            ElementSelected.Invoke(null, new[] { idx }); // typed container block → scroll to the block
        }
    }

    /// <summary>Tracks the highlighted structure-tree row so hover and click paint the right background
    /// (only one row selected at a time within a block's tree).</summary>
    private sealed class TreeRowSelection
    {
        private static readonly SolidColorBrush IdleBrush = new(Colors.Transparent);
        private static readonly SolidColorBrush HoverBrush = new(Windows.UI.Color.FromArgb(0x22, 0x80, 0x80, 0x80));
        private static readonly SolidColorBrush SelectedBrush = new(Windows.UI.Color.FromArgb(0x55, 0x46, 0x82, 0xB4));

        /// <summary>The neutral background a row starts with (also hit-testable, so pointer events fire).</summary>
        public static Brush Idle => IdleBrush;

        private Border? _selected;

        public void Select(Border row)
        {
            if (ReferenceEquals(_selected, row)) return;
            if (_selected is not null) _selected.Background = IdleBrush;
            _selected = row;
            row.Background = SelectedBrush;
        }

        public void SetHover(Border row, bool on)
        {
            if (ReferenceEquals(_selected, row)) return; // don't fight the selection highlight
            row.Background = on ? HoverBrush : IdleBrush;
        }
    }

    /// <summary>Accent colour for a component kind's row dot, so recognizable components read at a glance.</summary>
    private static Windows.UI.Color KindColor(HtmlStructure.HtmlNodeKind kind) => kind switch
    {
        HtmlStructure.HtmlNodeKind.Heading => Colors.SteelBlue,
        HtmlStructure.HtmlNodeKind.Text => Colors.Gray,
        HtmlStructure.HtmlNodeKind.Math => Colors.MediumPurple,
        HtmlStructure.HtmlNodeKind.Image => Colors.Teal,
        HtmlStructure.HtmlNodeKind.Ecg => Colors.Crimson,
        HtmlStructure.HtmlNodeKind.Table => Colors.SeaGreen,
        HtmlStructure.HtmlNodeKind.List => Colors.Goldenrod,
        HtmlStructure.HtmlNodeKind.Diagram => Colors.DarkOrange,
        HtmlStructure.HtmlNodeKind.Container => Colors.SlateGray,
        _ => Colors.DarkGray,
    };

    /// <summary>Builds the right-click "insert / replace with any app component" menu for a structure node.
    /// Insert is the primary (safe) action and comes first; "Insert inside" appends into a container so an
    /// author can add a component to a section without replacing it; "Replace with" is last (and, for a
    /// container, confirms first — replacing a section or the whole page would discard everything inside).</summary>
    private MenuFlyout BuildComponentMenu(string blockId, HtmlStructure.HtmlStructureNode node)
    {
        var flyout = new MenuFlyout();
        var edit = new MenuFlyoutItem { Text = "Edit…", Icon = new SymbolIcon(Symbol.Edit) };
        edit.Click += async (_, _) => await EditNodeAsync(blockId, node);
        flyout.Items.Add(edit);
        flyout.Items.Add(new MenuFlyoutSeparator());
        if (node.Children.Count > 0)
            flyout.Items.Add(BuildComponentSubmenu("Insert inside", blockId, node, Placement.Inside));
        flyout.Items.Add(BuildComponentSubmenu("Insert before", blockId, node, Placement.Before));
        flyout.Items.Add(BuildComponentSubmenu("Insert after", blockId, node, Placement.After));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(BuildComponentSubmenu("Replace with", blockId, node, Placement.Replace));
        var delete = new MenuFlyoutItem { Text = "Delete", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await DeleteNodeAsync(blockId, node);
        flyout.Items.Add(delete);
        return flyout;
    }

    /// <summary>Removes the element at <paramref name="node"/> from its block's body (confirming first when it
    /// is a container, since that discards everything inside it).</summary>
    private async Task DeleteNodeAsync(string blockId, HtmlStructure.HtmlStructureNode node)
    {
        if (BodyHtmlOf(blockId) is null) return;
        if (node.Children.Count > 0 && !await ConfirmDeleteAsync(node)) return;
        if (BodyHtmlOf(blockId) is not { } current) return;
        var newHtml = HtmlStructure.RemoveElement(current, node.Path);
        if (newHtml == current) return; // stale path — nothing changed
        SetBodyHtmlAndRebuild(blockId, newHtml);
    }

    private async Task<bool> ConfirmDeleteAsync(HtmlStructure.HtmlStructureNode node)
    {
        var dialog = new ContentDialog
        {
            RequestedTheme = AppTheme.Current,
            Title = "Delete element?",
            Content = $"“{node.Label}” and everything inside it will be deleted.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private MenuFlyoutSubItem BuildComponentSubmenu(string label, string blockId, HtmlStructure.HtmlStructureNode node, Placement placement)
    {
        var sub = new MenuFlyoutSubItem { Text = label };
        foreach (var (kind, kindLabel) in InsertableComponents)
        {
            var capturedKind = kind;
            var item = new MenuFlyoutItem { Text = kindLabel + "…" };
            item.Click += async (_, _) => await ApplyComponentAsync(blockId, node, placement, capturedKind);
            sub.Items.Add(item);
        }
        return sub;
    }

    /// <summary>Configures a component, compiles it to markup, and applies it to the Raw block at
    /// <paramref name="node"/> — inserting inside / before / after it, or replacing it — via a surgical
    /// <see cref="HtmlStructure"/> edit that leaves the rest of the markup untouched. Replacing a container
    /// (which discards its contents) is confirmed first.</summary>
    private async Task ApplyComponentAsync(string blockId, HtmlStructure.HtmlStructureNode node, Placement placement, ComponentKind kind)
    {
        if (BodyHtmlOf(blockId) is null) return;
        if (placement == Placement.Replace && node.Children.Count > 0 && !await ConfirmReplaceAsync(node)) return;

        var markup = await BuildComponentMarkupAsync(kind, placement == Placement.Replace);
        if (string.IsNullOrEmpty(markup)) return;
        if (BodyHtmlOf(blockId) is not { } current) return; // block may have changed while the dialog was open

        var newHtml = placement switch
        {
            Placement.Replace => HtmlStructure.ReplaceElement(current, node.Path, markup),
            Placement.Inside => HtmlStructure.AppendChild(current, node.Path, markup),
            _ => HtmlStructure.InsertAdjacent(current, node.Path, markup, after: placement == Placement.After),
        };
        if (newHtml == current) return; // stale path — nothing changed
        SetBodyHtmlAndRebuild(blockId, newHtml);
    }

    /// <summary>Edits the component at <paramref name="node"/> in place: the rich picker (pre-filled) for an
    /// ECG / ECG segment, or a raw-HTML editor for any other element. Replaces the node's markup on confirm,
    /// keeping its id.</summary>
    private async Task EditNodeAsync(string blockId, HtmlStructure.HtmlStructureNode node)
    {
        if (BodyHtmlOf(blockId) is not { } body) return;
        var outer = HtmlStructure.GetOuterHtml(body, node.Path);
        if (outer is null) return;

        var newMarkup = node.Tag switch
        {
            "ecgsegment" => HtmlCompiler.Parse(outer).FirstOrDefault() is HtmlBlock.EcgSegment seg
                ? (await PickEcgSegmentAsync(seg)) is { } s ? HtmlCompiler.BuildEcgSegmentTag(s) : null
                : null,
            "ecg" => HtmlCompiler.Parse(outer).FirstOrDefault() is HtmlBlock.Ecg ecg
                ? (await PickEcgAsync(ecg)) is { } e ? HtmlCompiler.BuildEcgTag(e) : null
                : null,
            _ => await EditRawHtmlAsync(node.Label, outer),
        };
        if (string.IsNullOrEmpty(newMarkup)) return;
        if (BodyHtmlOf(blockId) is not { } current) return;
        var updated = HtmlStructure.ReplaceElement(current, node.Path, newMarkup);
        if (updated == current) return;
        SetBodyHtmlAndRebuild(blockId, updated);
    }

    /// <summary>Generic fallback editor: the element's raw HTML in a text box.</summary>
    private async Task<string?> EditRawHtmlAsync(string label, string outer)
    {
        var box = new TextBox
        {
            Text = outer, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"), FontSize = 12, MinHeight = 160, Width = 460, IsSpellCheckEnabled = false,
        };
        if (!await ConfirmComponentDialogAsync($"Edit {label}", box) || string.IsNullOrWhiteSpace(box.Text)) return null;
        return box.Text;
    }

    /// <summary>Warns before replacing a container element (which removes everything inside it).</summary>
    private async Task<bool> ConfirmReplaceAsync(HtmlStructure.HtmlStructureNode node)
    {
        var dialog = new ContentDialog
        {
            RequestedTheme = AppTheme.Current,
            Title = "Replace element?",
            Content = $"“{node.Label}” and everything inside it will be replaced by the new component.",
            PrimaryButtonText = "Replace",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Prompts the author for a component's content and returns its compiled HTML (or null if
    /// cancelled / empty). ECG reuses the rich rhythm picker; the rest use small inline dialogs. The
    /// markup is produced by <see cref="HtmlCompiler.Compile"/> so it matches a top-level block exactly.</summary>
    private async Task<string?> BuildComponentMarkupAsync(ComponentKind kind, bool replacing)
    {
        var verb = replacing ? "Replace with" : "Insert";
        return kind switch
        {
            ComponentKind.Ecg => await PickEcgAsync() is { } e ? HtmlCompiler.BuildEcgTag(e) : null,
            ComponentKind.EcgSegment => await PickEcgSegmentAsync() is { } s ? HtmlCompiler.BuildEcgSegmentTag(s) : null,
            ComponentKind.Header => await PickHeaderMarkupAsync(verb),
            ComponentKind.Text => await PickTextMarkupAsync(verb),
            ComponentKind.Math => await PickMathMarkupAsync(verb),
            ComponentKind.Image => await PickImageMarkupAsync(verb),
            ComponentKind.Table => await PickTableMarkupAsync(verb),
            ComponentKind.List => await PickListMarkupAsync(verb),
            ComponentKind.Quote => await PickQuoteMarkupAsync(verb),
            ComponentKind.Note => await PickNoteMarkupAsync(verb),
            ComponentKind.Card => await PickCardMarkupAsync(verb, "card"),
            ComponentKind.Section => await PickCardMarkupAsync(verb, "section"),
            ComponentKind.Figure => await PickFigureMarkupAsync(verb),
            ComponentKind.Divider => HtmlComponents.Divider(), // no configuration
            _ => null,
        };
    }

    /// <summary>Compiles a single block to the same markup it would have as a top-level block.</summary>
    private static string Markup(HtmlBlock block) => HtmlCompiler.Compile(new[] { block });

    /// <summary>Shows a small OK/Cancel dialog around <paramref name="content"/>; true on OK.</summary>
    private async Task<bool> ConfirmComponentDialogAsync(string title, FrameworkElement content)
    {
        var dialog = new ContentDialog
        {
            RequestedTheme = AppTheme.Current,
            Title = title,
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 460 },
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<string?> PickHeaderMarkupAsync(string verb)
    {
        var level = new ComboBox { Header = "Level", MinWidth = 80 };
        for (var i = 1; i <= 6; i++) level.Items.Add($"H{i}");
        level.SelectedIndex = 1;
        var text = new TextBox { Header = "Heading text", IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
        var panel = new StackPanel { Spacing = 8, Width = 320 };
        panel.Children.Add(level);
        panel.Children.Add(text);
        if (!await ConfirmComponentDialogAsync($"{verb} heading", panel) || string.IsNullOrWhiteSpace(text.Text)) return null;
        return Markup(new HtmlBlock.Header(level.SelectedIndex + 1, text.Text));
    }

    private async Task<string?> PickTextMarkupAsync(string verb)
    {
        var box = new TextBox
        {
            Header = "Text or simple HTML",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 120,
            Width = 340,
            IsSpellCheckEnabled = false,
        };
        if (!await ConfirmComponentDialogAsync($"{verb} text", box) || string.IsNullOrWhiteSpace(box.Text)) return null;
        return Markup(new HtmlBlock.Paragraph(box.Text));
    }

    private async Task<string?> PickMathMarkupAsync(string verb)
    {
        var expr = new TextBox
        {
            Header = "LaTeX expression",
            PlaceholderText = "e.g. E = mc^2",
            FontFamily = new FontFamily("Consolas"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = 340,
            IsSpellCheckEnabled = false,
        };
        var display = new CheckBox { Content = "Display mode", IsChecked = true };
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(expr);
        panel.Children.Add(display);
        if (!await ConfirmComponentDialogAsync($"{verb} math", panel) || string.IsNullOrWhiteSpace(expr.Text)) return null;
        return Markup(new HtmlBlock.KaTeX(expr.Text, display.IsChecked == true));
    }

    private async Task<string?> PickImageMarkupAsync(string verb)
    {
        string? dataUri = null;
        var status = new TextBlock { Text = "No image", Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var urlBox = new TextBox { Header = "Image URL", PlaceholderText = "https://…", Width = 340 };
        var caption = new TextBox { Header = "Caption", Width = 340, IsSpellCheckEnabled = false };
        var browse = new Button { Content = "Browse image…", IsEnabled = _pickImage is not null };
        browse.Click += async (_, _) =>
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
            dataUri = $"data:{ImageMimeFromExtension(file.FileType)};base64,{Convert.ToBase64String(bytes)}";
            status.Text = "Image embedded (file loaded)";
            urlBox.Text = string.Empty;
        };
        var browseRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        browseRow.Children.Add(browse);
        browseRow.Children.Add(status);
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(browseRow);
        panel.Children.Add(urlBox);
        panel.Children.Add(caption);
        if (!await ConfirmComponentDialogAsync($"{verb} image", panel)) return null;
        var src = dataUri ?? urlBox.Text;
        if (string.IsNullOrWhiteSpace(src)) return null;
        return Markup(new HtmlBlock.Image(src, caption.Text));
    }

    private async Task<string?> PickTableMarkupAsync(string verb)
    {
        var rows = new NumberBox { Header = "Rows", Value = 2, Minimum = 1, Maximum = 30, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var cols = new NumberBox { Header = "Columns", Value = 2, Minimum = 1, Maximum = 12, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var panel = new StackPanel { Spacing = 8, Width = 240 };
        panel.Children.Add(rows);
        panel.Children.Add(cols);
        if (!await ConfirmComponentDialogAsync($"{verb} table", panel)) return null;
        var r = Math.Clamp((int)(double.IsNaN(rows.Value) ? 2 : rows.Value), 1, 30);
        var c = Math.Clamp((int)(double.IsNaN(cols.Value) ? 2 : cols.Value), 1, 12);
        var grid = Enumerable.Range(0, r)
            .Select(_ => (IReadOnlyList<string>)Enumerable.Repeat(string.Empty, c).ToList())
            .ToList();
        return Markup(new HtmlBlock.Table(grid));
    }

    // ── Structural components (Card / Section / List / Note / Quote / Figure) ─────

    private static TextBox ComponentTextBox(string header, string? placeholder = null, double minHeight = 0) => new()
    {
        Header = header,
        PlaceholderText = placeholder,
        AcceptsReturn = minHeight > 0,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = minHeight,
        Width = 340,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
    };

    private async Task<string?> PickListMarkupAsync(string verb)
    {
        var items = ComponentTextBox("List items (one per line)", "First item\nSecond item", 120);
        var numbered = new CheckBox { Content = "Numbered" };
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(items);
        panel.Children.Add(numbered);
        if (!await ConfirmComponentDialogAsync($"{verb} list", panel)) return null;
        var lines = (items.Text ?? string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        return lines.Count == 0 ? null : HtmlComponents.List(lines, numbered.IsChecked == true);
    }

    private async Task<string?> PickQuoteMarkupAsync(string verb)
    {
        var body = ComponentTextBox("Quote", minHeight: 96);
        var cite = ComponentTextBox("Attribution (optional)");
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(body);
        panel.Children.Add(cite);
        if (!await ConfirmComponentDialogAsync($"{verb} quote", panel) || string.IsNullOrWhiteSpace(body.Text)) return null;
        return HtmlComponents.Quote(body.Text, cite.Text);
    }

    private async Task<string?> PickNoteMarkupAsync(string verb)
    {
        var variant = new ComboBox { Header = "Style", Width = 200 };
        foreach (var v in HtmlComponents.NoteVariants) variant.Items.Add(v);
        variant.SelectedIndex = 0;
        var body = ComponentTextBox("Note text", minHeight: 96);
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(variant);
        panel.Children.Add(body);
        if (!await ConfirmComponentDialogAsync($"{verb} note", panel) || string.IsNullOrWhiteSpace(body.Text)) return null;
        return HtmlComponents.Note(variant.SelectedItem as string ?? "info", body.Text);
    }

    /// <summary>Shared picker for Card and Section (title + body); <paramref name="shape"/> selects which.</summary>
    private async Task<string?> PickCardMarkupAsync(string verb, string shape)
    {
        var title = ComponentTextBox("Title (optional)");
        var body = ComponentTextBox("Body (text or simple HTML)", minHeight: 120);
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(title);
        panel.Children.Add(body);
        if (!await ConfirmComponentDialogAsync($"{verb} {shape}", panel)) return null;
        if (string.IsNullOrWhiteSpace(title.Text) && string.IsNullOrWhiteSpace(body.Text)) return null;
        return shape == "section"
            ? HtmlComponents.Section(title.Text, body.Text)
            : HtmlComponents.Card(title.Text, body.Text);
    }

    private async Task<string?> PickFigureMarkupAsync(string verb)
    {
        var body = ComponentTextBox("Content (text/HTML — or leave empty and insert an image/ECG inside later)", minHeight: 96);
        var caption = ComponentTextBox("Caption");
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(body);
        panel.Children.Add(caption);
        if (!await ConfirmComponentDialogAsync($"{verb} figure", panel)) return null;
        var content = string.IsNullOrWhiteSpace(body.Text) ? "&nbsp;" : body.Text;
        return HtmlComponents.Figure(content, caption.Text);
    }

    /// <summary>
    /// Modal ECG builder: an embedded <see cref="RhythmChoosingPanel"/> (the same dataset picker used
    /// elsewhere) plus lead handpicks, a layout scheme, and a caption. Returns the composed
    /// <see cref="HtmlBlock.Ecg"/>, or null if cancelled / no rhythm chosen. The rhythm picker is
    /// embedded (not a nested dialog) because WinUI allows only one <see cref="ContentDialog"/> at a time.
    /// </summary>
    private async Task<HtmlBlock.Ecg?> PickEcgAsync(HtmlBlock.Ecg? initial = null)
    {
        if (_appVm is null) return null;

        var state = initial ?? new HtmlBlock.Ecg(string.Empty, Array.Empty<Lead>(), SeriesScheme.OneColumn, string.Empty);

        // Wide enough for the Width / Height / Alignment controls to sit on one row (see SizeSection).
        var panel = new StackPanel { Spacing = 10, Width = 460 };

        panel.Children.Add(new TextBlock { Text = AppStrings.EcgPickFromDataset, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        var rhythmPanel = new RhythmChoosingPanel
        {
            DisplayLanguage = _appVm.SelectedLanguage,
            ShowPinButton = false,
            Width = 320,
            Height = 220,
        };
        rhythmPanel.SetRhythms(_rhythms);
        if (!string.IsNullOrEmpty(state.Pathology)) rhythmPanel.SelectedId = state.Pathology;
        panel.Children.Add(rhythmPanel);

        panel.Children.Add(new TextBlock { Text = AppStrings.EcgPickLeadsHeader, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        var leadGrid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (var c = 0; c < 6; c++) leadGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leadGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leadGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < Leads.All.Count; i++)
        {
            var lead = Leads.All[i];
            // Seed IsChecked before wiring so it doesn't fire the handler / disturb `state`.
            var toggle = new ToggleButton { Content = lead.ToString(), MinWidth = 48, Padding = new Thickness(4, 2, 4, 2), IsChecked = state.Leads.Contains(lead) };
            void OnToggle()
            {
                var set = new SortedSet<Lead>(state.Leads);
                if (toggle.IsChecked == true) set.Add(lead); else set.Remove(lead);
                state = state with { Leads = set.ToList() };
            }
            toggle.Checked += (_, _) => OnToggle();
            toggle.Unchecked += (_, _) => OnToggle();
            Grid.SetColumn(toggle, i % 6);
            Grid.SetRow(toggle, i / 6);
            leadGrid.Children.Add(toggle);
        }
        panel.Children.Add(leadGrid);

        var schemeCombo = new ComboBox { Header = AppStrings.EcgPickLayout, Width = 160 };
        schemeCombo.Items.Add(AppStrings.EcgPickLayoutOneColumn);
        schemeCombo.Items.Add(AppStrings.EcgPickLayoutTwoColumns);
        schemeCombo.Items.Add(AppStrings.EcgPickLayoutGrid);
        schemeCombo.SelectedIndex = state.Scheme switch { SeriesScheme.TwoColumn => 1, SeriesScheme.Grid => 2, _ => 0 };
        schemeCombo.SelectionChanged += (_, _) => state = state with
        {
            Scheme = schemeCombo.SelectedIndex switch { 1 => SeriesScheme.TwoColumn, 2 => SeriesScheme.Grid, _ => SeriesScheme.OneColumn },
        };
        panel.Children.Add(schemeCombo);

        // Display filter — the monitor's bands, applied to every displayed lead of the embedded ECG.
        var filterOptions = new[] { EcgFilterType.None, EcgFilterType.Lowpass, EcgFilterType.Highpass, EcgFilterType.Bandpass };
        var filterCombo = new ComboBox { Header = AppStrings.MonitorFilters, Width = 200 };
        foreach (var f in filterOptions) filterCombo.Items.Add(FilterLabel(f));
        filterCombo.SelectedIndex = Math.Max(0, Array.IndexOf(filterOptions, state.Filter));
        filterCombo.SelectionChanged += (_, _) => state = state with { Filter = filterOptions[Math.Max(0, filterCombo.SelectedIndex)] };
        panel.Children.Add(filterCombo);

        // Display size (CSS px), empty = auto; plus horizontal placement within the parent. Same controls the
        // top-level block card offers, so a nested embed (edited through this modal) can be sized/aligned too.
        panel.Children.Add(SizeSection(state.WidthPx, state.HeightPx, state.Align,
            v => state = state with { WidthPx = v },
            v => state = state with { HeightPx = v },
            a => state = state with { Align = a }));

        var caption = new TextBox { Header = AppStrings.EcgPickCaption, IsSpellCheckEnabled = false, Text = state.Caption };
        caption.TextChanged += (_, _) => state = state with { Caption = caption.Text };
        panel.Children.Add(caption);

        var dialog = new ContentDialog
        {
            RequestedTheme = AppTheme.Current,
            Title = initial is null ? AppStrings.EcgPickTitleInsert : AppStrings.EcgPickTitleEdit,
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 480,
            },
            PrimaryButtonText = initial is null ? AppStrings.EcgPickInsert : AppStrings.EcgPickApply,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = !string.IsNullOrEmpty(state.Pathology), // enabled once a rhythm is chosen
        };
        // The wider panel (single Width/Height/Alignment row) needs more than the default ~548px dialog width.
        dialog.Resources["ContentDialogMaxWidth"] = 620.0;
        rhythmPanel.RhythmSelected += (_, entry) =>
        {
            state = state with { Pathology = entry.Id };
            dialog.IsPrimaryButtonEnabled = true;
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return string.IsNullOrEmpty(state.Pathology) ? null : state;
    }

    // ── ECG segment (a windowed strip of one lead of a real pathology) ──────────

    /// <summary>Sample rate of the pathology dataset (mirrors <c>EcgCalibration.SampleRateHz</c> / the renderer).</summary>
    private const float SegmentSampleRate = 500f;

    /// <summary>Localized combo label for a segment display filter — reuses the monitor's filter names so the
    /// bands read identically wherever they appear.</summary>
    private static string FilterLabel(EcgFilterType filter) => filter switch
    {
        EcgFilterType.Lowpass => AppStrings.MonitorFilterNameLp,
        EcgFilterType.Highpass => AppStrings.MonitorFilterNameHp,
        EcgFilterType.Bandpass => AppStrings.MonitorFilterNameBp,
        _ => AppStrings.MonitorFilterNameNone,
    };

    private string SegmentSummary(HtmlBlock.EcgSegment b)
    {
        var rhythm = _rhythms.FirstOrDefault(r => r.Id == b.Pathology);
        var name = string.IsNullOrEmpty(b.Pathology) ? "(no rhythm)"
            : rhythm is null ? b.Pathology
            : (_appVm?.SelectedLanguage == DomainLanguage.RU ? (rhythm.ResolvedNameRu ?? rhythm.TitleEn) : rhythm.TitleEn);
        var tips = b.Tips.Count > 0 ? $", {b.Tips.Count} tip(s)" : string.Empty;
        var size = b.WidthPx is not null || b.HeightPx is not null
            ? $" · {(b.WidthPx?.ToString() ?? "auto")}×{(b.HeightPx?.ToString() ?? "auto")}px"
            : string.Empty;
        return $"{name} · lead {b.Lead} · {b.StartSec:0.##}–{(b.StartSec + b.DurationSec):0.##}s{tips}{size}";
    }

    private FrameworkElement BuildEcgSegmentEditor(HtmlBlock.EcgSegment block)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(TypeLabel("ECG SEGMENT"));
        stack.Children.Add(new TextBlock { Text = SegmentSummary(block), Opacity = 0.8, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var edit = new Button { Content = AppStrings.SegEditRangeTips, HorizontalAlignment = HorizontalAlignment.Stretch };
        edit.Click += async (_, _) =>
        {
            if (Cur<HtmlBlock.EcgSegment>(block.Id) is not { } cur) return;
            var updated = await PickEcgSegmentAsync(cur);
            if (updated is not null) ReplaceAndRebuild(block.Id, updated with { Id = block.Id });
        };
        stack.Children.Add(edit);

        // Display size (CSS px). Empty = "auto" (intrinsic size from duration/amplitude). Width and height
        // are independent, so a non-proportional pair stretches the strip. Live-updates the preview.
        stack.Children.Add(new TextBlock { Text = AppStrings.SegWindowSize, FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.8 });
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sizeRow.Children.Add(SegmentSizeBox(AppStrings.SegWidthPx, block.WidthPx,
            v => { if (Cur<HtmlBlock.EcgSegment>(block.Id) is { } c) Replace(block.Id, c with { WidthPx = v }); }));
        sizeRow.Children.Add(SegmentSizeBox(AppStrings.SegHeightPx, block.HeightPx,
            v => { if (Cur<HtmlBlock.EcgSegment>(block.Id) is { } c) Replace(block.Id, c with { HeightPx = v }); }));
        sizeRow.Children.Add(AlignControl(block.Align,
            a => { if (Cur<HtmlBlock.EcgSegment>(block.Id) is { } c) Replace(block.Id, c with { Align = a }); }));
        stack.Children.Add(sizeRow);

        var caption = new TextBox { Header = AppStrings.SegCaption, Text = block.Caption, IsSpellCheckEnabled = false };
        caption.TextChanged += (_, _) => { if (Cur<HtmlBlock.EcgSegment>(block.Id) is { } c) Replace(block.Id, c with { Caption = caption.Text }); };
        stack.Children.Add(caption);
        return stack;
    }

    /// <summary>A compact px-size <see cref="NumberBox"/> for a segment's width/height. Empty shows the
    /// "auto" placeholder (intrinsic size) and reports null; a positive value reports that int.</summary>
    private static NumberBox SegmentSizeBox(string header, int? value, Action<int?> onChanged)
    {
        var box = new NumberBox
        {
            Header = header,
            Value = value ?? double.NaN, // NaN keeps the field empty so the placeholder shows
            PlaceholderText = AppStrings.SegSizeAuto,
            Minimum = 1,
            SmallChange = 10,
            LargeChange = 50,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 120,
        };
        box.ValueChanged += (_, e) =>
            onChanged(double.IsNaN(e.NewValue) || e.NewValue < 1 ? null : (int)Math.Round(e.NewValue));
        return box;
    }

    /// <summary>The "Window size" section (a header + a single Width / Height / Alignment row), shared by the
    /// ECG and ECG-segment <b>modal pickers</b> so an embed nested inside an HTML block (Card/Section/…) can be
    /// sized/aligned just like a top-level block — the block-editor cards build the same row inline.</summary>
    private static FrameworkElement SizeSection(
        int? width, int? height, EcgAlign align,
        Action<int?> onWidth, Action<int?> onHeight, Action<EcgAlign> onAlign)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = AppStrings.SegWindowSize, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(SegmentSizeBox(AppStrings.SegWidthPx, width, onWidth));
        row.Children.Add(SegmentSizeBox(AppStrings.SegHeightPx, height, onHeight));
        row.Children.Add(AlignControl(align, onAlign));
        stack.Children.Add(row);
        return stack;
    }

    /// <summary>A Left / Center / Right placement picker for an ECG figure within its parent block, shared by
    /// the block-editor cards and the modal pickers so alignment is set the same way everywhere.</summary>
    private static ComboBox AlignControl(EcgAlign current, Action<EcgAlign> onChanged)
    {
        var combo = new ComboBox { Header = AppStrings.SegAlignment, Width = 160 };
        combo.Items.Add(AppStrings.SegAlignLeft);
        combo.Items.Add(AppStrings.SegAlignCenter);
        combo.Items.Add(AppStrings.SegAlignRight);
        combo.SelectedIndex = current switch { EcgAlign.Center => 1, EcgAlign.Right => 2, _ => 0 };
        combo.SelectionChanged += (_, _) =>
            onChanged(combo.SelectedIndex switch { 1 => EcgAlign.Center, 2 => EcgAlign.Right, _ => EcgAlign.Left });
        return combo;
    }

    /// <summary>
    /// Modal ECG-segment builder: pick a rhythm, then <b>see the lead's real waveform</b> and drag the
    /// selection band to set the window, and drop guide lines / text labels / points onto it (via
    /// <see cref="SegmentRangeCanvas"/>). Returns the segment, or null if cancelled / no rhythm.
    /// </summary>
    private async Task<HtmlBlock.EcgSegment?> PickEcgSegmentAsync(HtmlBlock.EcgSegment? initial = null)
    {
        if (_appVm is null) return null;
        var state = initial ?? new HtmlBlock.EcgSegment(string.Empty, Lead.II, 0, HtmlCompiler.DefaultSegmentSeconds, string.Empty);

        var canvas = new SegmentRangeCanvas { Width = 620 };
        void LoadWaveform()
        {
            var raw = string.IsNullOrEmpty(state.Pathology)
                ? Array.Empty<float>()
                : _appVm.Repository.LeadWaveform(state.Pathology, state.Lead)?.Values ?? Array.Empty<float>();
            // Filter the full lead (not just the window) so the preview matches the rendered strip and the
            // absolute tip sample indices stay anchored.
            var values = EcgDisplayFilter.Filter(raw, state.Filter, SegmentSampleRate);
            canvas.Load(values, SegmentSampleRate,
                (int)Math.Round(state.StartSec * SegmentSampleRate),
                Math.Max(1, (int)Math.Round(state.DurationSec * SegmentSampleRate)), state.Tips);
        }
        canvas.RangeChanged += () => state = state with { StartSec = canvas.StartSec, DurationSec = canvas.DurationSec };
        canvas.TipsChanged += () => state = state with { Tips = canvas.Tips };

        // Two-column layout: the rhythm picker fills the tall left column; every other control
        // (lead / label / tools / zoom / canvas / caption) stacks in the right column below via `panel`.
        var leftColumn = new StackPanel { Spacing = 8, Width = 320 };
        leftColumn.Children.Add(new TextBlock { Text = AppStrings.SegFromDataset, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        var rhythmPanel = new RhythmChoosingPanel { DisplayLanguage = _appVm.SelectedLanguage, ShowPinButton = false, Width = 300, Height = 520 };
        rhythmPanel.SetRhythms(_rhythms);
        if (!string.IsNullOrEmpty(state.Pathology)) rhythmPanel.SelectedId = state.Pathology;
        leftColumn.Children.Add(rhythmPanel);

        var panel = new StackPanel { Spacing = 8, Width = 640 };

        // Lead selector + label text + clear-tips row (values/actions that aren't the pointer tool).
        var lead = new ComboBox { Header = AppStrings.SegLead, MinWidth = 90 };
        foreach (var l in Leads.All) lead.Items.Add(l.ToString());
        lead.SelectedItem = state.Lead.ToString();
        lead.SelectionChanged += (_, _) =>
        {
            if (Leads.FromToken(lead.SelectedItem as string ?? "II") is { } l) { state = state with { Lead = l }; LoadWaveform(); }
        };

        // Display filter — the same bands the Teaching monitor offers, applied to the strip so an author can
        // present a clean segment (baseline-wander / muscle-noise removed) instead of the raw recording.
        var filterOptions = new[] { EcgFilterType.None, EcgFilterType.Lowpass, EcgFilterType.Highpass, EcgFilterType.Bandpass };
        var filter = new ComboBox { Header = AppStrings.MonitorFilters, MinWidth = 150 };
        foreach (var f in filterOptions) filter.Items.Add(FilterLabel(f));
        filter.SelectedIndex = Math.Max(0, Array.IndexOf(filterOptions, state.Filter));
        filter.SelectionChanged += (_, _) =>
        {
            state = state with { Filter = filterOptions[Math.Max(0, filter.SelectedIndex)] };
            LoadWaveform();
        };

        var labelText = new TextBox { Header = AppStrings.SegLabelText, PlaceholderText = AppStrings.SegLabelTextHint, Width = 220, IsSpellCheckEnabled = false };
        labelText.TextChanged += (_, _) => canvas.LabelText = labelText.Text;
        var clear = new Button { Content = AppStrings.SegClearTips, VerticalAlignment = VerticalAlignment.Bottom };
        clear.Click += (_, _) => canvas.ClearTips();

        var optionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        optionsRow.Children.Add(lead);
        optionsRow.Children.Add(filter);
        optionsRow.Children.Add(labelText);
        optionsRow.Children.Add(clear);
        panel.Children.Add(optionsRow);

        // Tool palette: one button per canvas action (replaces the old drop-down). The buttons act as a
        // radio group — exactly one stays pressed, and pressing it sets the active pointer tool.
        panel.Children.Add(new TextBlock { Text = AppStrings.SegTool, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        var tools = new (string Tip, string Label, string Glyph, SegmentTool Tool)[]
        {
            (AppStrings.SegToolRangeTip, AppStrings.SegToolRange, "↔", SegmentTool.Range),
            (AppStrings.SegToolVLineTip, AppStrings.SegToolVLine, "│", SegmentTool.VerticalLine),
            (AppStrings.SegToolHLineTip, AppStrings.SegToolHLine, "─", SegmentTool.HorizontalLine),
            (AppStrings.SegToolLabelTip, AppStrings.SegToolLabel, "T", SegmentTool.Label),
            (AppStrings.SegToolPointTip, AppStrings.SegToolPoint, "●", SegmentTool.Point),
            (AppStrings.SegToolDeleteTip, AppStrings.SegToolDelete, "✕", SegmentTool.Delete),
            (AppStrings.SegToolPanTip, AppStrings.SegToolPan, "✥", SegmentTool.Pan),
            (AppStrings.SegToolCropTip, AppStrings.SegToolCrop, "▭", SegmentTool.Crop),
        };
        var toolButtons = new List<ToggleButton>();
        // 8 buttons don't fit one row inside the dialog — lay them out 4-across, 2 rows (like the leads grid).
        var toolPalette = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (var c = 0; c < 4; c++) toolPalette.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolPalette.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toolPalette.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var syncingTools = false;
        for (var i = 0; i < tools.Length; i++)
        {
            var (tip, label, glyph, t) = tools[i];
            var pick = t;
            var button = new ToggleButton
            {
                Content = $"{glyph}  {label}",
                MinWidth = 84,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 6, 8, 6),
                IsChecked = t == canvas.Tool,
            };
            ToolTipService.SetToolTip(button, tip);
            button.Checked += (_, _) =>
            {
                if (syncingTools) return;
                syncingTools = true;
                foreach (var other in toolButtons) other.IsChecked = ReferenceEquals(other, button);
                syncingTools = false;
                canvas.Tool = pick;
            };
            // Re-clicking the active tool must not clear the selection — keep one always pressed.
            button.Unchecked += (_, _) =>
            {
                if (syncingTools) return;
                syncingTools = true;
                button.IsChecked = true;
                syncingTools = false;
            };
            Grid.SetColumn(button, i % 4);
            Grid.SetRow(button, i / 4);
            toolButtons.Add(button);
            toolPalette.Children.Add(button);
        }
        panel.Children.Add(new Border
        {
            Child = toolPalette,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(6),
            Background = AppTheme.ControlFill,
            BorderBrush = AppTheme.ControlBorder,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        // Zoom / view controls (a view aid only — they never change the emitted segment).
        var zoomX = new Slider { Header = AppStrings.SegTimeScale, Minimum = 1, Maximum = 12, StepFrequency = 0.1, Value = 1, Width = 190 };
        var zoomY = new Slider { Header = AppStrings.SegAmplitudeScale, Minimum = 1, Maximum = 12, StepFrequency = 0.1, Value = 1, Width = 190 };
        var resetView = new Button { Content = AppStrings.SegResetView, VerticalAlignment = VerticalAlignment.Bottom };
        var syncingZoom = false;
        zoomX.ValueChanged += (_, e) => { if (!syncingZoom) canvas.SetZoomX(e.NewValue); };
        zoomY.ValueChanged += (_, e) => { if (!syncingZoom) canvas.SetZoomY(e.NewValue); };
        resetView.Click += (_, _) => canvas.ResetView();
        // A crop / reset changes the zoom from the canvas side — mirror it back onto the sliders.
        canvas.ViewChanged += () =>
        {
            syncingZoom = true;
            zoomX.Value = Math.Clamp(Math.Round(canvas.ZoomX, 1), zoomX.Minimum, zoomX.Maximum);
            zoomY.Value = Math.Clamp(Math.Round(canvas.ZoomY, 1), zoomY.Minimum, zoomY.Maximum);
            syncingZoom = false;
        };
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        zoomRow.Children.Add(zoomX);
        zoomRow.Children.Add(zoomY);
        zoomRow.Children.Add(resetView);
        panel.Children.Add(zoomRow);

        panel.Children.Add(new TextBlock
        {
            Text = AppStrings.SegHelp,
            FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(canvas);

        // Display size (CSS px), empty = auto; plus horizontal placement within the parent. Same controls the
        // top-level block card offers, so a nested embed (edited through this modal) can be sized/aligned too.
        panel.Children.Add(SizeSection(state.WidthPx, state.HeightPx, state.Align,
            v => state = state with { WidthPx = v },
            v => state = state with { HeightPx = v },
            a => state = state with { Align = a }));

        var caption = new TextBox { Header = AppStrings.SegCaption, Text = state.Caption, IsSpellCheckEnabled = false };
        caption.TextChanged += (_, _) => state = state with { Caption = caption.Text };
        panel.Children.Add(caption);

        // Place the rhythm picker (left) and the option/canvas stack (right) side by side.
        var columns = new Grid { ColumnSpacing = 16, VerticalAlignment = VerticalAlignment.Top };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(leftColumn, 0);
        Grid.SetColumn(panel, 1);
        columns.Children.Add(leftColumn);
        columns.Children.Add(panel);

        var dialog = new ContentDialog
        {
            RequestedTheme = AppTheme.Current,
            Title = AppStrings.SegTitle,
            Content = new ScrollViewer { Content = columns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 560 },
            PrimaryButtonText = initial is null ? AppStrings.SegInsert : AppStrings.SegApply,
            CloseButtonText = AppStrings.CommonCancel,
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = !string.IsNullOrEmpty(state.Pathology),
        };
        // Two columns need more room than the default ContentDialog max width (~548px).
        dialog.Resources["ContentDialogMaxWidth"] = 1040.0;
        rhythmPanel.RhythmSelected += (_, entry) =>
        {
            state = state with { Pathology = entry.Id };
            LoadWaveform();
            dialog.IsPrimaryButtonEnabled = true;
        };

        LoadWaveform();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        state = state with { StartSec = canvas.StartSec, DurationSec = canvas.DurationSec, Tips = canvas.Tips };
        return string.IsNullOrEmpty(state.Pathology) ? null : state;
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

        // ── Display size (CSS px) ─────────────────────────────────────────────
        // Empty = "auto" (intrinsic size from lead count/amplitude). Width and height are independent, so a
        // non-proportional pair stretches the figure. Live-updates the preview via Replace (no card rebuild).
        stack.Children.Add(new TextBlock { Text = "Window size", FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.8 });
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        sizeRow.Children.Add(SegmentSizeBox("Width (px)", block.WidthPx,
            v => { if (Cur<HtmlBlock.Ecg>(block.Id) is { } c) Replace(block.Id, c with { WidthPx = v }); }));
        sizeRow.Children.Add(SegmentSizeBox("Height (px)", block.HeightPx,
            v => { if (Cur<HtmlBlock.Ecg>(block.Id) is { } c) Replace(block.Id, c with { HeightPx = v }); }));
        sizeRow.Children.Add(AlignControl(block.Align,
            a => { if (Cur<HtmlBlock.Ecg>(block.Id) is { } c) Replace(block.Id, c with { Align = a }); }));
        stack.Children.Add(sizeRow);

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
            : (_appVm?.SelectedLanguage == DomainLanguage.RU ? (entry.ResolvedNameRu ?? entry.TitleEn) : entry.TitleEn);
    }

    /// <summary>Modal pathology picker (rhythm list only). Uses the same grouped-and-searchable
    /// <see cref="RhythmChoosingPanel"/> as the Teaching drawer. Returns the chosen id, or null.</summary>
    private async Task<string?> PickPathologyAsync(string? currentId)
    {
        if (_appVm is null) return null;
        string? selectedId = currentId;
        var panel = new RhythmChoosingPanel
        {
            DisplayLanguage = _appVm.SelectedLanguage,
            ShowPinButton = false, // pinning is meaningless in a modal picker
            Width = 320,
            Height = 420,
        };
        panel.SetRhythms(_rhythms);
        panel.SelectedId = currentId;
        var dialog = new ContentDialog
        {
            RequestedTheme = AppTheme.Current,
            Title = "Select rhythm",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = currentId is not null,
        };
        panel.RhythmSelected += (_, entry) =>
        {
            selectedId = entry.Id;
            dialog.IsPrimaryButtonEnabled = selectedId is not null;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return selectedId;
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
