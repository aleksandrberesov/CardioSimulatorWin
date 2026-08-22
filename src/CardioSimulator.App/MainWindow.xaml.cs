using System.ComponentModel;
using System.Globalization;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Screens;
using CardioSimulator.App.Security;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace CardioSimulator.App;

public sealed partial class MainWindow : Window
{
    /// <summary>Show the "demo ending soon" nag once the window opens when this many days (or fewer)
    /// remain. Only applies to a time-limited demo build (see <see cref="DemoGuard"/>).</summary>
    private const int DemoNagThresholdDays = 5;

    private readonly AppViewModel _appViewModel = new();
    private readonly DataSourceScreen _dataSourceScreen = new();
    private readonly DemoStatus _demoStatus;
    private readonly ExamSecurityGuard _securityGuard;
    private MainScreen? _mainScreen;
    private WelcomeOverlay? _welcomeOverlay;
    private bool _demoNagPending;

    public MainWindow()
    {
        InitializeComponent();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _securityGuard = new ExamSecurityGuard(this, hwnd);

        // Evaluate the time-limited-demo gate once (also advances the anti-rollback high-water mark).
        // For a normal perpetual build this is inert (IsDemo == false) and nothing below changes.
        _demoStatus = DemoGuard.Evaluate();
        _demoNagPending = _demoStatus is { IsDemo: true, IsExpired: false }
                          && _demoStatus.DaysRemaining >= 0
                          && _demoStatus.DaysRemaining <= DemoNagThresholdDays;

        // The window title is the app name shown top-left in the title bar; the auto-incrementing
        // build version rides alongside it there (BuildInfo is regenerated on every build). A demo
        // build appends its remaining-days / expired status.
        RefreshTitle();
        AppWindow.Resize(new SizeInt32(1200, 850));

        AppStrings.Current = _appViewModel.SelectedLanguage;
        ApplyTheme();

        _dataSourceScreen.Initialize(_appViewModel, PickZipAsync);
        _appViewModel.PropertyChanged += OnAppViewModelChanged;

        _appViewModel.TryLoadSaved();
        UpdateRoot();
    }

