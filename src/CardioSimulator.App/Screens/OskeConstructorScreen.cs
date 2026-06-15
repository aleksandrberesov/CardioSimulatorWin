using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DomainLanguage = CardioSimulator.Core.Domain.Language;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Standalone OSCE constructor — its own top-level operating mode (parallel to the Course
/// Constructor). An **Эталоны / Шаблон** toggle switches between authoring per-ECG answer keys
/// (<see cref="OskeConstructorViewModel"/>, with a live trace preview) and editing the form template
/// itself (<see cref="OskeFormEditor"/>). The ECG list refreshes reactively once the rhythm manifest
/// loads.
/// </summary>
/// <remarks>
/// The key/form/intro areas are built once and toggled via <see cref="UIElement.Visibility"/> rather
/// than swapped in/out of the tree — the Win2D-backed monitor tears itself down on <c>Unloaded</c>,
/// so re-parenting it would destroy it and crash the XAML layer (see <see cref="OSKEScreen"/>).
/// </remarks>
public sealed class OskeConstructorScreen : UserControl
{
    private readonly OskeConstructorViewModel _ctorVm;
    private readonly MonitorViewModel _monitorVm;
    private readonly RhythmViewModel _rhythmVm;
    private readonly AppViewModel _appVm;
    private readonly MonitorView _monitor = new();

    private ComboBox _specialtyBox = null!;
    private ComboBox _ecgBox = null!;
    private TextBlock _ecgLabel = null!;
    private Button _saveBtn = null!;
    private TextBlock _status = null!;
    private Button _keysModeBtn = null!;
    private Button _formModeBtn = null!;

    // Persistent body areas (toggled by Visibility, never removed from the tree).
    private Grid _keysArea = null!;
    private readonly ScrollViewer _keysEditorScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ContentControl _formHost = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };
    private FrameworkElement _introArea = null!;

    private string _ctorMode = "keys";
    private bool _suppressEcg;

    public OskeConstructorScreen(OskeConstructorViewModel ctorVm, MonitorViewModel monitorVm, RhythmViewModel rhythmVm, AppViewModel appVm)
    {
        _ctorVm = ctorVm;
        _monitorVm = monitorVm;
        _rhythmVm = rhythmVm;
        _appVm = appVm;
        _monitor.Bind(monitorVm, rhythmVm);
        _monitor.DisplayLanguage = appVm.SelectedLanguage;

        Content = BuildLayout();
        _rhythmVm.PropertyChanged += OnRhythmChanged;
        Unloaded += (_, _) => _rhythmVm.PropertyChanged -= OnRhythmChanged;
        ApplyMode();
    }

    private void OnRhythmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RhythmViewModel.Rhythms)) PopulateEcgBox();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Padding = new Thickness(12, 8, 12, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Button ModeButton(string text) => new()
        {
            Content = text,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        _keysModeBtn = ModeButton(AppStrings.OskeCtorModeKeys);
        _formModeBtn = ModeButton(AppStrings.OskeCtorModeForm);

        _specialtyBox = new ComboBox { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (sp, label) in SpecialtyOptions())
            _specialtyBox.Items.Add(new ComboBoxItem { Content = label, Tag = sp });
        _specialtyBox.SelectedIndex = (int)_ctorVm.Specialty;

        _ecgLabel = new TextBlock { Text = AppStrings.OskeFieldEcg, VerticalAlignment = VerticalAlignment.Center };
        _ecgBox = new ComboBox { MinWidth = 240, PlaceholderText = AppStrings.OskeFieldEcg, VerticalAlignment = VerticalAlignment.Center };

        _saveBtn = new Button { Content = AppStrings.OskeCtorSave, IsEnabled = false };
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };

        toolbar.Children.Add(_keysModeBtn);
        toolbar.Children.Add(_formModeBtn);
        toolbar.Children.Add(new TextBlock { Text = AppStrings.OskeFieldSpecialty, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        toolbar.Children.Add(_specialtyBox);
        toolbar.Children.Add(_ecgLabel);
        toolbar.Children.Add(_ecgBox);
        toolbar.Children.Add(_saveBtn);
        toolbar.Children.Add(_status);
        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        var body = BuildBody();
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        return root;
    }

    private Grid _body = null!;

    private Grid BuildBody()
    {
        _body = new Grid();

        // Keys area: persistent 2-pane layout — the monitor lives here for the screen's lifetime.
        _keysArea = new Grid { Visibility = Visibility.Collapsed };
        _keysArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        _keysArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        Grid.SetColumn(_monitor, 0);
        _keysArea.Children.Add(_monitor);
        Grid.SetColumn(_keysEditorScroll, 1);
        _keysArea.Children.Add(_keysEditorScroll);

        _formHost.Visibility = Visibility.Collapsed;

        _introArea = new TextBlock
        {
            Text = AppStrings.OskeCtorIntro,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Colors.Gray),
        };

        _body.Children.Add(_keysArea);
        _body.Children.Add(_formHost);
        _body.Children.Add(_introArea);

        PopulateEcgBox();

        _keysModeBtn.Click += (_, _) => { _ctorMode = "keys"; ApplyMode(); };
        _formModeBtn.Click += (_, _) => { _ctorMode = "form"; ApplyMode(); };
        _specialtyBox.SelectionChanged += (_, _) => RenderBody();
        _ecgBox.SelectionChanged += (_, _) => { if (!_suppressEcg) RenderBody(); };
        _saveBtn.Click += (_, _) => { if (_ctorVm.Save()) _status.Text = AppStrings.OskeCtorSaved; };

        return _body;
    }

    /// <summary>(Re)fills the ECG list from the rhythm manifest, preserving the current selection.</summary>
    private void PopulateEcgBox()
    {
        var prev = (_ecgBox.SelectedItem as ComboBoxItem)?.Tag as string ?? _ctorVm.EcgId;
        _suppressEcg = true;
        try
        {
            _ecgBox.Items.Clear();
            foreach (var entry in _rhythmVm.Rhythms)
                _ecgBox.Items.Add(new ComboBoxItem { Content = EcgLabel(entry.Id), Tag = entry.Id });
            if (prev is not null)
                _ecgBox.SelectedItem = _ecgBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == prev);
        }
        finally
        {
            _suppressEcg = false;
        }
    }

    private OskeSpecialty CurrentSpecialty() => (OskeSpecialty)((ComboBoxItem)_specialtyBox.SelectedItem).Tag;

    private void ApplyMode()
    {
        _keysModeBtn.FontWeight = _ctorMode == "keys" ? FontWeights.Bold : FontWeights.Normal;
        _formModeBtn.FontWeight = _ctorMode == "form" ? FontWeights.Bold : FontWeights.Normal;
        var keys = _ctorMode == "keys";
        _ecgLabel.Visibility = keys ? Visibility.Visible : Visibility.Collapsed;
        _ecgBox.Visibility = keys ? Visibility.Visible : Visibility.Collapsed;
        _saveBtn.Visibility = keys ? Visibility.Visible : Visibility.Collapsed;
        _status.Visibility = keys ? Visibility.Visible : Visibility.Collapsed;
        RenderBody();
    }

    private void RenderBody()
    {
        if (_ctorMode == "form")
        {
            _formHost.Content = new OskeFormEditor(_appVm.OskeRepository, CurrentSpecialty());
            _formHost.Visibility = Visibility.Visible;
            _keysArea.Visibility = Visibility.Collapsed;
            _introArea.Visibility = Visibility.Collapsed;
            _monitorVm.SetIsRunning(false);
            return;
        }

        if (_ecgBox.SelectedItem is not ComboBoxItem ei)
        {
            _introArea.Visibility = Visibility.Visible;
            _keysArea.Visibility = Visibility.Collapsed;
            _formHost.Visibility = Visibility.Collapsed;
            _saveBtn.IsEnabled = false;
            _status.Text = string.Empty;
            _monitorVm.SetIsRunning(false);
            return;
        }

        _ctorVm.Select(CurrentSpecialty(), (string)ei.Tag);
        _keysEditorScroll.Content = BuildKeyEditor();
        if (_ctorVm.EcgId is not null) _rhythmVm.SelectRhythm(_ctorVm.EcgId, persist: false);
        _monitorVm.SetIsRunning(true);

        _keysArea.Visibility = Visibility.Visible;
        _formHost.Visibility = Visibility.Collapsed;
        _introArea.Visibility = Visibility.Collapsed;
        _saveBtn.IsEnabled = true;
        _status.Text = _ctorVm.HasExistingKey ? AppStrings.OskeCtorHasKey : string.Empty;
    }

    private UIElement BuildKeyEditor()
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(12, 4, 12, 4) };
        var form = _ctorVm.Form!;
        foreach (var q in form.Questions)
        {
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(new TextBlock
            {
                Text = $"{q.Number}. {q.Title}",
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            foreach (var opt in q.Options)
            {
                var qid = q.Id;
                var oid = opt.Id;
                if (q.Kind == OskeAnswerKind.Single)
                {
                    var rb = new RadioButton
                    {
                        Content = WrapText(opt.Text),
                        GroupName = "octor_" + qid,
                        IsChecked = _ctorVm.IsCorrect(qid, oid),
                        Margin = new Thickness(12, 0, 0, 0),
                    };
                    rb.Checked += (_, _) => _ctorVm.SetSingle(qid, oid);
                    block.Children.Add(rb);
                }
                else
                {
                    var cb = new CheckBox
                    {
                        Content = WrapText(opt.Text),
                        IsChecked = _ctorVm.IsCorrect(qid, oid),
                        Margin = new Thickness(12, 0, 0, 0),
                    };
                    cb.Checked += (_, _) => _ctorVm.ToggleMulti(qid, oid, true);
                    cb.Unchecked += (_, _) => _ctorVm.ToggleMulti(qid, oid, false);
                    block.Children.Add(cb);
                }
            }
            panel.Children.Add(block);
        }
        return panel;
    }

    private static TextBlock WrapText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static IEnumerable<(OskeSpecialty, string)> SpecialtyOptions() => new[]
    {
        (OskeSpecialty.Therapy, AppStrings.OskeSpecialtyTherapy),
        (OskeSpecialty.Cardiology, AppStrings.OskeSpecialtyCardiology),
        (OskeSpecialty.FunctionalDiagnostics, AppStrings.OskeSpecialtyFd),
    };

    private string EcgLabel(string id)
    {
        var entry = _rhythmVm.Rhythms.FirstOrDefault(r => r.Id == id);
        if (entry is null) return id;
        return _appVm.SelectedLanguage == DomainLanguage.RU ? (entry.NameRu ?? entry.TitleEn) : entry.TitleEn;
    }
}
