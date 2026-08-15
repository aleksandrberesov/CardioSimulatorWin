using System;
using System.Linq;
using System.Threading.Tasks;
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

    // Two-column layout: registration form (left, top-aligned) + roster list (right, its own scroll).
    private readonly Grid _root = new()
    {
        Padding = new Thickness(16, 12, 16, 16),
        ColumnSpacing = 16,
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
        Content = _root;
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

        _root.Children.Clear();
        _root.ColumnDefinitions.Clear();
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var form = BuildForm();
        form.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(form, 0);

        var roster = BuildRosterCard();
        Grid.SetColumn(roster, 1);

        _root.Children.Add(form);
        _root.Children.Add(roster);
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

    private FrameworkElement BuildForm()
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

    private FrameworkElement BuildRosterCard()
    {
        // Header stays put; only the rows scroll — the star row constrains the ScrollViewer height.
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _rosterHeader = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = AppTheme.AppTextPrimary,
            Margin = new Thickness(0, 0, 0, 12),
        };
        Grid.SetRow(_rosterHeader, 0);

        _rosterHost = new StackPanel { Spacing = 8 };
        var listScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // Right gutter so the overlay scrollbar doesn't sit on top of the row action buttons.
            Padding = new Thickness(0, 0, 14, 0),
            Content = _rosterHost,
        };
        Grid.SetRow(listScroll, 1);

        grid.Children.Add(_rosterHeader);
        grid.Children.Add(listScroll);
        return Card(grid);
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

        // Group by группа (alphabetical), students alphabetical by ФИО within each group.
        var groups = students
            .GroupBy(s => s.Group?.Trim() ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCulture);

        foreach (var group in groups)
        {
            var members = group.OrderBy(s => s.FullName, StringComparer.CurrentCulture).ToList();
            _rosterHost.Children.Add(GroupHeader(group.Key, members.Count));
            foreach (var student in members)
                _rosterHost.Children.Add(BuildRow(student));
        }
    }

    private static UIElement GroupHeader(string group, int count) => new TextBlock
    {
        Text = $"{group} ({count})",
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = AppTheme.AppTextSecondary,
        Margin = new Thickness(2, 8, 0, 2),
    };

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

        var edit = new Button
        {
            Content = "✎",
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Foreground = AppTheme.AppTextSecondary,
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(edit, AppStrings.StudentsEdit);
        edit.Click += (_, _) => _ = ShowEditDialogAsync(student);

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

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(edit);
        actions.Children.Add(delete);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);

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

    // ── Edit ────────────────────────────────────────────────────────────────--

    private async Task ShowEditDialogAsync(Student student)
    {
        if (_vm is null || XamlRoot is null) return;

        var name = Field(AppStrings.ExamFieldFullName);
        name.Text = student.FullName;
        var group = Field(AppStrings.ExamFieldGroup);
        group.Text = student.Group;
        var email = Field(AppStrings.StudentsFieldEmail);
        email.Text = student.Email ?? string.Empty;

        var error = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Red),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        panel.Children.Add(name);
        panel.Children.Add(group);
        panel.Children.Add(email);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = AppStrings.StudentsEditTitle,
            Content = panel,
            PrimaryButtonText = AppStrings.CommonSave,
            CloseButtonText = AppStrings.CommonCancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        // Validate on Save; keep the dialog open (args.Cancel) and surface why when the edit is rejected.
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var outcome = _vm.Update(student.Id, name.Text, group.Text, email.Text);
            if (outcome == RegisterOutcome.Added) return;
            args.Cancel = true;
            error.Text = outcome switch
            {
                RegisterOutcome.Duplicate => AppStrings.StudentsDuplicate,
                RegisterOutcome.Invalid => AppStrings.StudentsInvalid,
                _ => AppStrings.StudentsSaveFailed,
            };
            error.Visibility = Visibility.Visible;
        };

        await dialog.ShowAsync();
    }
}
