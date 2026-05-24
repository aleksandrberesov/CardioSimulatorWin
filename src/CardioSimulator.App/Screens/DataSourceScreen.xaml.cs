using System.ComponentModel;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Localization;
using CardioSimulator.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace CardioSimulator.App.Screens;

/// <summary>
/// First-run screen shown until a Pathologies ZIP has been picked and confirmed,
/// or when the previously picked file is no longer usable. Faithful port of the
/// Android <c>DataSourceScreen</c>: pick a ZIP, extract+load it, then Continue.
/// </summary>
public sealed partial class DataSourceScreen : UserControl
{
    private AppViewModel? _appViewModel;
    private Func<Task<StorageFile?>>? _pickZip;

    public DataSourceScreen()
    {
        InitializeComponent();
        TitleText.Text = AppStrings.DataSourceTitle;
        DescText.Text = AppStrings.DataSourceDescription;
        PickButton.Content = AppStrings.DataSourcePickFolder;
        LoadingText.Text = AppStrings.DataSourceLoading;
        RetryButton.Content = AppStrings.DataSourceRetry;
        ContinueButton.Content = AppStrings.DataSourceContinue;
        DetailsButton.Content = AppStrings.DataSourceShowDetails;
        ChangeButton.Content = AppStrings.DataSourceChangeFolder;
    }

    public void Initialize(AppViewModel appViewModel, Func<Task<StorageFile?>> pickZip)
    {
        if (_appViewModel is not null) _appViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _appViewModel = appViewModel;
        _pickZip = pickZip;
        _appViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Render();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppViewModel.DataState)) Render();
    }

    private void Render()
    {
        var state = _appViewModel?.DataState ?? new DataState.NotConfigured();
        NotConfiguredPanel.Visibility = Vis(state is DataState.NotConfigured);
        LoadingPanel.Visibility = Vis(state is DataState.Loading);
        ErrorPanel.Visibility = Vis(state is DataState.Error);
        ReadyPanel.Visibility = Vis(state is DataState.Ready);

        if (state is DataState.Error error)
        {
            ErrorText.Text = error.Reason switch
            {
                DataState.ErrorReason.Unreadable => AppStrings.DataSourceErrorUnreadable,
                DataState.ErrorReason.Empty => AppStrings.DataSourceErrorEmpty,
                DataState.ErrorReason.BadManifest => AppStrings.DataSourceErrorBadManifest,
                _ => AppStrings.DataSourceErrorUnreadable,
            };
        }
        else if (state is DataState.Ready ready)
        {
            ReadyText.Text = AppStrings.DataSourceLoadedFormat(ready.PathologyCount);
        }
    }

    private static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    private async void OnPickClick(object sender, RoutedEventArgs e)
    {
        if (_appViewModel is null || _pickZip is null) return;
        var file = await _pickZip();
        if (file is not null) await _appViewModel.SetDataFolderAsync(file);
    }

    private void OnContinueClick(object sender, RoutedEventArgs e) => _appViewModel?.ConfirmData();

    private async void OnDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_appViewModel is null) return;

        var rhythmViewModel = new RhythmViewModel(_appViewModel.Repository);
        await rhythmViewModel.LoadManifestAsync();

        var panel = new RhythmChoosingPanel { DisplayLanguage = _appViewModel.SelectedLanguage, Height = 400 };
        panel.SetRhythms(rhythmViewModel.Rhythms);

        var dialog = new ContentDialog
        {
            Title = AppStrings.DataSourcePathologiesTitle(rhythmViewModel.Rhythms.Count),
            Content = panel,
            CloseButtonText = AppStrings.DataSourceClose,
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
