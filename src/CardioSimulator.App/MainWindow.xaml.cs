using System.ComponentModel;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.Screens;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace CardioSimulator.App;

public sealed partial class MainWindow : Window
{
    private readonly AppViewModel _appViewModel = new();
    private readonly DataSourceScreen _dataSourceScreen = new();
    private MainScreen? _mainScreen;
    private WelcomeOverlay? _welcomeOverlay;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Cardio Simulator";
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
                break;
        }
    }

    private void ApplyTheme() =>
        Root.RequestedTheme = _appViewModel.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;

    private void UpdateRoot()
    {
        Root.Children.Clear();
        if (!_appViewModel.IsDataConfirmed)
        {
            Root.Children.Add(_dataSourceScreen);
            return;
        }

        if (_mainScreen is null)
        {
            _mainScreen = new MainScreen();
            Root.Children.Add(_mainScreen);
            _mainScreen.Initialize(_appViewModel, PickZipAsync, PickSaveZipAsync, PickOpenImageAsync, PickOpenWfdbAsync, PickOpenJsonAsync, PickSaveJsonAsync);
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
        if (_mainScreen is null || _appViewModel.Prefs.WelcomeShown == true) return;

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
        _appViewModel.Prefs.WelcomeShown = true;
        if (_welcomeOverlay is not null) Root.Children.Remove(_welcomeOverlay);
        if (_mainScreen is not null) _mainScreen.Visibility = Visibility.Visible;
    }

    private async Task<StorageFile?> PickZipAsync()
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add(".zip");
        return await picker.PickSingleFileAsync();
    }

    private async Task<StorageFile?> PickSaveZipAsync()
    {
        var picker = new FileSavePicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeChoices.Add("ZIP archive", new List<string> { ".zip" });
        picker.SuggestedFileName = "ecg_export";
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