    private void OnAppViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.IsDataConfirmed):
                UpdateRoot();
                break;
            case nameof(AppViewModel.IsDarkTheme):
                ApplyTheme();
                break;
            case nameof(AppViewModel.SelectedLanguage):
                AppStrings.Current = _appViewModel.SelectedLanguage;
                RefreshTitle(); // re-localize the demo suffix, if any
                break;
        }
    }

    /// <summary>Sets the window title to "{Name}  v{FullVersion}", plus a localized demo suffix
    /// ("DEMO — N days left" / "DEMO EXPIRED") on a time-limited demo build. No-op suffix otherwise.</summary>
    private void RefreshTitle()
    {
        var title = $"{BuildInfo.Name}  v{BuildInfo.FullVersion}";
        if (_demoStatus.IsDemo)
        {
            var suffix = _demoStatus.IsExpired
                ? AppStrings.DemoTitleExpired
                : _demoStatus.DaysRemaining <= 0
                    ? AppStrings.DemoTitleLastDay
                    : AppStrings.DemoTitleDaysLeft(_demoStatus.DaysRemaining);
            title = $"{title}  •  {suffix}";
        }
        Title = title;
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _appViewModel.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        Theming.AppTheme.Set(_appViewModel.IsDarkTheme);
    }

    private void UpdateRoot()
    {
        Root.Children.Clear();

        // A time-limited demo that has passed its window is a hard stop: the "expired" block replaces
        // the whole shell (even the data-source screen) and the only way forward is Exit.
        if (_demoStatus.IsExpired)
        {
            var expired = new DemoExpiredOverlay(_demoStatus);
            expired.ExitRequested += (_, _) => Close();
            Root.Children.Add(expired);
            return;
        }

        if (!_appViewModel.IsDataConfirmed)
        {
            Root.Children.Add(_dataSourceScreen);
            return;
        }

        if (_mainScreen is null)
        {
            _mainScreen = new MainScreen();
            _mainScreen.Loaded += (_, _) => TryShowDemoNag();
            Root.Children.Add(_mainScreen);
            _mainScreen.Initialize(_appViewModel, _securityGuard, PickZipAsync, PickSaveZipAsync, PickOpenImageAsync, PickOpenWfdbAsync, PickOpenJsonAsync, PickSaveJsonAsync);
        }
        else
        {
            Root.Children.Add(_mainScreen);
        }

        MaybeShowWelcome();
    }

    /// <summary>
    /// On first launch, floats the <see cref="WelcomeOverlay"/> over the (default) Teaching shell.
    /// The shell is hidden behind it while it shows: the monitor's Win2D surface and the lecture
    /// WebView2 are native airspace controls that render above XAML, so a translucent overlay would
    /// be occluded — an opaque welcome with the shell collapsed is the reliable approach.
    /// </summary>
    private void MaybeShowWelcome()
    {
        if (_mainScreen is null || _appViewModel.Prefs.WelcomeDisabled == true) return;

        if (_welcomeOverlay is null)
        {
            _welcomeOverlay = new WelcomeOverlay();
            _welcomeOverlay.Started += OnWelcomeStarted;
        }
        if (!Root.Children.Contains(_welcomeOverlay))
        {
            Root.Children.Add(_welcomeOverlay); // added last ⇒ on top
        }
        _mainScreen.Visibility = Visibility.Collapsed;
    }

    private void OnWelcomeStarted(object? sender, EventArgs e)
    {
        _appViewModel.Prefs.WelcomeDisabled = _welcomeOverlay?.DontShowAgain == true;
        if (_welcomeOverlay is not null) Root.Children.Remove(_welcomeOverlay);
        if (_mainScreen is not null) _mainScreen.Visibility = Visibility.Visible;
        TryShowDemoNag(); // the welcome held the nag back; the shell is now interactive
    }

    /// <summary>
    /// Shows the "demo ending soon" dialog once per launch when the trial is in its final days. Held
    /// back while the first-launch welcome overlay is up (it re-fires from <see cref="OnWelcomeStarted"/>)
    /// so the two never stack, and skipped entirely once shown or on a non-demo build.
    /// </summary>
    private void TryShowDemoNag()
    {
        if (!_demoNagPending) return;
        if (_welcomeOverlay is not null && Root.Children.Contains(_welcomeOverlay)) return;
        if (Root.XamlRoot is null) return;
        _demoNagPending = false;
        _ = ShowDemoNagAsync();
    }

    private async Task ShowDemoNagAsync()
    {
        var expiry = _demoStatus.ExpiryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var body = _demoStatus.DaysRemaining <= 0
            ? AppStrings.DemoNagLastDayBody(expiry)
            : AppStrings.DemoNagBody(_demoStatus.DaysRemaining, expiry);

        var dialog = new ContentDialog
        {
            Title = AppStrings.DemoNagTitle,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = AppStrings.DemoNagContinue,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Theming.AppTheme.Current,
        };
        try { await dialog.ShowAsync(); }
        catch { /* a modal may already be open at launch; the title-bar countdown still conveys it */ }
    }

    private async Task<StorageFile?> PickZipAsync()
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        // Encrypted content packs only. The loader re-checks the pack magic, so this filter is a
        // convenience, not the gate: a .zip renamed to .pak is still rejected on load.
        picker.FileTypeFilter.Add(".pak");
        return await picker.PickSingleFileAsync();
    }

    private async Task<StorageFile?> PickSaveZipAsync(string suggestedFileName)
    {
        var picker = new FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        // Exports are always encrypted content packs — the only format the app can read back.
        picker.FileTypeChoices.Add("Encrypted content pack", new List<string> { ".pak" });
        picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(suggestedFileName);
        return await picker.PickSaveFileAsync();
    }

    private async Task<StorageFile?> PickOpenImageAsync()
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");
        return await picker.PickSingleFileAsync();
    }

    private async Task<StorageFile?> PickOpenWfdbAsync()
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add(".hea");
        picker.FileTypeFilter.Add(".mat");
        picker.FileTypeFilter.Add(".dat");
        return await picker.PickSingleFileAsync();
    }

    private async Task<StorageFile?> PickOpenJsonAsync()
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add(".json");
        return await picker.PickSingleFileAsync();
    }

    private async Task<StorageFile?> PickSaveJsonAsync()
    {
        var picker = new FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        picker.SuggestedFileName = "question_bank";
        return await picker.PickSaveFileAsync();
    }
}
