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

    /// <summary>A test queued to run on the next entry into Testing mode — set by the post-lecture Quick
    /// Test launcher (which may pass a freshly generated, unsaved test), consumed and cleared by the
    /// Testing screen on init. One-shot; null otherwise.</summary>
    public Test? PendingTest { get; set; }

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
        // Inert until TryLoadSaved swaps in a content pack — the only accepted dataset format.
        Repository = new PathologyRepository(new EmptyPathologySource());
        CourseRepository = new CourseRepository(new EmptyCourseSource());
        CourseViewerViewModel = new CourseViewerViewModel(CourseRepository);
        CourseConstructorViewModel = new CourseConstructorViewModel(CourseRepository);
        // Serve course assets (coursehost) to every lecture WebView from the active source — the
        // encrypted in-memory pack or the file-backed dataset — so protected assets stay off disk.
        Controls.LectureWebView.AssetResolver = CourseRepository.ReadCourseAsset;

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
        // Refresh the rhythm-group catalog whenever the manifest (re)loads. Reload() re-reads from
        // the in-memory content pack (or its writable overlay) that is currently active — see
        // TrySeedEncryptedDatasetAsync, which installs the catalog provider as it loads the pack.
        Repository.ManifestChanged += (_, _) => PathologyGroups.Reload();

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
            // The limited (student) edition omits the authoring/constructor modes entirely. This is
            // the single choke point: OperatingModes drives both the mode-picker flyout and the
            // number-key shortcuts, so filtering here removes every entry point into a constructor.
            // AppEdition.IsLimited is a compile-time const, so this branch folds away in the full build.
#pragma warning disable CS0162 // Unreachable code is intentional: edition-gated by a const flag.
            if (AppEdition.IsLimited && mode.IsConstructor()) continue;
