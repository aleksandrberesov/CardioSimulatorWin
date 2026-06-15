using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.App.Localization;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Edits an OSCE conclusion-form template for one specialty — add/remove/reorder blocks, edit titles,
/// switch single/multi, add/remove options, and set the pass threshold — then saves it via
/// <see cref="OskeRepository.WriteForm"/> (bumping the form version). Lets a teacher adapt the form to
/// changing accreditation requirements without a code release. Existing option ids are preserved on
/// rename so already-authored answer keys keep matching. WS6.
/// </summary>
public sealed class OskeFormEditor : UserControl
{
    private sealed class EditOption
    {
        public string Id = string.Empty;
        public string Text = string.Empty;
    }

    private sealed class EditQuestion
    {
        public string Id = string.Empty;
        public string Title = string.Empty;
        public OskeAnswerKind Kind;
        public List<EditOption> Options = new();
    }

    private readonly OskeRepository _repo;
    private readonly OskeSpecialty _specialty;
    private readonly List<EditQuestion> _questions;
    private double _passFraction;

    private readonly StackPanel _questionsHost = new() { Spacing = 12, Padding = new Thickness(12) };
    private readonly TextBlock _status = new() { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _passBox;

    public OskeFormEditor(OskeRepository repo, OskeSpecialty specialty)
    {
        _repo = repo;
        _specialty = specialty;

        var form = repo.FormFor(specialty);
        _passFraction = form.PassFraction;
        _questions = form.Questions.Select(q => new EditQuestion
        {
            Id = q.Id,
            Title = q.Title,
            Kind = q.Kind,
            Options = q.Options.Select(o => new EditOption { Id = o.Id, Text = o.Text }).ToList(),
        }).ToList();

        _passBox = new NumberBox
        {
            Header = AppStrings.OskeFormPass,
            Minimum = 0,
            Maximum = 100,
            Value = Math.Round(_passFraction * 100),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 5,
            LargeChange = 10,
            Width = 160,
        };
        _passBox.ValueChanged += (_, _) =>
        {
            var v = double.IsNaN(_passBox.Value) ? 0 : _passBox.Value;
            _passFraction = Math.Clamp(v / 100.0, 0, 1);
        };

        Content = BuildLayout();
        RebuildQuestions();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Padding = new Thickness(12, 4, 12, 8),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        header.Children.Add(_passBox);
        var saveBtn = new Button { Content = AppStrings.OskeFormSave, VerticalAlignment = VerticalAlignment.Bottom };
        saveBtn.Click += (_, _) => Save();
        header.Children.Add(saveBtn);
        header.Children.Add(_status);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _questionsHost };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    private void RebuildQuestions()
    {
        _questionsHost.Children.Clear();
        for (var i = 0; i < _questions.Count; i++)
            _questionsHost.Children.Add(BuildQuestionCard(_questions[i], i));

        var addBtn = new Button { Content = AppStrings.OskeFormAddQuestion, Margin = new Thickness(0, 4, 0, 0) };
        addBtn.Click += (_, _) =>
        {
            _questions.Add(new EditQuestion
            {
                Id = NewId("q_"),
                Title = string.Empty,
                Kind = OskeAnswerKind.Single,
                Options = new List<EditOption> { new() { Id = NewId("o_"), Text = string.Empty } },
            });
            RebuildQuestions();
        };
        _questionsHost.Children.Add(addBtn);
    }

    private UIElement BuildQuestionCard(EditQuestion q, int index)
    {
        var stack = new StackPanel { Spacing = 6 };

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var num = new TextBlock
        {
            Text = $"{index + 1}.",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        Grid.SetColumn(num, 0);
        top.Children.Add(num);

        var titleBox = new TextBox { Text = q.Title, PlaceholderText = AppStrings.OskeFormBlockTitle };
        titleBox.TextChanged += (_, _) => q.Title = titleBox.Text;
        Grid.SetColumn(titleBox, 1);
        top.Children.Add(titleBox);

        var kindBox = new ComboBox { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        kindBox.Items.Add(new ComboBoxItem { Content = AppStrings.OskeFormSingle, Tag = OskeAnswerKind.Single });
        kindBox.Items.Add(new ComboBoxItem { Content = AppStrings.OskeFormMulti, Tag = OskeAnswerKind.Multi });
        kindBox.SelectedIndex = q.Kind == OskeAnswerKind.Multi ? 1 : 0;
        kindBox.SelectionChanged += (_, _) =>
        {
            if (kindBox.SelectedItem is ComboBoxItem it) q.Kind = (OskeAnswerKind)it.Tag;
        };
        Grid.SetColumn(kindBox, 2);
        top.Children.Add(kindBox);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var up = SmallButton("▲");
        up.IsEnabled = index > 0;
        up.Click += (_, _) => Move(index, -1);
        var down = SmallButton("▼");
        down.IsEnabled = index < _questions.Count - 1;
        down.Click += (_, _) => Move(index, +1);
        var del = SmallButton("✕");
        del.Click += (_, _) => { _questions.RemoveAt(index); RebuildQuestions(); };
        controls.Children.Add(up);
        controls.Children.Add(down);
        controls.Children.Add(del);
        Grid.SetColumn(controls, 3);
        top.Children.Add(controls);

        stack.Children.Add(top);

        foreach (var opt in q.Options)
        {
            var captured = opt;
            var row = new Grid { Margin = new Thickness(16, 0, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var optBox = new TextBox { Text = captured.Text, PlaceholderText = AppStrings.OskeFormOption };
            optBox.TextChanged += (_, _) => captured.Text = optBox.Text;
            Grid.SetColumn(optBox, 0);
            row.Children.Add(optBox);
            var delOpt = SmallButton("✕");
            delOpt.Margin = new Thickness(4, 0, 0, 0);
            delOpt.Click += (_, _) => { q.Options.Remove(captured); RebuildQuestions(); };
            Grid.SetColumn(delOpt, 1);
            row.Children.Add(delOpt);
            stack.Children.Add(row);
        }

        var addOpt = new Button { Content = AppStrings.OskeFormAddOption, Margin = new Thickness(16, 2, 0, 0) };
        addOpt.Click += (_, _) =>
        {
            q.Options.Add(new EditOption { Id = NewId("o_"), Text = string.Empty });
            RebuildQuestions();
        };
        stack.Children.Add(addOpt);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Colors.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Child = stack,
        };
    }

    private static Button SmallButton(string glyph) =>
        new() { Content = glyph, Padding = new Thickness(6, 2, 6, 2), MinWidth = 0 };

    private void Move(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _questions.Count) return;
        (_questions[index], _questions[target]) = (_questions[target], _questions[index]);
        RebuildQuestions();
    }

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private void Save()
    {
        var formId = OskeForms.FormIdFor(_specialty);
        // Keep the form's canonical specialty stable (Cardiology owns the shared Cardiology/ФД form).
        var canonical = formId == OskeForms.TherapyFormId ? OskeSpecialty.Therapy : OskeSpecialty.Cardiology;

        var questions = _questions
            .Where(q => !string.IsNullOrWhiteSpace(q.Title) && q.Options.Any(o => !string.IsNullOrWhiteSpace(o.Text)))
            .Select((q, i) => new OskeQuestion(
                q.Id,
                i + 1,
                q.Title.Trim(),
                q.Kind,
                q.Options
                    .Where(o => !string.IsNullOrWhiteSpace(o.Text))
                    .Select(o => new OskeOption(o.Id, o.Text.Trim()))
                    .ToList()))
            .ToList();

        var form = new OskeForm(formId, canonical, $"{DateTime.Now:yyyy.MM.dd}", questions, _passFraction);
        _status.Text = _repo.WriteForm(form) ? AppStrings.OskeFormSaved : string.Empty;
    }
}
