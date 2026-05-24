using System.Net.Sockets;
using System.Text;
using CardioSimulator.App.Data;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using CardioSimulator.Core.Network;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Windows.Storage;
using TcpState = CardioSimulator.Core.Network.TcpConnectionState;

namespace CardioSimulator.App.ViewModels;

/// <summary>
/// Central application view-model. Faithful port of the Android <c>AppViewModel</c>:
/// owns the <see cref="PathologyRepository"/> + <see cref="DataSourcePrefs"/>, the
/// <see cref="DataState"/>/<see cref="IsDataConfirmed"/> gate, the selected operating        
/// mode / language / theme, and the TCP link (connect + auto-upload + reconnect loop +       
/// start/stop commands). Persisted settings are restored on construction.
/// </summary>
public partial class AppViewModel : ObservableObject
{
    public PathologyRepository Repository { get; }
    public DataSourcePrefs Prefs { get; }

    private readonly AppStateModel _appState;
    private readonly DispatcherQueue? _dispatcher;
    private readonly int _tcpReconnectIntervalMs;

    /// <summary>The five operating modes, in declaration order.</summary>
    public IReadOnlyList<OperatingModeModel> OperatingModes => _appState.OperatingModes;      

    [ObservableProperty]
    private OperatingModeModel _selectedOperatingMode;

    [ObservableProperty]
    private DataState _dataState = new DataState.NotConfigured();

    [ObservableProperty]
    private bool _isDataConfirmed;

    [ObservableProperty]
    private Language _selectedLanguage = Language.EN;

    [ObservableProperty]
    private bool _isDarkTheme = true;

    [ObservableProperty]
    private string _tcpIp = "192.168.1.100";

    [ObservableProperty]
    private int _tcpPort = 8080;

    [ObservableProperty]
    private TcpState _tcpConnectionState = new TcpState.Disconnected();

    public AppViewModel(int tcpReconnectIntervalMs = 5000)
    {
        _tcpReconnectIntervalMs = tcpReconnectIntervalMs;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Prefs = new DataSourcePrefs();
        Repository = new PathologyRepository(new FilePathologySource(AppPaths.PathologiesDir));

        var builder = new AppBuilder();
        foreach (var mode in Enum.GetValues<OperatingMode>())
        {
            builder.AddMode(new OperatingModeModel(mode));
        }
        _appState = builder.Build();

        // Restore persisted settings (language / theme / TCP target / last mode).
        if (Languages.FromTag(Prefs.LanguageTag) is { } savedLanguage)
        {
            _appState.UpdateLanguage(savedLanguage);
        }
        if (Prefs.TcpIp is { } savedIp || Prefs.TcpPort is not null)
        {
            _appState.UpdateTcpConnection(Prefs.TcpIp ?? _appState.TcpIp, Prefs.TcpPort ?? _appState.TcpPort);
        }
        if (ParseSavedMode() is { } savedMode)
        {
            _appState.UpdateMode(savedMode);
        }

        _selectedOperatingMode = _appState.SelectedOperatingMode;
        _selectedLanguage = _appState.SelectedLanguage;
        _tcpIp = _appState.TcpIp;
        _tcpPort = _appState.TcpPort;
        _isDarkTheme = Prefs.DarkTheme ?? true;
    }

    private OperatingModeModel? ParseSavedMode()
    {
        if (Prefs.LastOperatingMode is not { } name) return null;
        if (!Enum.TryParse<OperatingMode>(name, out var id)) return null;
        foreach (var mode in _appState.OperatingModes)
        {
            if (mode.Id == id) return mode;
        }
        return null;
    }

    // â”€â”€ Operating mode / language / theme â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public void UpdateOperatingMode(OperatingModeModel mode)
    {
        _appState.UpdateMode(mode);
        SelectedOperatingMode = mode;
        Prefs.LastOperatingMode = mode.Id.ToString();
    }

    public void UpdateLanguage(Language language, bool persist = true)
    {
        if (SelectedLanguage == language) return;
        _appState.UpdateLanguage(language);
        SelectedLanguage = language;
        if (persist) Prefs.LanguageTag = language.Tag();
    }

