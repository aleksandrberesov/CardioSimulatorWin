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

    public DomainLanguage DisplayLanguage { get; set; } = DomainLanguage.EN;
    public string? SelectedId { get; set; }

    public event EventHandler<PathologyEntry>? RhythmSelected;

    public RhythmChoosingPanel()
    {
        InitializeComponent();
        SearchBox.PlaceholderText = AppStrings.RhythmSearchPlaceholder;
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
            .Select(x => new RhythmItem(x.entry.Id, x.title, x.entry.Id == SelectedId))
            .ToList();
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RhythmItem item) return;
        SelectedId = item.Id;
        var entry = _rhythms.FirstOrDefault(r => r.Id == item.Id);
        if (entry is not null) RhythmSelected?.Invoke(this, entry);
        Rebuild();
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
