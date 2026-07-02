using CardioSimulator.App.Localization;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
// FrameworkElement already has a string `Language` property that shadows the enum
// type name inside this control, so reference the enum through an alias.
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Searchable scrollable list of pathology titles, mirroring the Android
/// <c>RhythmChoosingPanel</c>: title is the RU name when the language is Russian
/// else the English title; the selected entry is shown in red. The list can be shown either
/// grouped by clinical category (<see cref="PathologyGroups"/>) or flat alphabetically (A–Z),
/// toggled by the header button. The panel can be pinned open via the header pin button.
/// </summary>
public sealed partial class RhythmChoosingPanel : UserControl
{
    // Segoe MDL2 Assets glyphs for the list-mode toggle button.
    private static readonly string GlyphGroups = char.ConvertFromUtf32(0xE8FD);       // BulletedList → grouped view
    private static readonly string GlyphAlphabetical = char.ConvertFromUtf32(0xE8CB); // Sort → alphabetical view

    private IReadOnlyList<PathologyEntry> _rhythms = Array.Empty<PathologyEntry>();
    private string? _selectedId;
    private bool _groupView = true;
    private bool _clinicalMode = false;

    /// <summary>Group keys the user has collapsed in the grouped view.</summary>
    private readonly HashSet<string> _collapsedGroups = new();

    public DomainLanguage DisplayLanguage { get; set; } = DomainLanguage.EN;

    /// <summary>Raised when the pin button toggles (Android <c>setDrawerFixed</c>).</summary>
    public event EventHandler<bool>? PinnedChanged;

    private bool _suppressPinEvent;

    /// <summary>Reflects the pin button without re-raising <see cref="PinnedChanged"/>.</summary>
    public bool Pinned
    {
        get => PinToggle.IsChecked == true;
        set
        {
            _suppressPinEvent = true;
            PinToggle.IsChecked = value;
            _suppressPinEvent = false;
        }
    }

    public string? SelectedId
    {
        get => _selectedId;
        set
        {
            if (_selectedId == value) return;
            _selectedId = value;
            Rebuild();
        }
    }

    public event EventHandler<PathologyEntry>? RhythmSelected;

    public RhythmChoosingPanel()
    {
        InitializeComponent();
        SearchBox.PlaceholderText = AppStrings.RhythmSearchPlaceholder;
        HeaderTitle.Text = AppStrings.EditorRhythmsTitle;
        ToolTipService.SetToolTip(PinToggle, AppStrings.FixDrawer);
        ToolTipService.SetToolTip(ClinicalToggle, AppStrings.ClinicalModeTooltip);
        UpdateSortToggleVisual();
    }