    public void UpdateDarkTheme(bool isDark)
    {
        IsDarkTheme = isDark;
        Prefs.DarkTheme = isDark;
    }

    public void UpdateTcpConnection(string ip, int port)
    {
        _appState.UpdateTcpConnection(ip, port);
        TcpIp = ip;
        TcpPort = port;
        Prefs.TcpIp = ip;
        Prefs.TcpPort = port;
    }

    // â”€â”€ Data lifecycle â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// If a dataset was previously extracted and is still valid, load it and
    /// auto-confirm. If extraction is missing but the ZIP path is known, try
    /// to re-extract (Android parity).
    /// </summary>
    public async void TryLoadSaved()
    {
        var source = new FilePathologySource(AppPaths.PathologiesDir);
        if (source.IsValid())
        {
            Repository.SetSource(source);
            if (Reload())
            {
                IsDataConfirmed = true;
                return;
            }
        }

        // extraction missing/invalid; try re-extracting if we have a saved ZIP path
        if (Prefs.TreeUri is { } zipPath && File.Exists(zipPath))
        {
            try
            {
                var zipFile = await StorageFile.GetFileFromPathAsync(zipPath);
                await SetDataFolderAsync(zipFile);
                if (IsDataConfirmed) return;
            }
            catch
            {
                // fallback to data-source screen
            }
        }
    }

    /// <summary>
    /// Persists the picked ZIP, extracts it into the app data folder, swaps the
    /// repository to the extracted source, and reloads the manifest.
    /// </summary>
    public async Task SetDataFolderAsync(StorageFile zip)
    {
        IsDataConfirmed = false;
        Prefs.TreeUri = zip.Path;
        DataState = new DataState.Loading();

        var extracted = await Task.Run(() => ZipExtractor.Extract(zip.Path, AppPaths.PathologiesDir));
        if (!extracted)
        {
            DataState = new DataState.Error(DataState.ErrorReason.Unreadable);
            return;
        }

        var source = new FilePathologySource(AppPaths.PathologiesDir);
        if (!source.IsValid())
        {
            DataState = new DataState.Error(DataState.ErrorReason.Empty);
            return;
        }

        Repository.SetSource(source);
        Reload();
    }

    public void ConfirmData() => IsDataConfirmed = true;

    private bool Reload()
    {
        if (!Repository.LoadManifest())
        {
            DataState = new DataState.Error(DataState.ErrorReason.BadManifest);
            return false;
        }

        var count = Repository.Pathologies().Count;
        if (count == 0)
        {
            DataState = new DataState.Error(DataState.ErrorReason.Empty);
            return false;
        }

        DataState = new DataState.Ready(count);
        return true;
    }

    /// <summary>Re-packs the current dataset (with edits) to a user-chosen path.</summary>   
    public Task ExportZipAsync(string destPath) =>
        Task.Run(() => ZipCompressor.Zip(AppPaths.PathologiesDir, destPath));

    // â”€â”€ TCP link â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Socket? _tcpSocket;
    private CancellationTokenSource? _connectionCts;

    public void ToggleTcpConnection()
    {
        if (TcpConnectionState is TcpState.Disconnected or TcpState.Error)
        {
            ConnectTcp();
        }
        else
        {
            DisconnectTcp();
        }
    }

    public void DismissTcpError()
    {
        if (TcpConnectionState is TcpState.Error)
        {
            TcpConnectionState = new TcpState.Disconnected();
        }
    }

    private void ConnectTcp()
    {
        _connectionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _connectionCts = cts;
        var ip = TcpIp;
        var port = TcpPort;
        _ = Task.Run(() => ConnectionLoopAsync(ip, port, cts.Token));
    }

    private void DisconnectTcp()
    {
        _connectionCts?.Cancel();
        try { _tcpSocket?.Close(); } catch { /* ignore */ }
        _tcpSocket = null;
        SetConnectionState(new TcpState.Disconnected());
    }

