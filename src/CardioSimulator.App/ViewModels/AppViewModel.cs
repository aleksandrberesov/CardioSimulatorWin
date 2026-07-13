using System.Net.Sockets;
using System.Text;
using CardioSimulator.App.Data;
using CardioSimulator.App.Localization;
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

    public CourseRepository CourseRepository { get; }
    public CourseConstructorViewModel CourseConstructorViewModel { get; }
    public CourseViewerViewModel CourseViewerViewModel { get; }

    /// <summary>OSCE form templates + per-ECG answer keys (seeded on first run).</summary>
    public OskeRepository OskeRepository { get; }

    /// <summary>Persisted OSCE attempt results (one JSON per attempt).</summary>
    public OskeResultStore OskeResultStore { get; }

    /// <summary>Self-assessment tests for the Testing screen + constructor (seeded on first run).</summary>
    public TestRepository TestRepository { get; }

    /// <summary>The standing question bank — the authoring source of truth that tests snapshot from,
    /// and the JSON import/export target for AI-generated questions.</summary>
    public QuestionBankRepository QuestionBank { get; }

    /// <summary>The editable theme catalog for the question bank.</summary>
    public TestThemeStore Themes { get; }

    /// <summary>Persisted examination attempt results (one JSON per attempt).</summary>
    public ExamResultStore ExamResultStore { get; }

    /// <summary>The Group-mode LAN quiz server (QR → student phones). App-lifetime so a session
    /// survives switching screens; started/stopped from the Examination screen.</summary>
    public Network.GroupTestServer GroupTestServer { get; }

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

    /// <summary>Phase heading shown atop the data-source loading bar
    /// (e.g. "Preparing…", "Extracting ECG records", "Loading pathology list…").</summary>
    [ObservableProperty]
    private string _loadingTitle = string.Empty;

    /// <summary>Count/percent line under the loading bar (e.g. "123 / 540 records · 23%").
    /// Empty during indeterminate phases.</summary>
    [ObservableProperty]
    private string _loadingStatus = string.Empty;

    /// <summary>The item currently being processed (e.g. the record file name being
    /// extracted). Empty when there is nothing item-specific to show.</summary>
    [ObservableProperty]
    private string _loadingDetail = string.Empty;

    /// <summary>Extraction progress in percent (0–100) for the loading bar; only
    /// meaningful while <see cref="LoadingIsIndeterminate"/> is false.</summary>
    [ObservableProperty]
    private double _loadingProgress;

    /// <summary>True while the loading bar has no measurable progress (preparing /
    /// reading the manifest); false once per-record extraction progress is known.</summary>
    [ObservableProperty]
    private bool _loadingIsIndeterminate = true;

    /// <summary>True while an extraction is in progress and can be aborted — drives the
    /// visibility of the loading screen's Cancel button.</summary>
    [ObservableProperty]
    private bool _canCancelLoading;

    [ObservableProperty]
    private Language _selectedLanguage = Language.EN;

    [ObservableProperty]
    private bool _isDarkTheme = true;

    /// <summary>Pins the teaching rhythm drawer open so the monitor lays out beside it
    /// (Android <c>isDrawerFixed</c>). Persisted across launches.</summary>
    [ObservableProperty]
    private bool _isDrawerFixed;

    /// <summary>Sentinel course id meaning "show all rhythms" (no course filter).</summary>
    public const string AllRhythmsId = "__all_rhythms__";

    /// <summary>Available teaching courses (mirrors <see cref="CourseRepository"/>'s manifest).</summary>
    [ObservableProperty]
    private IReadOnlyList<CourseEntry> _courses = Array.Empty<CourseEntry>();

    /// <summary>Selected teaching course id; null or <see cref="AllRhythmsId"/> means no filter.</summary>
    [ObservableProperty]
    private string? _selectedCourseId;

    /// <summary>Pathology ids of the selected course, or null when no course filter is active.</summary>
    public IReadOnlyList<string>? SelectedCoursePathologies =>
        SelectedCourseId is null || SelectedCourseId == AllRhythmsId
            ? null
            : Courses.FirstOrDefault(c => c.Id == SelectedCourseId)?.Pathologies;

    /// <summary>Selects a teaching course (null/<see cref="AllRhythmsId"/> clears the filter); persisted.</summary>
    public void SelectCourse(string? courseId)
    {
        var normalized = courseId == AllRhythmsId ? null : courseId;
        if (SelectedCourseId == normalized) return;
        SelectedCourseId = normalized;
        Prefs.LastCourseId = normalized;
    }

    /// <summary>
    /// If true, the next transition to Teaching mode will preserve the SelectedCourseId
    /// instead of resetting it to null.
    /// </summary>
    public bool PreserveCourseSelection { get; set; }

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
        CourseRepository = new CourseRepository(new FileCourseSource(AppPaths.CoursesDir));
        CourseViewerViewModel = new CourseViewerViewModel(CourseRepository);
        CourseConstructorViewModel = new CourseConstructorViewModel(CourseRepository);

        OskeRepository = new OskeRepository(new FileOskeSource(AppPaths.OskeDir));
        OskeResultStore = new OskeResultStore(AppPaths.OskeResultsDir);

        TestRepository = new TestRepository(new FileTestSource(AppPaths.TestsDir));
        QuestionBank = new QuestionBankRepository(new FileQuestionBankSource(AppPaths.QuestionBankDir));
        Themes = new TestThemeStore(AppPaths.TestThemesFile);
        ExamResultStore = new ExamResultStore(AppPaths.ExamResultsDir);
        GroupTestServer = new Network.GroupTestServer(() => QuestionBank.Questions, ExamResultStore);
        // Seed the demo test + question bank once the pathology manifest is available (their questions
        // reference real ECG ids), covering every load path. Harmless on subsequent loads (guarded +
        // only-if-empty).
        Repository.ManifestChanged += (_, _) => SeedSampleTestIfNeeded();
        // Refresh the rhythm-group catalog from the dataset's bundled groups.txt whenever the
        // manifest (re)loads — extraction writes groups.txt alongside manifest.txt.
        Repository.ManifestChanged += (_, _) => PathologyGroups.Load(AppPaths.PathologiesDir);
        PathologyGroups.Load(AppPaths.PathologiesDir);

        // Keep the teaching course list in sync with the course manifest, and restore the
        // last selected course (drives the course-aware rhythm filter in Teaching mode).
        _courses = CourseRepository.Courses;
        // ManifestChanged can fire on a background thread (a course save writes on Task.Run). Marshal
        // the bound-list update to the UI thread so its PropertyChanged doesn't touch UI cross-thread.
        CourseRepository.ManifestChanged += (_, _) => RunOnUi(() => Courses = CourseRepository.Courses);
        _selectedCourseId = Prefs.LastCourseId;

        var builder = new AppBuilder();
        foreach (var mode in Enum.GetValues<OperatingMode>())
        {
            builder.AddMode(new OperatingModeModel(mode));
        }
        _appState = builder.Build();

        // Restore persisted settings (language / theme / TCP target).
        if (Languages.FromTag(Prefs.LanguageTag) is { } savedLanguage)
        {
            _appState.UpdateLanguage(savedLanguage);
        }
        if (Prefs.TcpIp is { } savedIp || Prefs.TcpPort is not null)
        {
            _appState.UpdateTcpConnection(Prefs.TcpIp ?? _appState.TcpIp, Prefs.TcpPort ?? _appState.TcpPort);
        }
        // Always launch on the Teaching screen (the app's home). The last-used mode is
        // intentionally NOT restored — every launch opens on Teaching, and the Teaching build
        // resets the course filter to "All rhythms" on entry (see MainScreen.BuildForMode), so the
        // user always lands on the all-rhythms monitor.
        _appState.UpdateMode(_appState.OperatingModes.First(m => m.Id == OperatingMode.Teaching));

        _selectedOperatingMode = _appState.SelectedOperatingMode;
        _selectedLanguage = _appState.SelectedLanguage;
        _tcpIp = _appState.TcpIp;
        _tcpPort = _appState.TcpPort;
        _isDarkTheme = Prefs.DarkTheme ?? true;
        _isDrawerFixed = Prefs.DrawerFixed ?? false;
    }

    public void SetDrawerFixed(bool fixedOpen)
    {
        if (IsDrawerFixed == fixedOpen) return;
        IsDrawerFixed = fixedOpen;
        Prefs.DrawerFixed = fixedOpen;
    }

    // â”€â”€ Operating mode / language / theme â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public void UpdateOperatingMode(OperatingModeModel mode)
    {
        _appState.UpdateMode(mode);
        SelectedOperatingMode = mode;
        // The mode is not persisted: the app always launches on Teaching (see the constructor).
    }

    /// <summary>
    /// Guard the active screen registers to veto (or defer) leaving it — e.g. the Course Constructor
    /// prompts to save unsaved edits. Returns true to allow the mode switch, false to stay put. Cleared
    /// by the screen on unload. Routed through <see cref="RequestOperatingModeAsync"/>.
    /// </summary>
    public Func<Task<bool>>? LeaveGuardAsync { get; set; }

    /// <summary>
    /// Requests a mode switch, first running <see cref="LeaveGuardAsync"/> (if any) so the current
    /// screen can confirm/cancel. UI entry points (mode menu, keyboard shortcut) call this instead of
    /// <see cref="UpdateOperatingMode"/> directly so the guard can't be bypassed.
    /// </summary>
    public async Task RequestOperatingModeAsync(OperatingModeModel mode)
    {
        if (mode.Id == SelectedOperatingMode.Id) return; // already here — nothing to leave
        if (LeaveGuardAsync is { } guard && !await guard()) return;
        UpdateOperatingMode(mode);
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread — directly if already there, else enqueued.</summary>
    private void RunOnUi(Action action)
    {
        if (_dispatcher is { } d && !d.HasThreadAccess) d.TryEnqueue(() => action());
        else action();
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
        SeedOskeFormsIfNeeded();

        var courseSource = new FileCourseSource(AppPaths.CoursesDir);
        if (courseSource.IsValid())
        {
            CourseRepository.SetSource(courseSource);
            CourseRepository.LoadManifest();
        }
        else if (Prefs.CoursesTreeUri is { } courseZipPath && File.Exists(courseZipPath))
        {
            try
            {
                var zipFile = await StorageFile.GetFileFromPathAsync(courseZipPath);
                await SetCourseFolderAsync(zipFile);
            }
            catch { }
        }
        else
        {
            // Fresh install: seed the bundled sample course (mirrors the Android SampleCourseSeeder).
            await TrySeedBundledCoursesAsync();
        }

        var source = new FilePathologySource(AppPaths.PathologiesDir);
        if (source.IsValid())
        {
            Repository.SetSource(source);
            // Show the loading screen and read the manifest off the UI thread so a large
            // dataset paints a visible "Loading pathology list…" step instead of freezing.
            LoadingIsIndeterminate = true;
            LoadingProgress = 0;
            LoadingTitle = AppStrings.DataSourceLoadingManifest;
            LoadingStatus = string.Empty;
            LoadingDetail = string.Empty;
            CanCancelLoading = false;
            DataState = new DataState.Loading();
            if (await ReloadAsync())
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

        // Boot default: seed from the dataset bundled with the app, mirroring the Android
        // AssetPathologySource (so a fresh install has data without a first-run ZIP pick).
        if (await TrySeedBundledDatasetAsync()) return;
    }

    /// <summary>
    /// Extracts the dataset shipped in <c>Assets/Pathologies.zip</c> into the app data folder
    /// and loads it. Returns false (leaving the data-source screen) if no bundle is present.
    /// </summary>
    private async Task<bool> TrySeedBundledDatasetAsync()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "Pathologies.zip");
        if (!File.Exists(bundled)) return false;
        try
        {
            var token = BeginLoading();
            bool extracted;
            try
            {
                extracted = await Task.Run(
                    () => ZipExtractor.Extract(bundled, AppPaths.PathologiesDir, ExtractionProgress(), token), token);
            }
            catch (OperationCanceledException)
            {
                OnLoadingCancelled();
                return false;
            }
            CanCancelLoading = false;

            if (extracted)
            {
                var source = new FilePathologySource(AppPaths.PathologiesDir);
                if (source.IsValid())
                {
                    Repository.SetSource(source);
                    if (await ReloadAsync())
                    {
                        IsDataConfirmed = true;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // fall through to the data-source screen
        }
        // Seeding failed: drop back to the "pick a ZIP" screen rather than a stuck loading bar.
        DataState = new DataState.NotConfigured();
        return false;
    }

    /// <summary>
    /// Extracts the sample course shipped in <c>Assets/Courses.zip</c> into the app data folder
    /// and loads it. Returns false if no bundle is present. Mirrors the Android SampleCourseSeeder.
    /// </summary>
    private async Task<bool> TrySeedBundledCoursesAsync()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "Courses.zip");
        if (!File.Exists(bundled)) return false;
        try
        {
            var ok = await Task.Run(() => CourseZipExtractor.Extract(bundled, AppPaths.CoursesDir));
            if (!ok) return false;
            var source = new FileCourseSource(AppPaths.CoursesDir);
            if (!source.IsValid()) return false;
            CourseRepository.SetSource(source);
            CourseRepository.LoadManifest();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// On first run, write the two built-in conclusion forms (from <see cref="OskeSeedForms"/>) to
    /// <c>OskeDir/forms</c>. The C# seed is the single source of truth, so no bundled ZIP is needed —
    /// unlike courses/pathologies, whose content is large external data. Answer keys start empty
    /// (authored later in the OSCE constructor).
    /// </summary>
    private void SeedOskeFormsIfNeeded()
    {
        try
        {
            if (OskeRepository.Forms.Count > 0) return;
            foreach (var form in OskeSeedForms.All())
                OskeRepository.WriteForm(form);
        }
        catch
        {
            // best-effort seed; the exam screen falls back to OskeSeedForms in memory
        }
    }

    private bool _sampleTestSeeded;

    /// <summary>
    /// On first run (once the dataset is loaded), write the built-in demo test from
    /// <see cref="TestSeed"/> so the Testing screen has content, seed the question bank from the same
    /// questions so the bank isn't blank, and seed the theme catalog. Attempted at most once per
    /// session; each store is seeded only when empty/missing — a teacher's own content (or deleted
    /// demos) is left untouched.
    /// </summary>
    private void SeedSampleTestIfNeeded()
    {
        if (_sampleTestSeeded) return;
        try
        {
            var ids = Repository.Pathologies().Select(p => p.Id).Take(3).ToList();
            if (ids.Count == 0) return; // wait until pathologies are actually loaded
            _sampleTestSeeded = true;

            Themes.SeedIfMissing();

            var sample = TestSeed.Sample(ids);
            if (TestRepository.Tests.Count == 0) TestRepository.WriteTest(sample);
            if (QuestionBank.Questions.Count == 0) QuestionBank.Import(sample.Questions);
        }
        catch
        {
            // best-effort seed; the Testing screen shows an empty-state hint if it fails
        }
    }

    public async Task SetCourseFolderAsync(StorageFile zip)
    {
        Prefs.CoursesTreeUri = zip.Path;
        var extracted = await Task.Run(() => CourseZipExtractor.Extract(zip.Path, AppPaths.CoursesDir));
        if (extracted)
        {
            var source = new FileCourseSource(AppPaths.CoursesDir);
            if (source.IsValid())
            {
                this.CourseRepository.SetSource(source);
                this.CourseRepository.LoadManifest();
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
        var token = BeginLoading();

        bool extracted;
        try
        {
            extracted = await Task.Run(
                () => ZipExtractor.Extract(zip.Path, AppPaths.PathologiesDir, ExtractionProgress(), token), token);
        }
        catch (OperationCanceledException)
        {
            OnLoadingCancelled();
            return;
        }
        CanCancelLoading = false;

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
        await ReloadAsync();
    }

    // â”€â”€ Loading status (data-source loading bar) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private CancellationTokenSource? _loadingCts;

    /// <summary>Enters the <see cref="DataState.Loading"/> state with an indeterminate
    /// "preparing" status, arms a fresh cancellation token, and shows the Cancel button.
    /// Returns the token to hand to <see cref="ZipExtractor"/>.</summary>
    private CancellationToken BeginLoading()
    {
        _loadingCts?.Dispose();
        _loadingCts = new CancellationTokenSource();

        LoadingIsIndeterminate = true;
        LoadingProgress = 0;
        LoadingTitle = AppStrings.DataSourcePreparing;
        LoadingStatus = string.Empty;
        LoadingDetail = string.Empty;
        CanCancelLoading = true;
        DataState = new DataState.Loading();
        return _loadingCts.Token;
    }

    /// <summary>Requests abort of the in-progress extraction (no-op if nothing is loading).</summary>
    public void CancelLoading() => _loadingCts?.Cancel();

    /// <summary>Restores the "pick a ZIP" screen after the user aborts an extraction.</summary>
    private void OnLoadingCancelled()
    {
        CanCancelLoading = false;
        LoadingStatus = string.Empty;
        LoadingDetail = string.Empty;
        DataState = new DataState.NotConfigured();
    }

    /// <summary>An <see cref="IProgress{T}"/> that marshals <see cref="ZipExtractor"/>
    /// progress (raised on a background thread) onto the UI thread and updates the
    /// loading bar. Created fresh per extraction.</summary>
    private IProgress<ZipProgress> ExtractionProgress() =>
        new ActionProgress<ZipProgress>(OnExtractionProgress);

    private void OnExtractionProgress(ZipProgress p)
    {
        void Apply()
        {
            LoadingIsIndeterminate = false;
            var ratio = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
            LoadingProgress = ratio;
            LoadingTitle = AppStrings.DataSourceExtractingTitle;
            LoadingStatus = AppStrings.DataSourceRecordsFormat(p.Done, p.Total, (int)ratio);
            LoadingDetail = p.CurrentEntry ?? string.Empty;
        }
        if (_dispatcher is null || _dispatcher.HasThreadAccess) Apply();
        else _dispatcher.TryEnqueue(() => Apply());
    }

    /// <summary>Minimal <see cref="IProgress{T}"/> that forwards to a delegate without
    /// capturing a synchronization context (marshaling is handled by the delegate).</summary>
    private sealed class ActionProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;
        public ActionProgress(Action<T> action) => _action = action;
        public void Report(T value) => _action(value);
    }

    public void ConfirmData() => IsDataConfirmed = true;

    /// <summary>Reads the manifest (off the UI thread so a large dataset paints the
    /// loading screen instead of freezing) and moves to <see cref="DataState.Ready"/> or
    /// an error. The manifest read itself is not cancellable, so the Cancel button is
    /// hidden for this phase.</summary>
    private async Task<bool> ReloadAsync()
    {
        LoadingIsIndeterminate = true;
        LoadingProgress = 100;
        LoadingTitle = AppStrings.DataSourceLoadingManifest;
        LoadingStatus = string.Empty;
        LoadingDetail = string.Empty;
        CanCancelLoading = false;

        if (!await Task.Run(() => Repository.LoadManifest()))
        {
            DataState = new DataState.Error(DataState.ErrorReason.BadManifest);
            return false;
        }

        var count = await Task.Run(() => Repository.Pathologies().Count);
        if (count == 0)
        {
            DataState = new DataState.Error(DataState.ErrorReason.Empty);
            return false;
        }

        LoadingStatus = AppStrings.DataSourceLoadedFormat(count);
        DataState = new DataState.Ready(count);
        return true;
    }

    /// <summary>Re-packs the current dataset (with edits) to a user-chosen path.</summary>
    public Task ExportZipAsync(string destPath) =>
        Task.Run(() => ZipCompressor.Zip(AppPaths.PathologiesDir, destPath));

    /// <summary>Re-packs the current course bundle to a user-chosen path. Returns true on success.</summary>
    public Task<bool> ExportCoursesZipAsync(string destPath) =>
        Task.Run(() => ZipCompressor.Zip(AppPaths.CoursesDir, destPath));

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
        await SendSingleArchiveAsync(socket, AppPaths.PathologiesDir, "Pathologies.zip", ct);
    }

    private async Task SendSingleArchiveAsync(Socket socket, string sourceDir, string filename, CancellationToken ct)
    {
        var zipPath = await Task.Run(() => ZipCompressor.ZipToTemp(sourceDir, "upload_" + filename), ct);
        if (zipPath is null) return;

        await _sendLock.WaitAsync(ct);
        try
        {
            var size = new FileInfo(zipPath).Length;
            var msg = new TcpMessage.UploadMessage
            {
                Id = Guid.NewGuid().ToString(),
                Filename = filename,
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