    private void OnPinChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_suppressPinEvent) return;
        PinnedChanged?.Invoke(this, PinToggle.IsChecked == true);
    }

    private void OnToggleSortClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _groupView = !_groupView;
        UpdateSortToggleVisual();
        Rebuild();
    }

    private void UpdateSortToggleVisual()
    {
        // The icon + tooltip reflect the view that is currently active.
        SortIcon.Glyph = _groupView ? GlyphGroups : GlyphAlphabetical;
        ToolTipService.SetToolTip(SortToggle,
            _groupView ? AppStrings.RhythmSortByGroup : AppStrings.RhythmSortAlphabetical);
    }

    public void SetRhythms(IReadOnlyList<PathologyEntry> rhythms)
    {
        _rhythms = rhythms;
        Rebuild();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Rebuild();

    private void OnToggleClinicalClick(object sender, RoutedEventArgs e)
    {
        _clinicalMode = ClinicalToggle.IsChecked == true;
        SortToggle.IsEnabled = !_clinicalMode;
        Rebuild();
    }

    private string TitleOf(PathologyEntry entry) =>
        DisplayLanguage == DomainLanguage.RU ? entry.NameRu ?? entry.TitleEn : entry.TitleEn;

    private string GetClinicalCaseTitle(PathologyEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ClinicalCase))
            return TitleOf(entry);

        var pairs = entry.ClinicalCase.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;
            var key = parts[0].Trim().ToLowerInvariant();
            if (key == "title" || key == "название" || key == "título" || key == "titulo" || key == "标题" || key == "शीर्षक")
            {
                return parts[1].Trim();
            }
        }
        return TitleOf(entry);
    }

    private void Rebuild()
    {
        // Refresh localized UI strings in case language changed
        SearchBox.PlaceholderText = AppStrings.RhythmSearchPlaceholder;
        HeaderTitle.Text = AppStrings.EditorRhythmsTitle;
        ToolTipService.SetToolTip(PinToggle, AppStrings.FixDrawer);
        ToolTipService.SetToolTip(ClinicalToggle, AppStrings.ClinicalModeTooltip);
        ClinicalDashboardHeader.Text = AppStrings.ClinicalDashboardTitle;
        UpdateSortToggleVisual();

        var query = SearchBox.Text ?? string.Empty;
        var list = _rhythms.AsEnumerable();
        if (_clinicalMode)
        {
            list = list.Where(r => !string.IsNullOrWhiteSpace(r.ClinicalCase));
        }

        var matches = list
            .Select(r => (entry: r, title: _clinicalMode ? GetClinicalCaseTitle(r) : TitleOf(r)))
            .Where(x => x.title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // If current selection is not in the filtered matches, select the first match or clear selection
        if (_selectedId is not null && !matches.Any(x => x.entry.Id == _selectedId))
        {
            _selectedId = matches.FirstOrDefault().entry?.Id;
            var entry = _rhythms.FirstOrDefault(r => r.Id == _selectedId);
            if (entry is not null) RhythmSelected?.Invoke(this, entry);
        }

        var rows = new List<object>();
        if (_groupView || _clinicalMode)
        {
            var byGroup = matches
                .GroupBy(x => PathologyGroups.IsKnown(x.entry.Group) ? x.entry.Group! : PathologyGroups.Other)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.title, StringComparer.CurrentCultureIgnoreCase).ToList());

            // Known groups in canonical order, then the trailing "Other" bucket.
            foreach (var key in PathologyGroups.OrderedKeys.Append(PathologyGroups.Other))
            {
                if (!byGroup.TryGetValue(key, out var items) || items.Count == 0) continue;
                var collapsed = _collapsedGroups.Contains(key);
                rows.Add(new RhythmHeader(key, PathologyGroups.DisplayName(key), items.Count, collapsed));
                if (collapsed) continue; // header only; items hidden until expanded
                foreach (var x in items)
                    rows.Add(new RhythmItem(x.entry.Id, x.title, x.entry.Id == _selectedId));
            }
        }
        else
        {
            foreach (var x in matches.OrderBy(x => x.title, StringComparer.CurrentCultureIgnoreCase))
                rows.Add(new RhythmItem(x.entry.Id, x.title, x.entry.Id == _selectedId));
        }

        List.ItemsSource = rows;
        ScrollToSelected();

        var selectedEntry = _rhythms.FirstOrDefault(r => r.Id == _selectedId);
        if (_clinicalMode && selectedEntry is not null && !string.IsNullOrWhiteSpace(selectedEntry.ClinicalCase))
        {
            ClinicalParametersList.ItemsSource = ParseClinicalCase(selectedEntry.ClinicalCase);
            ClinicalDashboard.Visibility = Visibility.Visible;
        }
        else
        {
            ClinicalDashboard.Visibility = Visibility.Collapsed;
        }
    }

    private List<ClinicalParameter> ParseClinicalCase(string clinicalCaseData)
    {
        var list = new List<ClinicalParameter>();
        if (string.IsNullOrWhiteSpace(clinicalCaseData)) return list;

        var parsedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var customList = new List<KeyValuePair<string, string>>();

        var pairs = clinicalCaseData.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;
            var key = parts[0].Trim().ToLowerInvariant();
            var val = parts[1].Trim();

            string standardKey;
            switch (key)
            {
                case "title":
                case "название":
                case "título":
                case "titulo":
                case "标题":
                case "शीर्षक":
                    standardKey = "title";
                    break;
                case "name":
                case "имя":
                case "фио":
                case "nombre":
                case "姓名":
                case "नाम":
                    standardKey = "name";
                    break;
                case "age":
                case "возраст":
                case "edad":
                case "年龄":
                case "आयु":
                    standardKey = "age";
                    break;
                case "gender":
                case "пол":
                case "género":
                case "genero":
                case "性别":
                case "लिंग":
                    standardKey = "gender";
                    break;
                case "hr":
                case "heart_rate":
                case "heartrate":
                case "чсс":
                case "frecuencia cardíaca":
                case "frecuencia cardiaca":
                case "心率":
                case "हृदय दर":
                    standardKey = "hr";
                    break;
                case "bp":
                case "blood_pressure":
                case "bloodpressure":
                case "ад":
                case "presión arterial":
                case "presion arterial":
                case "血压":
                case "रक्तचाप":
                    standardKey = "bp";
                    break;
                default:
                    standardKey = "custom:" + parts[0].Trim();
                    break;
            }

            if (standardKey.StartsWith("custom:"))
            {
                customList.Add(new KeyValuePair<string, string>(standardKey.Substring(7), val));
            }
            else
            {
                parsedMap[standardKey] = val;
            }
        }

        if (parsedMap.TryGetValue("title", out var titleVal))
            list.Add(new ClinicalParameter(AppStrings.ClinicalLabelTitle, titleVal));

        if (parsedMap.TryGetValue("name", out var nameVal))
            list.Add(new ClinicalParameter(AppStrings.ClinicalLabelPatientName, nameVal));

        if (parsedMap.TryGetValue("age", out var ageVal))
            list.Add(new ClinicalParameter(AppStrings.ClinicalLabelAge, ageVal));

        if (parsedMap.TryGetValue("gender", out var genderVal))
        {
            var displayGender = genderVal;
            var g = genderVal.Trim().ToLowerInvariant();
            if (g == "male" || g == "мужской" || g == "муж" || g == "мужчина" || g == "masculino" || g == "hombre" || g == "男" || g == "男性" || g == "पुरुष")
            {
                displayGender = AppStrings.GenderMale;
            }
            else if (g == "female" || g == "женский" || g == "жен" || g == "женщина" || g == "femenino" || g == "mujer" || g == "女" || g == "女性" || g == "महिला")
            {
                displayGender = AppStrings.GenderFemale;
            }
            list.Add(new ClinicalParameter(AppStrings.ClinicalLabelGender, displayGender));
        }

        if (parsedMap.TryGetValue("hr", out var hrVal))
            list.Add(new ClinicalParameter(AppStrings.ClinicalLabelHr, hrVal));

        if (parsedMap.TryGetValue("bp", out var bpVal))
            list.Add(new ClinicalParameter(AppStrings.ClinicalLabelBp, bpVal));

        foreach (var custom in customList)
        {
            var label = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(custom.Key);
            list.Add(new ClinicalParameter(label, custom.Value));
        }

        return list;
    }

    /// <summary>Scrolls the list so the selected rhythm is visible (Android's animateScrollToItem).</summary>
    private void ScrollToSelected()
    {
        // Defer to next layout pass — ScrollIntoView is a no-op if called synchronously
        // right after ItemsSource is replaced, before the ListView has laid out new items.
        if (_selectedId is null) return;
        var id = _selectedId;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (List.ItemsSource is not IReadOnlyList<object> items) return;
            var match = items.OfType<RhythmItem>().FirstOrDefault(i => i.Id == id);
            if (match is null) return;

            // Skip scroll if the item is already fully visible in the current viewport.
            if (List.ContainerFromItem(match) is FrameworkElement container)
            {
                var top = container.TransformToVisual(List).TransformPoint(default).Y;
                if (top >= 0 && top + container.ActualHeight <= List.ActualHeight) return;
            }

            List.ScrollIntoView(match, ScrollIntoViewAlignment.Leading);
        });
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        // Tapping a group header toggles its collapsed state.
        if (e.ClickedItem is RhythmHeader header)
        {
            if (!_collapsedGroups.Remove(header.Key)) _collapsedGroups.Add(header.Key);
            Rebuild();
            return;
        }

        if (e.ClickedItem is not RhythmItem item) return;
        _selectedId = item.Id;
        Rebuild();
        var entry = _rhythms.FirstOrDefault(r => r.Id == item.Id);
        if (entry is not null) RhythmSelected?.Invoke(this, entry);
    }
}

