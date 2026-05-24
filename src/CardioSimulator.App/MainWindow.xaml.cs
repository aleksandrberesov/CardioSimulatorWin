using System.ComponentModel;
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

        _dataSourceScreen.Initialize(_appViewModel, PickZipAsync);
        _appViewModel.PropertyChanged += OnAppViewModelChanged;

        _appViewModel.TryLoadSaved();
        UpdateRoot();
    }

    private void OnAppViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.IsDataConfirmed)) UpdateRoot();
    }

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
            _mainScreen.Initialize(_appViewModel);
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
}
