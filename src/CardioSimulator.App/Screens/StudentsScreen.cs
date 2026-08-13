using System;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Theming;
using CardioSimulator.App.ViewModels;
using CardioSimulator.Core.Domain;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CardioSimulator.App.Screens;

/// <summary>
/// Full-edition «Регистрация студентов» screen: a form to add a student (ФИО + группа + optional
/// e-mail) onto the persisted roster, and a list of already-registered students with per-row delete.
/// All state lives in <see cref="StudentRegistrationViewModel"/>; the roster list re-renders on
/// <see cref="StudentRegistrationViewModel.StateChanged"/> and the whole page rebuilds on theme change
/// (language changes rebuild the screen from <c>MainScreen</c>). The registration screen is Full-only
/// — the mode itself is filtered out of the Limited build (see <c>OperatingModes.IsFullEditionOnly</c>).
/// </summary>
public sealed class StudentsScreen : UserControl
{
    private static readonly Color Red = Color.FromArgb(0xFF, 0xD3, 0x3A, 0x2F);

    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(16, 12, 16, 24),
    };

    private StudentRegistrationViewModel? _vm;

    // Form controls — kept so the Register handler can read/clear them and toggle the button.
    private TextBox? _fullName;
    private TextBox? _group;
    private TextBox? _email;
    private Button? _registerButton;
    private TextBlock? _status;

    // Roster section — re-rendered on its own so adding/removing doesn't disturb the form.
    private TextBlock? _rosterHeader;
    private StackPanel? _rosterHost;

    public StudentsScreen()
    {
        Content = _scroll;
        Loaded += (_, _) => AppTheme.Changed += OnThemeChanged;
        Unloaded += OnUnloaded;
    }

    public void Initialize(StudentRegistrationViewModel vm)
    {
        _vm = vm;
        vm.StateChanged += RenderRoster;
        BuildPage();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AppTheme.Changed -= OnThemeChanged;
        if (_vm is not null) _vm.StateChanged -= RenderRoster;
    }

    private void OnThemeChanged() => BuildPage();

    // ── Page ──────────────────────────────────────────────────────────────────

    private void BuildPage()
    {
        if (_vm is null) return;

        var page = new StackPanel { Spacing = 16, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch };
        page.Children.Add(BuildForm());
        page.Children.Add(BuildRosterCard());
        _scroll.Content = page;
        RenderRoster();
    }

    private static Border Card(UIElement child) => new()
    {
        Background = AppTheme.AppCardBackground,
        BorderBrush = AppTheme.AppCardBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(20),
        Child = child,
    };

    private UIElement BuildForm()
    {
        var stack = new StackPanel { Spacing = 12 };

        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.StudentsTitle,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.AppTextPrimary,
        });
        stack.Children.Add(new TextBlock
        {
            Text = AppStrings.StudentsSubtitle,
            FontSize = 13,
            Foreground = AppTheme.AppTextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -4, 0, 4),
        });

        _fullName = Field(AppStrings.ExamFieldFullName);
        _group = Field(AppStrings.ExamFieldGroup);
        _email = Field(AppStrings.StudentsFieldEmail);
        stack.Children.Add(_fullName);
        stack.Children.Add(_group);
        stack.Children.Add(_email);

        _status = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        _registerButton = new Button
        {
            Content = AppStrings.StudentsRegister,
            Background = AppTheme.Accent,
            Foreground = AppTheme.OnAccent,
            Padding = new Thickness(20, 8, 20, 8),
            CornerRadius = new CornerRadius(8),
            IsEnabled = false,
        };
        _registerButton.Click += OnRegisterClick;

        var actionRow = new Grid();
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_status, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_registerButton, 1);
        actionRow.Children.Add(_status);
        actionRow.Children.Add(_registerButton);
        stack.Children.Add(actionRow);

        _fullName.TextChanged += (_, _) => Revalidate();
        _group.TextChanged += (_, _) => Revalidate();
        Revalidate();

        return Card(stack);
    }

    private static TextBox Field(string header) => new()
    {
        Header = header,
        IsSpellCheckEnabled = false,
        IsTextPredictionEnabled = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private void Revalidate()
    {
        if (_registerButton is null) return;
        _registerButton.IsEnabled =
            !string.IsNullOrWhiteSpace(_fullName?.Text) &&
            !string.IsNullOrWhiteSpace(_group?.Text);
    }

    private UIElement BuildRosterCard()
    {
        var stack = new StackPanel { Spacing = 12 };
        _rosterHeader = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.AppTextPrimary,
        };
        _rosterHost = new StackPanel { Spacing = 8 };
        stack.Children.Add(_rosterHeader);
        stack.Children.Add(_rosterHost);
        return Card(stack);
    }

    // ── Roster list ─────────────────────────────────────────────────────────--

    private void RenderRoster()
    {
        if (_vm is null || _rosterHost is null || _rosterHeader is null) return;

        var students = _vm.Students;
        _rosterHeader.Text = $"{AppStrings.StudentsListTitle} ({students.Count})";
        _rosterHost.Children.Clear();

        if (students.Count == 0)
        {
            _rosterHost.Children.Add(new TextBlock
            {
                Text = AppStrings.StudentsEmpty,
                FontSize = 13,
                Foreground = AppTheme.AppTextSecondary,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var student in students)
            _rosterHost.Children.Add(BuildRow(student));
    }

    private UIElement BuildRow(Student student)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = student.FullName,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.AppTextPrimary,
            TextWrapping = TextWrapping.Wrap,
        });

        var detail = student.Group;
        if (!string.IsNullOrWhiteSpace(student.Email)) detail += $" · {student.Email}";
        detail += $" · {student.RegisteredAt.LocalDateTime:yyyy-MM-dd}";
        info.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 12,
            Foreground = AppTheme.AppTextSecondary,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(info, 0);
        row.Children.Add(info);

        var delete = new Button
        {
            Content = "✕",
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Foreground = new SolidColorBrush(Red),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(delete, AppStrings.StudentsRemove);
        var id = student.Id;
        delete.Click += (_, _) => _vm?.Remove(id);
        Grid.SetColumn(delete, 1);
        row.Children.Add(delete);

        return new Border
        {
            Background = AppTheme.AppSubtleFill,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 10, 10),
            Child = row,
        };
    }

    // ── Register ────────────────────────────────────────────────────────────--

    private void OnRegisterClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _fullName is null || _group is null || _email is null || _status is null) return;

        var outcome = _vm.Register(_fullName.Text, _group.Text, _email.Text);
        switch (outcome)
        {
            case RegisterOutcome.Added:
                _fullName.Text = string.Empty;
                _group.Text = string.Empty;
                _email.Text = string.Empty;
                _fullName.Focus(FocusState.Programmatic);
                ShowStatus(AppStrings.StudentsAdded, AppTheme.PositiveColor);
                break;
            case RegisterOutcome.Duplicate:
                ShowStatus(AppStrings.StudentsDuplicate, Red);
                break;
            case RegisterOutcome.Invalid:
                ShowStatus(AppStrings.StudentsInvalid, Red);
                break;
            default:
                ShowStatus(AppStrings.StudentsSaveFailed, Red);
                break;
        }
        Revalidate();
    }

    private void ShowStatus(string text, Color color)
    {
        if (_status is null) return;
        _status.Text = text;
        _status.Foreground = new SolidColorBrush(color);
        _status.Visibility = Visibility.Visible;
    }
}