/// <summary>Display row for <see cref="RhythmChoosingPanel"/>'s list.</summary>
public sealed class RhythmItem
{
    public RhythmItem(string id, string title, bool isSelected)
    {
        Id = id;
        Title = title;
        Foreground = new SolidColorBrush(isSelected ? Microsoft.UI.Colors.Red : Theming.AppTheme.TextPrimaryColor);
    }

    public string Id { get; }
    public string Title { get; }
    public Brush Foreground { get; }
}

/// <summary>Tappable section header row in the grouped rhythm list (collapse/expand).</summary>
public sealed class RhythmHeader
{
    private static readonly string ChevronDown = char.ConvertFromUtf32(0xE70D);  // expanded
    private static readonly string ChevronRight = char.ConvertFromUtf32(0xE76C); // collapsed

    public RhythmHeader(string key, string title, int count, bool isCollapsed)
    {
        Key = key;
        Title = title;
        Count = count;
        IsCollapsed = isCollapsed;
    }

    public string Key { get; }
    public string Title { get; }
    public int Count { get; }
    public bool IsCollapsed { get; }
    public string Chevron => IsCollapsed ? ChevronRight : ChevronDown;
    public string CountText => Count.ToString();
}

/// <summary>Picks the header vs. rhythm-row template for the grouped list.</summary>
public sealed class RhythmRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? ItemTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is RhythmHeader ? HeaderTemplate : ItemTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}

public sealed class ClinicalParameter
{
    public string Label { get; }
    public string Value { get; }

    public ClinicalParameter(string label, string value)
    {
        Label = label;
        Value = value;
    }
}