#pragma warning restore CS0162
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

    /// <summary>
    /// After a course pack is (re)loaded, re-opens the course/lecture that was showing so the editor,
    /// preview and teaching view display the <b>new</b> content — even when the reloaded pack reuses the
    /// same ids (re-exporting over the same file). <see cref="Course"/> and <see cref="Lecture"/> are
    /// records, so re-selecting an equal-valued item would be suppressed by the <c>ObservableProperty</c>
    /// equality check and the view would keep the stale content; clearing to null first forces the
    /// re-assignment to notify. Same ids preserve the user's place; a missing id falls back to the new
    /// pack's first course/lecture. Runs on the UI thread (the caller's continuation).
    /// </summary>
    private void ReopenAfterCourseReload(
        string? ctorCourseId, string? ctorLectureId, string? ctorLang,
        string? viewerCourseId, string? viewerLectureId)
    {
        var courses = CourseRepository.Courses;
        Data.ReloadDebug.Log($"ReopenAfterCourseReload ENTER ctorCourse={ctorCourseId} ctorLecture={ctorLectureId} courses={courses.Count}");

        // Constructor — only when it was in use (a course was open); its panel picks defaults on entry otherwise.
        if (ctorCourseId is not null)
        {
            var vm = CourseConstructorViewModel;
            vm.ResetSelection();
            var courseId = courses.Any(c => c.Id == ctorCourseId) ? ctorCourseId
                : courses.Count > 0 ? courses[0].Id : null;
            if (courseId is not null)
            {
                vm.SelectCourse(courseId);
                var lectureId = ctorLectureId is not null && vm.SelectedCourse?.ContentItem(ctorLectureId) is not null
                    ? ctorLectureId : vm.SelectedCourse?.FirstContentItemId();
                if (lectureId is not null) vm.SelectLecture(lectureId, ctorLang ?? SelectedLanguage.Tag());
                Data.ReloadDebug.Log($"  constructor re-selected course={courseId} lecture={lectureId} => TargetLecture.RawHtml len={vm.TargetLecture?.RawHtml.Length ?? -1} body='{Data.ReloadDebug.Snip(vm.TargetLecture?.RawHtml)}'");
            }
        }

        // Teaching viewer — re-open the same course/lecture when still present, else drop the filter.
        var viewer = CourseViewerViewModel;
        viewer.Clear();
        if (viewerCourseId is not null && courses.Any(c => c.Id == viewerCourseId))
        {
            viewer.SelectCourse(viewerCourseId);
            if (viewerLectureId is not null && viewer.SelectedCourse?.ContentItem(viewerLectureId) is not null)
                viewer.SelectLecture(viewerLectureId, SelectedLanguage.Tag());
            if (SelectedCourseId is not null && SelectedCourseId != viewerCourseId) SelectedCourseId = viewerCourseId;
            Data.ReloadDebug.Log($"  viewer re-selected course={viewerCourseId} lecture={viewerLectureId} => LectureContent.RawHtml len={viewer.LectureContent?.RawHtml.Length ?? -1} body='{Data.ReloadDebug.Snip(viewer.LectureContent?.RawHtml)}'");
        }
        else if (SelectedCourseId is not null && courses.All(c => c.Id != SelectedCourseId))
        {
            SelectedCourseId = null; // the filtered course is gone from the new pack
        }

        OnPropertyChanged(nameof(SelectedCourseId));
        OnPropertyChanged(nameof(SelectedCoursePathologies));
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
        Theming.AppTheme.Set(isDark);
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
    /// Resolves both datasets from encrypted content packs — the only accepted format. A pack the
    /// user picked in Settings wins over the one bundled in <c>Assets</c>; nothing is extracted, so
    /// there is no on-disk dataset to fall back to if neither pack loads.
    /// </summary>
    public async void TryLoadSaved()
    {
        SeedOskeFormsIfNeeded();
        DropLegacyPicks();

        // Courses: the user's picked pack wins, else the bundled one. A pick that no longer loads
        // (file moved or deleted) falls through to the bundle rather than leaving the viewer empty.
        if (Prefs.CoursesTreeUri is not { } coursePak ||
            !File.Exists(coursePak) ||
            !TrySeedEncryptedCourses(coursePak, AppPaths.CourseOverlayPakFor(coursePak)))
        {
            TrySeedEncryptedCourses(BundledCoursePak, AppPaths.CourseOverlayPak);
        }

        // Pathologies: same precedence. Each pack carries its own overlay, so one pack's edits never
        // replay onto another pack's ids.
        if (Prefs.TreeUri is { } pickedPak && File.Exists(pickedPak) &&
            await TrySeedEncryptedDatasetAsync(pickedPak, AppPaths.PathologyOverlayPakFor(pickedPak)))
        {
            return;
        }

        if (await TrySeedEncryptedDatasetAsync(BundledPathologyPak, AppPaths.PathologyOverlayPak))
        {
            return;
        }

        // No usable pack anywhere — show the picker rather than a stuck loading bar.
        DataState = new DataState.NotConfigured();
    }

    /// <summary>
    /// Forgets a saved pick that this build can no longer open — an install upgrading from the ZIP
    /// era. Only a pick whose file is <i>present but not a pack</i> is dropped: a missing file may be
    /// a pack on a disconnected drive, and that pick is worth keeping. Without this the dead pointer
    /// would survive every launch, silently re-failing before the bundled pack loads.
    /// </summary>
    private void DropLegacyPicks()
    {
        if (Prefs.TreeUri is { } d && File.Exists(d) && !IsContentPack(d)) Prefs.TreeUri = null;
        if (Prefs.CoursesTreeUri is { } c && File.Exists(c) && !IsContentPack(c)) Prefs.CoursesTreeUri = null;
    }

    /// <summary>The encrypted dataset shipped with the app; the default source when the user has not
    /// picked their own.</summary>
    private static string BundledPathologyPak =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Pathologies.pak");

    /// <summary>
    /// True if <paramref name="path"/> begins with the content-pack magic. Cheap enough to run on the
    /// UI thread (reads 4 bytes) and authoritative: the file's extension is not consulted, so a pack
    /// named <c>.zip</c> (or vice versa) still routes to the right loader.
    /// </summary>
    private static bool IsContentPack(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return fs.Read(head) == head.Length && ContentCrypto.LooksLikePack(head);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Loads the dataset from the encrypted content pack at <paramref name="pak"/>, entirely in
    /// memory (see <see cref="EncryptedPathologySource"/>). Packs are the only accepted format, so
    /// this is the single dataset read path: the bundled <c>Assets/Pathologies.pak</c> and a pack the
    /// user picked in Settings both come through here. Returns false if the pack is absent, is not a
    /// pack, or fails to open/authenticate, letting the caller fall back to the bundled pack.
    ///
    /// <para>Loading shows the same status window as the old ZIP flow, driven by phase rather than a
    /// record count: a pack does no per-record work at load (entries decode lazily), so the only real
    /// phases are opening and reading the manifest. Both run off the UI thread — a large pack's
    /// central directory still takes a couple of seconds to parse.</para>
    /// </summary>
    /// <param name="overlayPak">Where the Full edition keeps this pack's constructor edits. Distinct
    /// per pack — see <see cref="AppPaths.PathologyOverlayPakFor"/>.</param>
    private async Task<bool> TrySeedEncryptedDatasetAsync(string pak, string overlayPak)
    {
        if (!File.Exists(pak) || !IsContentPack(pak)) return false;

        // Phase 1 — decrypt + authenticate the whole pack into memory.
        BeginPackLoading(pak);
        var encrypted = await Task.Run(() =>
        {
            try
            {
                var source = EncryptedPathologySource.Open(pak);
                if (source.IsValid()) return source;
                source.Dispose();
                return null;
            }
            catch
            {
                return null; // corrupt / wrong key / truncated — caller falls back
            }
        });
        if (encrypted is null) return false;

        try
        {
            // Full edition: layer an encrypted writable overlay so the constructor can edit the
            // protected pack (copy-on-write; the pack itself stays read-only, the overlay is
            // AES-encrypted so even edited/duplicated bundle content never lands as plaintext).
            // Limited edition: no constructor, so keep the source strictly read-only.
#pragma warning disable CS0162 // Edition-gated by a compile-time const (one branch folds away).
            if (AppEdition.IsFull)
            {
                var overlay = new OverlayPathologySource(
                    encrypted, WritableEncryptedOverlay.OpenOrCreate(overlayPak));
                Repository.SetSource(overlay);
                PathologyGroups.LoadFromOverlay(overlay.ReadGroupsText, overlay.WriteGroupsText);
            }
            else
            {
                Repository.SetSource(encrypted);
                // Source the rhythm-group catalog from the pack instead of an on-disk groups.txt.
                PathologyGroups.LoadFromArchive(encrypted.ReadGroupsText);
            }
#pragma warning restore CS0162

            // Release the pack this one replaces. A decrypted pack holds its whole ZIP in memory
            // (hundreds of MB for a large dataset), so re-picking would otherwise stack them up.
            // Ordered after SetSource, and tracked even when the manifest fails below, because the
            // repository already points here — disposing on failure would leave reads hitting a
            // disposed archive.
            var previous = _activePathologyPack;
            _activePathologyPack = encrypted;
            if (!ReferenceEquals(previous, encrypted)) previous?.Dispose();

            // Phase 2 — manifest (ReloadAsync sets its own title and the final "Loaded N" status).
            if (await ReloadAsync())
            {
                IsDataConfirmed = true;
                return true;
            }
        }
        catch
        {
            // fall through — caller decides whether to try the bundled pack or show the picker
        }
        return false;
    }

    /// <summary>The decrypted pack currently backing <see cref="Repository"/>, held so it can be
    /// disposed when another pack replaces it. Null until the first pack loads.</summary>
    private EncryptedPathologySource? _activePathologyPack;

    /// <summary>The course counterpart of <see cref="_activePathologyPack"/>.</summary>
    private EncryptedCourseSource? _activeCoursePack;

    /// <summary>Puts the data-source screen into the opening phase of a pack load. Mirrors the chrome
    /// the ZIP extractor used (title + detail + bar), but indeterminate: a pack is opened lazily and
    /// decodes no records, so there is no per-record progress to report and nothing to cancel
    /// part-way.</summary>
    private void BeginPackLoading(string pak)
    {
        LoadingIsIndeterminate = true;
        LoadingProgress = 0;
        LoadingTitle = AppStrings.DataSourceDecrypting;
        LoadingStatus = string.Empty;
        LoadingDetail = Path.GetFileName(pak);
        CanCancelLoading = false;
        DataState = new DataState.Loading();
    }

    /// <summary>The encrypted course bundle shipped with the app.</summary>
    private static string BundledCoursePak =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Courses.pak");

    /// <summary>
    /// Loads courses from the encrypted content pack at <paramref name="pak"/>, entirely in memory
    /// (see <see cref="EncryptedCourseSource"/>). No lecture HTML or asset is written to disk. Packs
    /// are the only accepted course format, so this is the single course read path. Returns false if
    /// the pack is absent, is not a pack, or fails to open.
    /// </summary>
    /// <param name="overlayPak">Where the Full edition keeps this pack's course-constructor edits.</param>
    private bool TrySeedEncryptedCourses(string pak, string overlayPak)
    {
        if (!File.Exists(pak) || !IsContentPack(pak)) return false;
        try
        {
            EncryptedCourseSource encrypted;
            try
            {
                encrypted = EncryptedCourseSource.Open(pak);
            }
            catch
            {
                return false; // corrupt / wrong key / truncated — caller falls back to the bundle
            }
            if (!encrypted.IsValid())
            {
                encrypted.Dispose();
                return false;
            }
            // Full edition wraps the pack in an encrypted writable overlay (constructor edits);
            // Limited keeps it strictly read-only.
#pragma warning disable CS0162 // Edition-gated by a compile-time const (one branch folds away).
            if (AppEdition.IsFull)
            {
                CourseRepository.SetSource(new OverlayCourseSource(
                    encrypted, WritableEncryptedOverlay.OpenOrCreate(overlayPak)));
            }
            else
            {
                CourseRepository.SetSource(encrypted);
            }
#pragma warning restore CS0162

            // Release the pack this one replaces (see the note in TrySeedEncryptedDatasetAsync).
            var previous = _activeCoursePack;
            _activeCoursePack = encrypted;
            if (!ReferenceEquals(previous, encrypted)) previous?.Dispose();

            return CourseRepository.LoadManifest();
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
            var ecgIds = Repository.Pathologies().Select(p => p.Id).Take(24).ToList();
            if (ecgIds.Count == 0) return; // wait until pathologies are actually loaded
            _sampleTestSeeded = true;

            Themes.SeedIfMissing();

            var sample = TestSeed.Sample(ecgIds); // uses the first three ids for its ECG-bound questions
            if (TestRepository.Tests.Count == 0) TestRepository.WriteTest(sample);
            // Fill the bank with the curated cross-theme pool (browse / generator / quick-test content).
            if (QuestionBank.Questions.Count == 0) QuestionBank.Import(TestSeed.BankQuestions(ecgIds));
        }
        catch
        {
            // best-effort seed; the Testing screen shows an empty-state hint if it fails
        }
    }

    /// <summary>Adopts a user-picked course pack. Persisted only once it loads, so a bad pick can't
    /// leave the course viewer pointing at nothing (see <see cref="SetDataFolderAsync"/>). Returns a
    /// <see cref="CourseLoadReport"/> describing what came through, so the caller can show the user the
    /// loaded courses and a content preview rather than switching silently.</summary>
    public async Task<CourseLoadReport> SetCourseFolderAsync(StorageFile file)
    {
        var name = file.Name;
        // Capture what's open BEFORE the swap so we can re-open the same course/lecture from the new pack
        // (see ReopenAfterCourseReload — this is what makes a re-export over the same file actually refresh).
        var ctorCourseId = CourseConstructorViewModel.SelectedCourse?.Id;
        var ctorLectureId = CourseConstructorViewModel.SelectedLecture?.Id ?? CourseConstructorViewModel.TargetLecture?.Id;
        var ctorLang = CourseConstructorViewModel.TargetLecture?.Language;
        var viewerCourseId = CourseViewerViewModel.SelectedCourse?.Id;
        var viewerLectureId = CourseViewerViewModel.SelectedLecture?.Id;

        Data.ReloadDebug.Log($"SetCourseFolderAsync file='{file.Path}' captured ctorCourse={ctorCourseId} ctorLecture={ctorLectureId} viewerCourse={viewerCourseId} viewerLecture={viewerLectureId}");
        var loaded = await Task.Run(
            () => TrySeedEncryptedCourses(file.Path, AppPaths.CourseOverlayPakFor(file.Path)));
        Data.ReloadDebug.Log($"TrySeedEncryptedCourses loaded={loaded}; repo now has {CourseRepository.Courses.Count} course(s): [{string.Join(", ", CourseRepository.Courses.Select(c => c.Id))}]");
        if (loaded)
        {
            Prefs.CoursesTreeUri = file.Path;
            // The source is swapped and the manifest is loaded, so re-open the previously-showing content
            // from the new pack. On the UI thread — it raises PropertyChanged the screens listen to.
            RunOnUi(() => ReopenAfterCourseReload(ctorCourseId, ctorLectureId, ctorLang, viewerCourseId, viewerLectureId));
        }
        return await Task.Run(() => BuildCourseLoadReport(name, loaded));
    }

    /// <summary>
    /// Summarises the freshly loaded course source: each course with its lecture count and languages,
    /// plus the first readable lecture rendered to plain text. Reading a real lecture (not just the
    /// manifest) is deliberate — it is what tells a stale/empty pack apart from a good one. Runs off
    /// the UI thread (it decrypts lecture bodies).
    /// </summary>
    private CourseLoadReport BuildCourseLoadReport(string fileName, bool loaded)
    {
        if (!loaded)
            return new CourseLoadReport(false, fileName, Array.Empty<CourseLoadSummary>(), 0, null, null, null);

        var summaries = new List<CourseLoadSummary>();
        var totalLectures = 0;
        string? previewCourse = null, previewLecture = null, previewSnippet = null;

        foreach (var entry in CourseRepository.Courses)
        {
            var course = CourseRepository.ReadCourse(entry.Id);
            var count = course?.Lectures.Count ?? entry.LecturesCount;
            totalLectures += count;
            summaries.Add(new CourseLoadSummary(
                DisplayTitle(entry.NameRu, entry.TitleEn, entry.Id),
                count,
                course?.Languages ?? Array.Empty<string>()));

            if (previewSnippet is not null || course is null) continue;

            // First readable content item across the pack (a plain lecture, or a leaf Тема that
            // carries its own body), rendered to a short plain-text snippet — proof content loaded.
            foreach (var item in ContentItems(course))
            {
                var lang = course.Languages.Count > 0 ? course.Languages[0] : "en";
                var text = CourseRepository.ReadLecture(entry.Id, item.Id, lang) is { } lecture
                    ? PlainTextPreview(lecture.RawHtml, 400)
                    : null;
                if (string.IsNullOrWhiteSpace(text)) continue;
                previewCourse = DisplayTitle(entry.NameRu, entry.TitleEn, entry.Id);
                previewLecture = DisplayTitle(item.NameRu, item.TitleEn, item.Id);
                previewSnippet = text;
                break;
            }
        }

        return new CourseLoadReport(true, fileName, summaries, totalLectures, previewCourse, previewLecture, previewSnippet);
    }

    /// <summary>Clickable content items in navigation terms: plain lectures, then leaf Темы (which
    /// carry their own lecture body). Mirrors <see cref="Course.ContentItem"/>.</summary>
    private static IEnumerable<LectureEntry> ContentItems(Course course)
    {
        foreach (var l in course.Lectures) yield return l;
        foreach (var t in course.Topics)
            if (t.IsLeaf) yield return new LectureEntry(t.Id, t.TitleEn, t.NameRu, t.Id);
    }

    private string DisplayTitle(string? nameRu, string titleEn, string id) =>
        (SelectedLanguage == Language.RU && !string.IsNullOrWhiteSpace(nameRu) ? nameRu
            : !string.IsNullOrWhiteSpace(titleEn) ? titleEn
            : nameRu) ?? id;

    /// <summary>Strips tags and collapses whitespace to a single readable line, truncated to
    /// <paramref name="maxLen"/>. A preview only — not a sanitizer.</summary>
    private static string PlainTextPreview(string html, int maxLen)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length <= maxLen ? text : text[..maxLen].TrimEnd() + "…";
    }

    /// <summary>
    /// Adopts a user-picked dataset. An encrypted content pack is loaded in memory (no extraction);
    /// a plaintext ZIP is extracted into the app data folder and served from there. Either way the
    /// repository is swapped and the manifest reloaded.
    ///
    /// <para>The pick is persisted to <see cref="DataSourcePrefs.TreeUri"/> only once it is known to
    /// load. Persisting earlier would strand the app: a non-null TreeUri permanently suppresses the
    /// bundled pack at startup, so a single bad pick would leave no working dataset and no UI to
    /// undo it.</para>
    /// </summary>
    public async Task SetDataFolderAsync(StorageFile file)
    {
        IsDataConfirmed = false;

        if (await TrySeedEncryptedDatasetAsync(file.Path, AppPaths.PathologyOverlayPakFor(file.Path)))
        {
            Prefs.TreeUri = file.Path;
            return;
        }

        // Not a pack, or a pack that won't decrypt. The previously loaded dataset stays active.
        DataState = new DataState.Error(DataState.ErrorReason.Unreadable);
    }

    // â”€â”€ Loading status (data-source loading bar) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>Cancel is never offered for a pack load: decryption is one atomic operation, so there
    /// is no partial state to abort into. Kept because the data-source screen binds it.</summary>
    public void CancelLoading() { }

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

    /// <summary>
    /// Exports the current dataset (with the instructor's edits) as an encrypted <c>.pak</c>, built
    /// in memory. Packs are the only format the app reads or writes, so there is no plaintext branch:
    /// the exported pack re-imports through the same picker.
    /// </summary>
    public Task<bool> ExportZipAsync(string destPath) =>
        Task.Run(() => TryWritePack(Repository.Source, destPath));

    /// <summary>Re-packs the current course bundle as an encrypted <c>.pak</c>. Returns true on success.</summary>
    public Task<bool> ExportCoursesZipAsync(string destPath) =>
        Task.Run(() => TryWritePack(CourseRepository.Source, destPath));

    /// <summary>
    /// Streams <paramref name="source"/> to <paramref name="destPath"/> as an encrypted pack.
    /// Returns false instead of throwing: this runs from an <c>async void</c> click handler, and the
    /// loaded pack's file is held open, so exporting over the pack currently in use fails with a
    /// sharing violation that must not take the app down.
    /// </summary>
    private static bool TryWritePack(object source, string destPath)
    {
        if (source is not IContentPackExportable exportable) return false;
        try
        {
            ContentPackWriter.WriteEncryptedPack(exportable, destPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // â”€â”€ TCP link â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Socket? _tcpSocket;
    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _streamCts;

    /// <summary>Samples per <c>points</c> frame — 50 at 500 Hz is a 100 ms window, so the pump wakes
    /// ten times a second rather than per sample.</summary>
    private const int PointsChunkSamples = 50;

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
        StopPointsStream();
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

                await SendUploadZipAsync(socket, ct);

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
                StopPointsStream();
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

    /// <summary>
    /// On every connect, pushes the current dataset to the server: build a plain text ZIP, send an
    /// <c>upload</c> header line followed by <see cref="TcpMessage.UploadMessage.Size"/> raw ZIP bytes,
    /// then delete the temp file. The ZIP is built from the live source, so it carries the instructor's
    /// edits merged over the base, not just the shipped Assets copy.
    ///
    /// <para>Text ZIP rather than the app's own <c>.pak</c> because that is what the server ingests —
    /// see <see cref="PlainTextZipWriter"/> for the binary-to-text conversion. Nothing on this path is
    /// encrypted: the TCP target is user-editable in every edition, so whoever the app is pointed at
    /// receives the whole dataset in the clear. Confidentiality is left to the transport.</para>
    /// </summary>
    private async Task SendUploadZipAsync(Socket socket, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"cardio-upload-{Guid.NewGuid():N}.zip");
        try
        {
            // Build to a temp file rather than memory: a real dataset is >1 GB as text and would blow
            // past Array.MaxLength, never mind the working set.
            if (!await Task.Run(() => TryWriteTextZip(tmp), ct)) return;

            var size = new FileInfo(tmp).Length;
            await _sendLock.WaitAsync(ct);
            try
            {
                var header = TcpProtocol.Encode(new TcpMessage.UploadMessage
                {
                    Id = Guid.NewGuid().ToString(),
                    Filename = "Pathologies.zip",
                    Size = size,
                }) + "\n";
                await SendAllAsync(socket, Encoding.UTF8.GetBytes(header), ct);

                await using var fs = File.OpenRead(tmp);
                var buffer = new byte[81920];
                int read;
                while ((read = await fs.ReadAsync(buffer, ct)) > 0)
                {
                    await SendAllAsync(socket, buffer.AsMemory(0, read), ct);
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch
        {
            // Best-effort: a failed upload must not tear down an otherwise usable command channel.
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Writes the live dataset to <paramref name="destPath"/> as a text ZIP. Returns false instead of
    /// throwing, matching <see cref="TryWritePack"/>: this runs from the connection loop, where a
    /// malformed pathology must not take the link (or the app) down.
    /// </summary>
    private bool TryWriteTextZip(string destPath)
    {
        if (Repository.Source is not IContentPackExportable exportable) return false;
        try
        {
            // The packer wrote the binary with the manifest's order, so passing it back makes this the
            // exact inverse; Leads.All is the same fallback the packer and the read path use.
            var leadOrder = Repository.Manifest()?.LeadOrder is { Count: > 0 } order ? order : Leads.All;
            PlainTextZipWriter.WriteTextZip(exportable, leadOrder, destPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts the run: sends <c>start</c>, then streams the selected pathology's waveforms as
    /// <c>points</c> frames until <see cref="SendStopCommand"/> or a disconnect. Pass the waveforms the
    /// monitor is showing — they are snapshotted here, so a later rhythm change does not retarget an
    /// in-flight stream.
    /// </summary>
    public void SendStartCommand(
        string? pathology = null,
        string? name = null,
        IReadOnlyDictionary<Lead, Points>? waveforms = null,
        EcgCalibration? calibration = null)
    {
        var socket = _tcpSocket;
        if (socket is null || TcpConnectionState is not TcpState.Connected) return;

        var rate = calibration?.SampleRateHz ?? new EcgCalibration().SampleRateHz;
        var snapshot = Snapshot(waveforms);

        _ = Task.Run(async () =>
        {
            var paramsMap = new Dictionary<string, string>();
            if (pathology is not null) paramsMap["pathology"] = pathology;
            if (name is not null) paramsMap["name"] = name;
            var msg = new TcpMessage.StartCommand
            {
                Id = Guid.NewGuid().ToString(),
                SampleRate = (int)Math.Round(rate),
                Params = paramsMap,
            };
            // Only pump once the receiver has been told what is coming and at what rate.
            if (await SendLineAsync(socket, msg) && snapshot.Count > 0)
            {
                StartPointsStream(socket, pathology, snapshot, rate);
            }
        });
    }

    public void SendStopCommand()
    {
        StopPointsStream();

        var socket = _tcpSocket;
        if (socket is null || TcpConnectionState is not TcpState.Connected) return;

        _ = SendLineAsync(socket, new TcpMessage.StopCommand { Id = Guid.NewGuid().ToString() });
    }

    /// <summary>Copies the waveforms out of the view-model's dictionaries, dropping empty leads. The
    /// pump reads this for the life of the run, so it must not alias mutable view-model state.</summary>
    private static Dictionary<Lead, float[]> Snapshot(IReadOnlyDictionary<Lead, Points>? waveforms) =>
        waveforms is null
            ? new Dictionary<Lead, float[]>()
            : waveforms
                .Where(kv => kv.Value.Values.Count > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Values.ToArray());

    private void StartPointsStream(Socket socket, string? identy, Dictionary<Lead, float[]> waveforms, float sampleRateHz)
    {
        StopPointsStream();
        var cts = new CancellationTokenSource();
        _streamCts = cts;
        _ = Task.Run(() => PointsLoopAsync(socket, identy, waveforms, sampleRateHz, cts.Token));
    }

    private void StopPointsStream()
    {
        var cts = _streamCts;
        _streamCts = null;
        try { cts?.Cancel(); cts?.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Streams each lead as <c>points</c> frames, paced at the sample rate and looping the record the
    /// way the on-screen monitor does, so the peer sees a continuous trace rather than a one-shot dump.
    /// <c>offset</c> is the frame's start index within the record and wraps with the loop.
    /// </summary>
    private async Task PointsLoopAsync(
        Socket socket,
        string? identy,
        Dictionary<Lead, float[]> waveforms,
        float sampleRateHz,
        CancellationToken ct)
    {
        var rate = sampleRateHz > 0 ? sampleRateHz : new EcgCalibration().SampleRateHz;
        var period = TimeSpan.FromMilliseconds(PointsChunkSamples * 1000.0 / rate);
        var cursors = waveforms.Keys.ToDictionary(lead => lead, _ => 0);

        try
        {
            using var timer = new PeriodicTimer(period);
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_tcpSocket != socket || TcpConnectionState is not TcpState.Connected) return;

                foreach (var (lead, values) in waveforms)
                {
                    var offset = cursors[lead];
                    var count = Math.Min(PointsChunkSamples, values.Length - offset);
                    var msg = new TcpMessage.PointsMessage
                    {
                        Lead = lead,
                        Identy = identy,
                        Offset = offset,
                        Values = values.AsSpan(offset, count).ToArray(),
                    };
                    if (!await SendLineAsync(socket, msg, ct)) return;
                    cursors[lead] = (offset + count) % values.Length;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped or disconnected.
        }
        catch
        {
            // Socket died mid-frame; the connection loop handles the reconnect.
        }
    }

    /// <summary>Encodes and sends one newline-terminated frame. Returns false if the socket failed,
    /// which the pump treats as end-of-stream.</summary>
    private async Task<bool> SendLineAsync(Socket socket, TcpMessage message, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(TcpProtocol.Encode(message) + "\n");
        try
        {
            await _sendLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        try
        {
            await SendAllAsync(socket, bytes, ct);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Sends every byte of <paramref name="data"/>. A stream socket may accept a partial
    /// write, which would silently corrupt a length-prefixed upload or split a JSON frame.</summary>
    private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        while (!data.IsEmpty)
        {
            var sent = await socket.SendAsync(data, SocketFlags.None, ct);
            if (sent <= 0) throw new IOException("Socket closed while sending.");
            data = data[sent..];
        }
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
