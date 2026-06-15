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
        ScrollToSelected();
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
            if (List.ItemsSource is not IReadOnlyList<RhythmItem> items) return;
            var match = items.FirstOrDefault(i => i.Id == id);
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
        Foreground = new SolidColorBrush(isSelected ? Microsoft.UI.Colors.Red : Microsoft.UI.Colors.Black);
    }

    public string Id { get; }
    public string Title { get; }
    public Brush Foreground { get; }
}