    private async Task ConnectionLoopAsync(string ip, int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SetConnectionState(new TcpState.Connecting());
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);   
                connectCts.CancelAfter(_tcpReconnectIntervalMs);
                await socket.ConnectAsync(ip, port, connectCts.Token);

                _tcpSocket = socket;
                SetConnectionState(new TcpState.Connected());

                await SendUploadArchiveAsync(socket, ct);

                // Drain incoming bytes so a socket EOF (disconnect) is detected.
                var buffer = new byte[1024];
                while (!ct.IsCancellationRequested)
                {
                    var read = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);       
                    if (read == 0) break;
                }
            }
            catch
            {
                // Connection lost or failed to connect â€” fall through to retry.
            }
            finally
            {
                try { socket.Close(); } catch { /* ignore */ }
                if (_tcpSocket == socket) _tcpSocket = null;
            }

            if (!ct.IsCancellationRequested)
            {
                SetConnectionState(new TcpState.Disconnected());
                try { await Task.Delay(_tcpReconnectIntervalMs, ct); } catch { break; }       
            }
        }
    }

    /// <summary>On every connect, uploads the current dataset snapshot (header + raw ZIP bytes).</summary>
    private async Task SendUploadArchiveAsync(Socket socket, CancellationToken ct)
    {
        var zipPath = await Task.Run(() => ZipCompressor.ZipToTemp(AppPaths.PathologiesDir, "upload.zip"), ct);
        if (zipPath is null) return;

        await _sendLock.WaitAsync(ct);
        try
        {
            var size = new FileInfo(zipPath).Length;
            var msg = new TcpMessage.UploadMessage
            {
                Id = Guid.NewGuid().ToString(),
                Filename = "Pathologies.zip",
                Size = size,
            };
            var header = TcpProtocol.Encode(msg) + "\n";
            await socket.SendAsync(Encoding.UTF8.GetBytes(header), SocketFlags.None, ct);     

            await using var fs = File.OpenRead(zipPath);
            var buffer = new byte[81920];
            int read;
            while ((read = await fs.ReadAsync(buffer, ct)) > 0)
            {
                await socket.SendAsync(buffer.AsMemory(0, read), SocketFlags.None, ct);       
            }
        }
        catch
        {
            // best-effort upload
        }
        finally
        {
            _sendLock.Release();
            try { File.Delete(zipPath); } catch { /* ignore */ }
        }
    }

    public void SendStartCommand(string? pathology = null, string? name = null)
    {
        var socket = _tcpSocket;
        if (socket is null || TcpConnectionState is not TcpState.Connected) return;

        _ = Task.Run(async () =>
        {
            await _sendLock.WaitAsync();
            try
            {
                var paramsMap = new Dictionary<string, string>();
                if (pathology is not null) paramsMap["pathology"] = pathology;
                if (name is not null) paramsMap["name"] = name;
                var msg = new TcpMessage.StartCommand
                {
                    Id = Guid.NewGuid().ToString(),
                    SampleRate = null,
                    Params = paramsMap,
                };
                var bytes = Encoding.UTF8.GetBytes(TcpProtocol.Encode(msg) + "\n");
                await socket.SendAsync(bytes, SocketFlags.None);
            }
            catch { /* ignore */ }
            finally { _sendLock.Release(); }
        });
    }

    public void SendStopCommand()
    {
        var socket = _tcpSocket;
        if (socket is null || TcpConnectionState is not TcpState.Connected) return;

        _ = Task.Run(async () =>
        {
            await _sendLock.WaitAsync();
            try
            {
                var msg = new TcpMessage.StopCommand { Id = Guid.NewGuid().ToString() };      
                var bytes = Encoding.UTF8.GetBytes(TcpProtocol.Encode(msg) + "\n");
                await socket.SendAsync(bytes, SocketFlags.None);
            }
            catch { /* ignore */ }
            finally { _sendLock.Release(); }
        });
    }

    /// <summary>Marshals a connection-state change onto the UI thread (sockets run on the pool).</summary>
    private void SetConnectionState(TcpState state)
    {
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => TcpConnectionState = state);
        }
        else
        {
            TcpConnectionState = state;
        }
    }
}
