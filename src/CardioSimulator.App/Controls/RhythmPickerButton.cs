using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The canonical inline rhythm/pathology picker: a dropdown button showing the selected rhythm's
/// localized name that opens a flyout hosting the grouped-and-searchable <see cref="RhythmChoosingPanel"/>
/// — the exact same UI as the Teaching left drawer (category groups, subgroups, search box, and the
/// clinical / group-vs-A–Z / expand-all / collapse-all buttons). Every ECG/rhythm field in the app
/// uses this control so they all look and behave identically, replacing the old assortment of flat
/// <see cref="ComboBox"/>es and <see cref="MenuFlyout"/>s.
/// </summary>
/// <remarks>
/// The panel is built lazily on first open, so the (potentially huge) catalog is only grouped when
/// the picker is actually opened. Selection commits on the panel's <see cref="RhythmChoosingPanel.RhythmInvoked"/>
/// (an explicit tap) — never on the auto-select that fires while filtering — so typing in the search
/// box can't spuriously commit and close the flyout.
/// </remarks>
public sealed class RhythmPickerButton : UserControl
{
    private readonly Button _button;
    private readonly TextBlock _label;
    private readonly Button _clearButton;
    private readonly Flyout _flyout;

    private RhythmChoosingPanel? _panel;
    private IReadOnlyList<PathologyEntry> _rhythms = Array.Empty<PathologyEntry>();
    private string? _selectedId;

    /// <summary>Fired when the user picks a rhythm (carries the entry) or clears the field (null).</summary>
    public event EventHandler<PathologyEntry?>? SelectionChanged;

    public RhythmPickerButton()
    {
        _label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _button = new Button
        {
            Content = _label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _clearButton = new Button
        {
            Content = "✕",
            MinWidth = 36,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _clearButton.Click += (_, _) => Clear();

        _flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };
        _flyout.Opening += OnFlyoutOpening;
        _button.Flyout = _flyout;

        var root = new Grid { MinWidth = 220 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_button, 0);
        root.Children.Add(_button);
        Grid.SetColumn(_clearButton, 1);
        root.Children.Add(_clearButton);
        Content = root;

        UpdateLabel();
    }

    /// <summary>Language used for the button label (the panel is told separately via the same value).</summary>
    public DomainLanguage DisplayLanguage { get; set; } = DomainLanguage.EN;

    /// <summary>Text shown on the button when nothing is selected (e.g. a localized "None").</summary>
    public string PlaceholderText { get; set; } = string.Empty;

    /// <summary>Tooltip for the clear (✕) button.</summary>
    public string? ClearTooltip { get; set; }

    /// <summary>Height of the dropdown panel.</summary>
    public double DropdownHeight { get; set; } = 440;

    /// <summary>Width of the dropdown panel.</summary>
    public double DropdownWidth { get; set; } = 300;

    /// <summary>Whether the clear (✕) button is offered when a rhythm is selected.</summary>
    public bool ShowClearButton { get; set; } = true;

    /// <summary>Optional predicate restricting which rhythms appear in the panel (e.g. exclude a target
    /// and already-chosen distractors). Applied when the panel is (re)populated.</summary>
    public Func<PathologyEntry, bool>? Filter { get; set; }

    public string? SelectedId
    {
        get => _selectedId;
        set
        {
            _selectedId = value;
            if (_panel is not null) _panel.SelectedId = value;
            UpdateLabel();
        }
    }

    /// <summary>Supplies the rhythm catalog. Safe to call repeatedly (e.g. when the manifest loads).</summary>
    public void SetRhythms(IReadOnlyList<PathologyEntry> rhythms)
    {
        _rhythms = rhythms;
        if (_panel is not null) _panel.SetRhythms(FilteredRhythms());
        UpdateLabel();
    }

    private IReadOnlyList<PathologyEntry> FilteredRhythms() =>
        Filter is null ? _rhythms : _rhythms.Where(Filter).ToList();

    private void OnFlyoutOpening(object? sender, object e)
    {
        if (_panel is null)
        {
            _panel = new RhythmChoosingPanel
            {
                DisplayLanguage = DisplayLanguage,
                ShowPinButton = false, // pinning is meaningless inside a dropdown
                Width = DropdownWidth,
                Height = DropdownHeight,
            };
            _panel.SetRhythms(FilteredRhythms());
            _panel.RhythmInvoked += (_, entry) =>
            {
                _selectedId = entry.Id;
                UpdateLabel();
                SelectionChanged?.Invoke(this, entry);
                _flyout.Hide();
            };
            _flyout.Content = _panel;
        }
        else
        {
            _panel.DisplayLanguage = DisplayLanguage;
            _panel.SetRhythms(FilteredRhythms());
        }
        _panel.SelectedId = _selectedId;
    }

    private void Clear()
    {
        _selectedId = null;
        if (_panel is not null) _panel.SelectedId = null;
        UpdateLabel();
        SelectionChanged?.Invoke(this, null);
    }

    private void UpdateLabel()
    {
        if (_selectedId is { } id && _rhythms.FirstOrDefault(r => r.Id == id) is { } entry)
        {
            _label.Text = DisplayLanguage == DomainLanguage.RU ? (entry.NameRu ?? entry.TitleEn) : entry.TitleEn;
            _clearButton.Visibility = ShowClearButton ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _label.Text = PlaceholderText;
            _clearButton.Visibility = Visibility.Collapsed;
        }
        if (ClearTooltip is not null) ToolTipService.SetToolTip(_clearButton, ClearTooltip);
    }
}
