using CardioSimulator.App.Localization;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
// FrameworkElement already has a string `Language` property that shadows the enum
// type name inside this control, so reference the enum through an alias.
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Searchable scrollable list of pathology titles, mirroring the Android
/// <c>RhythmChoosingPanel</c>: title is the RU name when the language is Russian
/// else the English title; the selected entry is shown in red.
/// </summary>
public sealed partial class RhythmChoosingPanel : UserControl
{
    private IReadOnlyList<PathologyEntry> _rhythms = Array.Empty<PathologyEntry>();
    private string? _selectedId;

    public DomainLanguage DisplayLanguage { get; set; } = DomainLanguage.EN;

    /// <summary>Raised when the "Fix drawer" checkbox toggles (Android <c>setDrawerFixed</c>).</summary>
    public event EventHandler<bool>? PinnedChanged;

    private bool _suppressPinEvent;

    /// <summary>Reflects the "Fix drawer" checkbox without re-raising <see cref="PinnedChanged"/>.</summary>
    public bool Pinned
    {
        get => PinCheck.IsChecked == true;
        set
        {
            _suppressPinEvent = true;
            PinCheck.IsChecked = value;
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
            ScrollToSelected();
        }
    }

    public event EventHandler<PathologyEntry>? RhythmSelected;

    public RhythmChoosingPanel()
    {
        InitializeComponent();
        SearchBox.PlaceholderText = AppStrings.RhythmSearchPlaceholder;
        PinCheck.Content = AppStrings.FixDrawer;
    }

    private void OnPinChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_suppressPinEvent) return;
        PinnedChanged?.Invoke(this, PinCheck.IsChecked == true);
    }

    public void SetRhythms(IReadOnlyList<PathologyEntry> rhythms)
    {
        _rhythms = rhythms;
        Rebuild();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        var query = SearchBox.Text ?? string.Empty;
        List.ItemsSource = _rhythms
            .Select(r => (entry: r, title: DisplayLanguage == DomainLanguage.RU ? r.NameRu ?? r.TitleEn : r.TitleEn))
            .Where(x => x.title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(x => new RhythmItem(x.entry.Id, x.title, x.entry.Id == _selectedId))
            .ToList();
    }

    /// <summary>Scrolls the list so the selected rhythm is visible (Android's animateScrollToItem).</summary>
    private void ScrollToSelected()
    {
        if (_selectedId is null || List.ItemsSource is not IReadOnlyList<RhythmItem> items) return;
        var match = items.FirstOrDefault(i => i.Id == _selectedId);
        if (match is not null) List.ScrollIntoView(match, ScrollIntoViewAlignment.Leading);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RhythmItem item) return;
        _selectedId = item.Id;
        Rebuild();
        ScrollToSelected();
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
        Foreground = new SolidColorBrush(isSelected ? Microsoft.UI.Colors.Red : Microsoft.UI.Colors.Black);
    }

    public string Id { get; }
    public string Title { get; }
    public Brush Foreground { get; }
}
