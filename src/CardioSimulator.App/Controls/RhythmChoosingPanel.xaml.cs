using System.ComponentModel;
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

    /// <summary>
    /// When the current selection is filtered out (by the search query), auto-select the first
    /// remaining match and raise <see cref="RhythmSelected"/>. On by default — a picker convenience
    /// for the monitor/teaching drawers. The Constructor turns this OFF: it must never silently switch
    /// the pathology being edited (that would discard unsaved edits), so it drives the selection
    /// one-way from the editor and only reacts to explicit taps.
    /// </summary>
    public bool AutoSelectOnFilter { get; set; } = true;

    /// <summary>
    /// The clinical-cases (true) vs plain-rhythms (false) filter, mirroring the header toggle but
    /// settable in code. A host makes the list follow an externally-owned selection with it: the
    /// Constructor flips it to the edited pathology's clinical status so the pathology always shows in
    /// its correct list — giving a pathology a clinical case moves it into the clinical-cases list.
    /// </summary>
    public bool ClinicalMode
    {
        get => _clinicalMode;
        set
        {
            if (_clinicalMode == value) return;
            _clinicalMode = value;
            // Programmatic IsChecked assignment does not raise ToggleButton.Click, so mirror the state
            // the click handler would set (toggle visual + A–Z sort availability) ourselves.
            ClinicalToggle.IsChecked = value;
            SortToggle.IsEnabled = !value;
            Rebuild();
            ScrollToSelected();
        }
    }

    // ── Multi-select (picker) mode ──────────────────────────────────────────────
    // Off by default: the drawer/monitor single-selects. When on, each rhythm row shows a checkbox and a
    // row tap toggles membership instead of single-selecting — letting the whole grouped/searchable UI
    // double as a multi-pick list (e.g. choosing which rhythms a course exposes). Checked/locked state
    // lives here (keyed by id), not on the recycled row VMs, so it survives Rebuild/scroll/search.
    private bool _multiSelect;
    private readonly HashSet<string> _checkedIds = new();
    private readonly HashSet<string> _lockedIds = new();

    /// <summary>Turns the panel into a multi-select picker: rhythm rows show a checkbox and tapping a row
    /// toggles its membership instead of single-selecting. Off by default.</summary>
    public bool MultiSelect
    {
        get => _multiSelect;
        set { if (_multiSelect == value) return; _multiSelect = value; Rebuild(); }
    }

    /// <summary>The ids checked in multi-select mode, including the always-checked locked ids.</summary>
    public IReadOnlyList<string> CheckedIds => _checkedIds.Concat(_lockedIds).Distinct().ToList();

    /// <summary>Seeds the initially-checked ids for multi-select mode.</summary>
    public void SetCheckedIds(IEnumerable<string> ids)
    {
        _checkedIds.Clear();
        foreach (var id in ids) _checkedIds.Add(id);
        Rebuild();
    }

    /// <summary>Sets ids that are checked and cannot be toggled off (e.g. rhythms embedded in a lecture);
    /// their row checkbox shows checked but dimmed.</summary>
    public void SetLockedIds(IEnumerable<string> ids)
    {
        _lockedIds.Clear();
        foreach (var id in ids) _lockedIds.Add(id);
        Rebuild();
    }

    /// <summary>Builds a rhythm row, carrying its multi-select checkbox state when the panel is a picker.</summary>
    private RhythmItem MakeItem(PathologyEntry entry, string rowTitle, bool isSubgroupItem)
    {
        var locked = _lockedIds.Contains(entry.Id);
        var isChecked = _multiSelect && (locked || _checkedIds.Contains(entry.Id));
        return new RhythmItem(entry.Id, rowTitle, entry.Id == _selectedId, isSubgroupItem,
            showCheckbox: _multiSelect, isChecked: isChecked, checkboxEnabled: !locked, isLocked: locked);
    }

    /// <summary>Group keys the user has collapsed in the grouped view.</summary>
    private readonly HashSet<string> _collapsedGroups = new();

    /// <summary>Subgroup keys the user has collapsed in the grouped view (groupKey + "/" + subgroupName).</summary>
    private readonly HashSet<string> _collapsedSubgroups = new();

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

    /// <summary>
    /// Whether the header pin toggle is shown. Hidden when the panel is hosted somewhere pinning is
    /// meaningless (e.g. inside a dropdown flyout picker rather than the collapsible left drawer).
    /// </summary>
    public bool ShowPinButton
    {
        get => PinToggle.Visibility == Visibility.Visible;
        set => PinToggle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Whether the large up/down page-scroll buttons float over the list's bottom-right corner.
    /// Off by default; enabled for the Teaching rhythm drawer where the list is long and the panel
    /// is often used on touch screens.
    /// </summary>
    public bool ShowScrollButtons
    {
        get => ScrollButtons.Visibility == Visibility.Visible;
        set => ScrollButtons.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public string? SelectedId
    {
        get => _selectedId;
        set
        {
            if (_selectedId == value) return;
            _selectedId = value;
            ExpandForId(value);
            Rebuild();
            ScrollToSelected();
        }
    }

    public event EventHandler<PathologyEntry>? RhythmSelected;

    /// <summary>
    /// Raised only when the user explicitly taps a rhythm row — unlike <see cref="RhythmSelected"/>,
    /// which <em>also</em> fires when filtering auto-selects the first remaining match. Hosts that
    /// present the panel as a one-shot dropdown picker listen to this to commit the choice and dismiss,
    /// so typing in the search box can't spuriously commit + close the picker mid-search.
    /// </summary>
    public event EventHandler<PathologyEntry>? RhythmInvoked;

    public RhythmChoosingPanel()
    {
        InitializeComponent();
        SearchBox.PlaceholderText = AppStrings.RhythmSearchPlaceholder;
        ToolTipService.SetToolTip(PinToggle, AppStrings.FixDrawer);
        ToolTipService.SetToolTip(ClinicalToggle, AppStrings.ClinicalModeTooltip);
        ToolTipService.SetToolTip(ExpandAllButton, AppStrings.ExpandAll);
        ToolTipService.SetToolTip(CollapseAllButton, AppStrings.CollapseAll);
        UpdateSortToggleVisual();

        Loaded += (_, _) => Theming.AppTheme.Changed += OnThemeChanged;
        Unloaded += (_, _) => Theming.AppTheme.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged() => Rebuild();

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
        ScrollToSelected();
    }

    private void OnExpandAllClick(object sender, RoutedEventArgs e)
    {
        _collapsedGroups.Clear();
        _collapsedSubgroups.Clear();
        Rebuild();
        ScrollToSelected();
    }

    private void OnCollapseAllClick(object sender, RoutedEventArgs e)
    {
        var list = _rhythms.AsEnumerable();
        if (_clinicalMode)
        {
            list = list.Where(r => !string.IsNullOrWhiteSpace(r.ClinicalCase));
        }
        else
        {
            list = list.Where(r => string.IsNullOrWhiteSpace(r.ClinicalCase));
        }

        var query = SearchBox.Text ?? string.Empty;
        var matches = list
            .Select(r => (entry: r, title: _clinicalMode ? GetClinicalCaseTitle(r) : TitleOf(r)))
            .Where(x => MatchesQuery(x.entry, x.title, query))
            .ToList();

        var byGroup = matches
            .GroupBy(x => PathologyGroups.IsKnown(x.entry.Group) ? x.entry.Group! : PathologyGroups.Other);

        foreach (var g in byGroup)
        {
            _collapsedGroups.Add(g.Key);
            var byTitle = g.GroupBy(x => x.title);
            foreach (var t in byTitle)
            {
                if (t.Count() > 1)
                {
                    var subgroupKey = g.Key + "/" + t.Key;
                    _collapsedSubgroups.Add(subgroupKey);
                }
            }
        }

        Rebuild();
    }

    private void UpdateSortToggleVisual()
    {
        // The icon + tooltip reflect the view that is currently active.
        SortIcon.Glyph = _groupView ? GlyphGroups : GlyphAlphabetical;
        ToolTipService.SetToolTip(SortToggle,
            _groupView ? AppStrings.RhythmSortByGroup : AppStrings.RhythmSortAlphabetical);
    }

    private void ExpandForId(string? id)
    {
        if (id is null) return;
        var entry = _rhythms.FirstOrDefault(r => r.Id == id);
        if (entry is null) return;

        var groupKey = PathologyGroups.IsKnown(entry.Group) ? entry.Group! : PathologyGroups.Other;
        _collapsedGroups.Remove(groupKey);

        var baseTitle = _clinicalMode ? GetClinicalCaseTitle(entry) : TitleOf(entry);
        var subgroupKey = groupKey + "/" + baseTitle;
        _collapsedSubgroups.Remove(subgroupKey);
    }

    public void SetRhythms(IReadOnlyList<PathologyEntry> rhythms)
    {
        _rhythms = rhythms;
        ExpandForId(_selectedId);
        Rebuild();
        ScrollToSelected();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Rebuild();

    private void OnToggleClinicalClick(object sender, RoutedEventArgs e)
    {
        _clinicalMode = ClinicalToggle.IsChecked == true;
        SortToggle.IsEnabled = !_clinicalMode;
        Rebuild();
        ScrollToSelected();
    }

    private string TitleOf(PathologyEntry entry) =>
        DisplayLanguage == DomainLanguage.RU ? entry.ResolvedNameRu ?? entry.TitleEn : entry.TitleEn;

    /// <summary>
    /// Whether an entry matches the current search box query. Matches on the displayed
    /// <paramref name="title"/> (case-insensitive substring), on the pathology's
    /// <see cref="PathologyEntry.Number"/> by prefix when the query is purely numeric (typing a number
    /// jumps to the correspondingly-numbered rhythm/clinical case — the number is shown as the row's
    /// prefix), or on any of its taxonomy acronyms (case-insensitive substring).
    /// </summary>
    private static bool MatchesQuery(PathologyEntry entry, string title, string query)
    {
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        var trimmed = query.Trim();
        if (trimmed.Length == 0)
            return false;

        if (entry.Number is { } number && trimmed.All(char.IsDigit)
            && number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                .StartsWith(trimmed, StringComparison.Ordinal))
            return true;

        return entry.AcronymList.Any(a => a.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Row label shown in the list. A numbered pathology is prefixed with its number
    /// ("{Number} {title}") in both rhythm and clinical mode; search and sort still key off the
    /// plain title.</summary>
    private string RowTitle(PathologyEntry entry, string title, bool isSubgroupItem = false, int indexInSubgroup = 0)
    {
        var prefix = entry.Number is { } n ? $"{n} " : "";
        if (isSubgroupItem)
        {
            if (entry.Number is not null)
            {
                return $"{entry.Number} {title}";
            }
            else
            {
                return $"{title} ({entry.Id})";
            }
        }
        return prefix + title;
    }

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
                var val = parts[1].Trim();
                return DisplayLanguage == DomainLanguage.RU
                    ? (PathologyTranslationHelpers.ResolveTextRu(val) ?? val)
                    : val;
            }
        }
        return TitleOf(entry);
    }

    private bool _isRebuilding;

    private void Rebuild(bool preserveScroll = false)
    {
        if (_isRebuilding) return;
        _isRebuilding = true;
        try
        {
            RebuildInternal(preserveScroll);
        }
        finally
        {
            _isRebuilding = false;
        }
    }

    private void RebuildInternal(bool preserveScroll = false)
    {
        var sv = GetListScrollViewer();
        double? oldOffset = sv?.VerticalOffset;

        // Refresh localized UI strings in case language changed
        SearchBox.PlaceholderText = AppStrings.RhythmSearchPlaceholder;
        ToolTipService.SetToolTip(PinToggle, AppStrings.FixDrawer);
        ToolTipService.SetToolTip(ClinicalToggle, AppStrings.ClinicalModeTooltip);
        ToolTipService.SetToolTip(ExpandAllButton, AppStrings.ExpandAll);
        ToolTipService.SetToolTip(CollapseAllButton, AppStrings.CollapseAll);
        ClinicalDashboardHeader.Text = AppStrings.ClinicalDashboardTitle;
        UpdateSortToggleVisual();

        var query = SearchBox.Text ?? string.Empty;
        var list = _rhythms.AsEnumerable();
        if (_clinicalMode)
        {
            list = list.Where(r => !string.IsNullOrWhiteSpace(r.ClinicalCase));
        }
        else
        {
            list = list.Where(r => string.IsNullOrWhiteSpace(r.ClinicalCase));
        }

        var matches = list
            .Select(r => (entry: r, title: _clinicalMode ? GetClinicalCaseTitle(r) : TitleOf(r)))
            .Where(x => MatchesQuery(x.entry, x.title, query))
            .ToList();

        // When the current selection falls outside the filtered matches, only the self-driven picker
        // (monitor/teaching) reacts — by following the filter to the first remaining match, or clearing
        // the selection when nothing matches. The Constructor (AutoSelectOnFilter=false) owns its
        // selection externally and must never let filtering reassign the pathology being edited (that
        // would discard unsaved edits); its host instead flips <see cref="ClinicalMode"/> so the edited
        // pathology reappears in its correct clinical-vs-rhythm list.
        if (AutoSelectOnFilter && _selectedId is not null && !matches.Any(x => x.entry.Id == _selectedId))
        {
            _selectedId = matches.FirstOrDefault().entry?.Id;
            var entry = _rhythms.FirstOrDefault(r => r.Id == _selectedId);
            if (entry is not null)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    RhythmSelected?.Invoke(this, entry);
                });
            }
        }

        var rows = new List<object>();
        if (_groupView || _clinicalMode)
        {
            var byGroup = matches
                .GroupBy(x => PathologyGroups.IsKnown(x.entry.Group) ? x.entry.Group! : PathologyGroups.Other)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Known groups in canonical order, then the trailing "Other" bucket.
            foreach (var key in PathologyGroups.OrderedKeys.Append(PathologyGroups.Other))
            {
                if (!byGroup.TryGetValue(key, out var items) || items.Count == 0) continue;
                var collapsed = _collapsedGroups.Contains(key);
                rows.Add(new RhythmHeader(key, PathologyGroups.DisplayName(key), items.Count, collapsed));
                if (collapsed) continue; // header only; items hidden until expanded

                // Group items by their base title.
                // We order subgroups and standalone items by:
                // 1. Complexity (number of factors in the title, separated by '+').
                // 2. Alphabetically by title.
                var itemsByTitle = items.GroupBy(x => x.title).ToList();
                var sortedTitles = itemsByTitle
                    .Select(g => new
                    {
                        Title = g.Key,
                        Items = g.ToList(),
                        FactorCount = g.Key.Split('+', StringSplitOptions.TrimEntries).Length
                    })
                    .OrderBy(x => x.FactorCount)
                    .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                foreach (var groupInfo in sortedTitles)
                {
                    var title = groupInfo.Title;
                    var groupItems = groupInfo.Items;

                    if (groupItems.Count > 1)
                    {
                        // Subgroup!
                        var subgroupKey = key + "/" + title;
                        var subgroupCollapsed = _collapsedSubgroups.Contains(subgroupKey);
                        rows.Add(new RhythmSubgroupHeader(subgroupKey, title, groupItems.Count, subgroupCollapsed));
                        if (subgroupCollapsed) continue;

                        for (int i = 0; i < groupItems.Count; i++)
                        {
                            var x = groupItems[i];
                            rows.Add(MakeItem(
                                x.entry,
                                RowTitle(x.entry, x.title, isSubgroupItem: true, indexInSubgroup: i + 1),
                                isSubgroupItem: true));
                        }
                    }
                    else
                    {
                        // Standalone item!
                        var x = groupItems[0];
                        rows.Add(MakeItem(
                            x.entry,
                            RowTitle(x.entry, x.title, isSubgroupItem: false, indexInSubgroup: 0),
                            isSubgroupItem: false));
                    }
                }
            }
        }
        else
        {
            foreach (var x in matches.OrderBy(x => x.title, StringComparer.CurrentCultureIgnoreCase))
                rows.Add(MakeItem(x.entry, RowTitle(x.entry, x.title), isSubgroupItem: false));
        }

        List.ItemsSource = rows;

        if (preserveScroll && oldOffset is { } offset)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                var currentSv = GetListScrollViewer();
                currentSv?.ChangeView(null, offset, null, true);
            });
        }

        UpdateClinicalDashboard();
    }

    /// <summary>Refreshes the bottom clinical-parameters dashboard for the current selection. Called from
    /// <see cref="RebuildInternal"/> and, on an in-place selection tap, from <see cref="OnItemClick"/> — so
    /// the dashboard follows the selection without a list rebuild.</summary>
    private void UpdateClinicalDashboard()
    {
        var selectedEntry = _rhythms.FirstOrDefault(r => r.Id == _selectedId);
        if (_clinicalMode && selectedEntry is not null && !string.IsNullOrWhiteSpace(selectedEntry.ClinicalCase))
        {
            // Header: "Clinical Case №N" when the case is enumerated, else the plain title.
            ClinicalDashboardHeader.Text = selectedEntry.Number is { } number
                ? $"{AppStrings.ClinicalDashboardTitle} №{number}"
                : AppStrings.ClinicalDashboardTitle;
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
                case "description":
                case "описание":
                case "descripción":
                case "descripcion":
                case "描述":
                case "विवरण":
                    standardKey = "description";
                    break;
                case "name":
                case "имя":
                case "фио":
                case "nombre":
                case "姓名":
                case "нама":
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
        {
            var displayTitle = DisplayLanguage == DomainLanguage.RU
                ? (PathologyTranslationHelpers.ResolveTextRu(titleVal) ?? titleVal)
                : titleVal;
            list.Add(new ClinicalParameter(AppStrings.ClinicalLabelTitle, displayTitle));
        }

        if (parsedMap.TryGetValue("description", out var descriptionVal))
        {
            var displayDesc = DisplayLanguage == DomainLanguage.RU
                ? (PathologyTranslationHelpers.ResolveTextRu(descriptionVal) ?? descriptionVal)
                : descriptionVal;
            list.Add(new ClinicalParameter(AppStrings.ClinicalLabelDescription, displayDesc));
        }

        if (parsedMap.TryGetValue("name", out var nameVal))
        {
            var displayName = DisplayLanguage == DomainLanguage.RU
                ? (PathologyTranslationHelpers.ResolveTextRu(nameVal) ?? nameVal)
                : nameVal;
            list.Add(new ClinicalParameter(AppStrings.ClinicalLabelPatientName, displayName));
        }

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
            var customKey = custom.Key;
            var customVal = custom.Value;
            if (DisplayLanguage == DomainLanguage.RU)
            {
                customKey = PathologyTranslationHelpers.ResolveTextRu(customKey) ?? customKey;
                customVal = PathologyTranslationHelpers.ResolveTextRu(customVal) ?? customVal;
            }
            var label = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(customKey);
            list.Add(new ClinicalParameter(label, customVal));
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

    /// <summary>The list's internal <see cref="ScrollViewer"/>, cached once the template is realized.</summary>
    private ScrollViewer? _listScrollViewer;

    private ScrollViewer? GetListScrollViewer()
    {
        if (_listScrollViewer is not null) return _listScrollViewer;
        _listScrollViewer = FindDescendant<ScrollViewer>(List);
        return _listScrollViewer;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) return typed;
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    private void OnScrollUpClick(object sender, RoutedEventArgs e)
    {
        var sv = GetListScrollViewer();
        if (sv is null) return;
        // Scroll up by ~90% of a page so a sliver of the previous viewport stays for context.
        var target = Math.Max(0, sv.VerticalOffset - sv.ViewportHeight * 0.9);
        sv.ChangeView(null, target, null);
    }

    private void OnScrollDownClick(object sender, RoutedEventArgs e)
    {
        var sv = GetListScrollViewer();
        if (sv is null) return;
        var target = Math.Min(sv.ScrollableHeight, sv.VerticalOffset + sv.ViewportHeight * 0.9);
        sv.ChangeView(null, target, null);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        // Tapping a group header toggles its collapsed state.
        if (e.ClickedItem is RhythmHeader header)
        {
            if (!_collapsedGroups.Remove(header.Key)) _collapsedGroups.Add(header.Key);
            Rebuild(preserveScroll: true);
            return;
        }

        if (e.ClickedItem is RhythmSubgroupHeader subgroupHeader)
        {
            if (!_collapsedSubgroups.Remove(subgroupHeader.Key)) _collapsedSubgroups.Add(subgroupHeader.Key);
            Rebuild(preserveScroll: true);
            return;
        }

        if (e.ClickedItem is not RhythmItem item) return;

        // Multi-select picker: a row tap toggles membership (the checkbox is display-only). Locked rows are
        // always included and can't be toggled off. No Rebuild — the row VM raises PropertyChanged so its
        // checkbox updates in place, keeping scroll position.
        if (_multiSelect)
        {
            if (item.IsLocked) return;
            var nowChecked = !item.IsChecked;
            item.IsChecked = nowChecked;
            if (nowChecked) _checkedIds.Add(item.Id); else _checkedIds.Remove(item.Id);
            return;
        }

        // Recolour the selection in place rather than rebuilding the list. Replacing ItemsSource resets
        // the ScrollViewer to the top and the deferred offset-restore lands a frame later, so a full
        // Rebuild makes the list visibly flash-and-jump on every tap. Only the highlight (and the clinical
        // dashboard) actually changes, so update just the previously- and newly-selected rows and leave the
        // scroll position untouched — the tapped row stays exactly where it is under the finger.
        var previousId = _selectedId;
        _selectedId = item.Id;
        if (previousId != _selectedId && List.ItemsSource is IEnumerable<object> currentRows)
        {
            foreach (var rhythmRow in currentRows.OfType<RhythmItem>())
            {
                if (rhythmRow.Id == previousId) rhythmRow.SetSelected(false);
                else if (rhythmRow.Id == _selectedId) rhythmRow.SetSelected(true);
            }
        }
        UpdateClinicalDashboard();

        var entry = _rhythms.FirstOrDefault(r => r.Id == item.Id);
        if (entry is not null)
        {
            RhythmSelected?.Invoke(this, entry);
            RhythmInvoked?.Invoke(this, entry); // explicit tap → hosts may commit + dismiss
        }
    }
}

/// <summary>Display row for <see cref="RhythmChoosingPanel"/>'s list.</summary>
public sealed class RhythmItem : INotifyPropertyChanged
{
    public RhythmItem(string id, string title, bool isSelected, bool isSubgroupItem = false,
        bool showCheckbox = false, bool isChecked = false, bool checkboxEnabled = true, bool isLocked = false)
    {
        Id = id;
        Title = title;
        _foreground = new SolidColorBrush(isSelected ? Microsoft.UI.Colors.Red : Theming.AppTheme.TextPrimaryColor);
        Padding = new Thickness(isSubgroupItem ? 24 : 4, 7, 4, 7);
        CheckboxVisibility = showCheckbox ? Visibility.Visible : Visibility.Collapsed;
        CheckboxEnabled = checkboxEnabled;
        IsLocked = isLocked;
        _isChecked = isChecked;
    }

    public string Id { get; }
    public string Title { get; }
    public Thickness Padding { get; }

    private Brush _foreground;

    /// <summary>Row text colour; red marks the selected rhythm. Bound <c>OneWay</c> and raises change
    /// notification so a selection change can recolour just the affected rows in place, instead of
    /// rebuilding the whole list (which resets the ScrollViewer and makes the list visibly jump).</summary>
    public Brush Foreground
    {
        get => _foreground;
        private set
        {
            if (ReferenceEquals(_foreground, value)) return;
            _foreground = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Foreground)));
        }
    }

    /// <summary>Recolours the row to reflect single-select highlight without a list rebuild.</summary>
    public void SetSelected(bool selected) =>
        Foreground = new SolidColorBrush(selected ? Microsoft.UI.Colors.Red : Theming.AppTheme.TextPrimaryColor);

    /// <summary>Whether the row shows a selection checkbox (multi-select picker mode).</summary>
    public Visibility CheckboxVisibility { get; }

    /// <summary>False for locked rows — the checkbox shows checked but dimmed.</summary>
    public bool CheckboxEnabled { get; }

    /// <summary>Locked rows are always included and can't be toggled off (e.g. a rhythm embedded in a lecture).</summary>
    public bool IsLocked { get; }

    private bool _isChecked;

    /// <summary>Checked state in multi-select mode. Bound one-way to the row checkbox; the panel toggles it
    /// on a row tap and reads it back on commit. Raises change notification so the checkbox reflects a
    /// programmatic toggle without a full rebuild.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
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

/// <summary>Tappable subgroup header row in the grouped rhythm list (collapse/expand).</summary>
public sealed class RhythmSubgroupHeader
{
    private static readonly string ChevronDown = char.ConvertFromUtf32(0xE70D);  // expanded
    private static readonly string ChevronRight = char.ConvertFromUtf32(0xE76C); // collapsed

    public RhythmSubgroupHeader(string key, string title, int count, bool isCollapsed)
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
    public string CountText => $"({Count})";
}

/// <summary>Picks the header vs. subgroup-header vs. rhythm-row template for the grouped list.</summary>
public sealed class RhythmRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? SubgroupHeaderTemplate { get; set; }
    public DataTemplate? ItemTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is RhythmHeader) return HeaderTemplate;
        if (item is RhythmSubgroupHeader) return SubgroupHeaderTemplate;
        return ItemTemplate;
    }

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
