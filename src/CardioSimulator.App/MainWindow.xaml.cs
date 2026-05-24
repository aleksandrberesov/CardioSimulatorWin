using System.ComponentModel;
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

    public MainWindow()
    {
        InitializeComponent();
        Title = "CardioSimulator";
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
            _mainScreen.Initialize(_appViewModel, PickZipAsync, PickSaveZipAsync);
        }
        else
        {
            Root.Children.Add(_mainScreen);
        }
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
}
