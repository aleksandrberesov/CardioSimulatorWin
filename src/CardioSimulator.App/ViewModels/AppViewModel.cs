using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    /// <summary>Persisted examination attempt results (one JSON per attempt).</summary>
    public ExamResultStore ExamResultStore { get; }

    /// <summary>The instructor's registered-student roster (Full edition only). Populated from the
    /// Students registration screen; a single JSON file under the app root.</summary>
    public StudentStore StudentStore { get; }

    /// <summary>The editable Treatment Protocols reference content, authored by the Full-edition Admin
    /// on the Treatment Protocols screen. A single JSON file under the app root; built-in defaults until
    /// the first edit.</summary>
    public Data.TreatmentProtocolStore TreatmentProtocolStore { get; }

    /// <summary>The Group-mode LAN quiz server (QR → student phones). App-lifetime so a session
    /// survives switching screens; started/stopped from the Examination screen.</summary>
    public Network.GroupTestServer GroupTestServer { get; }

    /// <summary>A test queued to run on the next entry into Testing mode — set by the post-lecture Quick
    /// Test launcher (which may pass a freshly generated, unsaved test), consumed and cleared by the
    /// Testing screen on init. One-shot; null otherwise.</summary>
    public Test? PendingTest { get; set; }

    /// <summary>A student queued to be selected when entering Learning Scale mode — set by the Students
    /// roster screen when clicking "View Learning Scale", consumed and cleared when building LearningScaleScreen.
    /// One-shot; null otherwise.</summary>
    public Student? PendingLearningScaleStudent { get; set; }

    /// <summary>A graded exam launch queued from the Learning Scale «Сдать» (A3, customer 28-08): the block's
    /// key test + the student to record it for, and the roster student to return to the dashboard with after.
    /// Consumed and cleared by <see cref="Screens.ExaminationScreen"/>. One-shot; null otherwise.</summary>
    public PendingExamLaunch? PendingExamLaunch { get; set; }

    private readonly AppStateModel _appState;
    private readonly DispatcherQueue? _dispatcher;
    private readonly int _tcpReconnectIntervalMs;

    /// <summary>The five operating modes, in declaration order.</summary>
    public IReadOnlyList<OperatingModeModel> OperatingModes => _appState.OperatingModes;      

    [ObservableProperty]
    private OperatingModeModel _selectedOperatingMode;

    // ── Admin / User runtime role (Full edition only) ──────────────────────
    // A configurable lock layered on top of the compile-time edition: an instructor enters Admin
    // (PIN-guarded) to hide screens/blocks, then drops to User and hands the machine to students.
    // Defaults (User, no PIN, nothing hidden) reproduce today's out-of-the-box Full experience.
    // Loaded from Prefs in the constructor; see the SetAdminPin/EnterAdmin/SetModeHidden methods.

    private readonly HashSet<OperatingMode> _hiddenModes = new();
    private readonly HashSet<AppBlock> _hiddenBlocks = new();
    private AppRole _role = AppRole.User;

    /// <summary>The current runtime role. Setting it persists and refreshes
    /// <see cref="VisibleOperatingModes"/>; use <see cref="EnterAdmin"/>/<see cref="ExitAdmin"/>
    /// rather than assigning directly so PIN verification and the safe-landing redirect run.</summary>
    public AppRole Role
    {
        get => _role;
        private set
        {
            if (!SetProperty(ref _role, value)) return;
            Prefs.AppRoleName = value.ToString();
            OnPropertyChanged(nameof(VisibleOperatingModes));
        }
    }

    /// <summary>Whether an admin PIN has been set (first Admin entry sets one; see <see cref="SetAdminPin"/>).</summary>
    public bool HasAdminPin =>
        !string.IsNullOrEmpty(Prefs.AdminPinHash) && !string.IsNullOrEmpty(Prefs.AdminPinSalt);

    /// <summary>
    /// The operating modes visible for the current <see cref="Role"/>: the full (edition-filtered)
    /// list in Admin, the non-hidden subset in User. This is the single choke point the mode-picker
    /// flyout and the Ctrl+N shortcuts read, so hiding a mode removes both entry points into it.
    /// Teaching is always retained (never hideable).
    /// </summary>
    public IReadOnlyList<OperatingModeModel> VisibleOperatingModes =>
        ModeVisibility.Visible(OperatingModes, _hiddenModes, Role);

    /// <summary>Whether <paramref name="mode"/> is currently hidden from User mode.</summary>
    public bool IsModeHidden(OperatingMode mode) => _hiddenModes.Contains(mode);

    /// <summary>Whether an in-screen block should render: always in Admin (so the admin can see and
    /// configure it), otherwise only when the admin hasn't hidden it.</summary>
    public bool IsBlockVisible(AppBlock block) => Role == AppRole.Admin || !_hiddenBlocks.Contains(block);

    /// <summary>Whether <paramref name="block"/> is marked hidden — the persisted state the admin
    /// checklist reflects (unlike <see cref="IsBlockVisible"/>, this ignores the current role).</summary>
    public bool IsBlockHidden(AppBlock block) => _hiddenBlocks.Contains(block);

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

    /// <summary>Whether the monitor's R-peak pulse beep is enabled. Persisted; default on. Shared
    /// state so the monitor toggle and the Settings screen stay in sync.</summary>
    [ObservableProperty]
    private bool _monitorSoundEnabled = true;

    /// <summary>Monitor pulse-beep loudness (0..1). Persisted; default 0.6. Adjusted from Settings and
    /// applied live to the monitor.</summary>
    [ObservableProperty]
    private double _monitorSoundVolume = 0.6;

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

    /// <summary>
    /// The effective rhythm filter for the Teaching monitor drawer: the course-wide list plus the
    /// currently-open theme's own rhythms (union). Null in "All rhythms" mode (no filter — every rhythm
    /// shows). The theme is the Тема owning the viewer's current lecture; with no theme open (a loose
    /// lecture, or a flat course with no Темы) only the course-wide list applies.
    /// </summary>
    public IReadOnlyList<string>? EffectiveTeachingPathologies => GetEffectiveTeachingPathologies();

    private IReadOnlyList<string>? GetEffectiveTeachingPathologies()
    {
        if (SelectedCourseId is null || SelectedCourseId == AllRhythmsId) return null;

        // Prefer the fully-parsed course the viewer holds — it carries Topics + per-topic rhythms;
        // fall back to the manifest's course-wide list before the viewer is populated.
        var course = CourseViewerViewModel.SelectedCourse;
        var courseWide = course?.Pathologies ?? SelectedCoursePathologies ?? Array.Empty<string>();

        var lecture = CourseViewerViewModel.SelectedLecture;
        var theme = lecture is not null && course is not null
            ? course.Topics.FirstOrDefault(t => t.Id == lecture.Topic)
            : null;

        var explicitThemeRhythms = theme?.PathologyList ?? Array.Empty<string>();

        // Extract taxonomy subsections from active lecture/theme metadata or title/id numeration
        var subKeys = ExtractSubsections(lecture, theme);

        var taxonomyRhythms = new List<string>();
        if (subKeys.Count > 0 && Repository is not null)
        {
            var allPathologies = Repository.Pathologies();
            var taxonomyAcronyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in subKeys)
            {
                foreach (var entry in Taxonomy.Shared.ForSubsectionOrTopic(k))
                {
                    taxonomyAcronyms.Add(entry.Acronym);
                }
            }
            if (taxonomyAcronyms.Count > 0)
            {
                taxonomyRhythms.AddRange(Taxonomy.ResolvePathologyIdsForAcronyms(taxonomyAcronyms, allPathologies));
            }
        }

        var combined = courseWide.Concat(explicitThemeRhythms).Concat(taxonomyRhythms).Distinct().ToList();
        return combined.Count > 0 ? combined : courseWide.ToList();
    }

    private static HashSet<string> ExtractSubsections(LectureEntry? lecture, TopicEntry? theme)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfValid(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var trimmed = raw.Trim();
            set.Add(trimmed);
            var subKey = Taxonomy.SubtopicKeyOf(trimmed);
            if (!string.IsNullOrEmpty(subKey)) set.Add(subKey);
        }

        void ExtractNumeration(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // Match dotted section/subsection numbers like 4.6.2, 4.6, 3.1
            var dottedMatch = System.Text.RegularExpressions.Regex.Match(text, @"\b(\d+\.\d+(?:\.\d+)?)\b");
            if (dottedMatch.Success)
            {
                AddIfValid(dottedMatch.Groups[1].Value);
                return;
            }

            // Match explicit section titles like "Раздел 4", "Section 4", "Тема 4"
            var sectionMatch = System.Text.RegularExpressions.Regex.Match(
                text,
                @"\b(?:раздел|section|тема)\s+(\d+)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (sectionMatch.Success)
            {
                AddIfValid(sectionMatch.Groups[1].Value);
            }
        }

        AddIfValid(lecture?.Subsection);
        AddIfValid(theme?.Subsection);

        ExtractNumeration(lecture?.TitleEn);
        ExtractNumeration(lecture?.NameRu);
        ExtractNumeration(lecture?.Id);

        ExtractNumeration(theme?.TitleEn);
        ExtractNumeration(theme?.NameRu);
        ExtractNumeration(theme?.Id);

        return set;
    }

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
        // The Teaching monitor's rhythm filter is theme-aware (course-wide list + the open theme's own
        // rhythms), so recompute it whenever the viewer navigates to a different lecture/theme or course.
        CourseViewerViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CourseViewerViewModel.SelectedLecture)
                or nameof(CourseViewerViewModel.SelectedCourse))
                OnPropertyChanged(nameof(EffectiveTeachingPathologies));
        };
        // Serve course assets (coursehost) to every lecture WebView from the active source — the
        // encrypted in-memory pack or the file-backed dataset — so protected assets stay off disk.
        Controls.LectureWebView.AssetResolver = CourseRepository.ReadCourseAsset;

        OskeRepository = new OskeRepository(new FileOskeSource(AppPaths.OskeDir));
        OskeResultStore = new OskeResultStore(AppPaths.OskeResultsDir);

        TestRepository = new TestRepository(new FileTestSource(AppPaths.TestsDir));
        QuestionBank = new QuestionBankRepository(new FileQuestionBankSource(AppPaths.QuestionBankDir));
        ExamResultStore = new ExamResultStore(AppPaths.ExamResultsDir);
        StudentStore = new StudentStore(AppPaths.StudentsFile);
        TreatmentProtocolStore = new Data.TreatmentProtocolStore(AppPaths.TreatmentProtocolsFile);
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
            // The limited (student) edition omits the Full-only modes entirely — every authoring/
            // constructor mode plus the student-registration roster. This is the single choke point:
            // OperatingModes drives both the mode-picker flyout and the number-key shortcuts, so
            // filtering here removes every entry point into those modes. AppEdition.IsLimited is a
            // compile-time const, so this branch folds away in the full build.
#pragma warning disable CS0162 // Unreachable code is intentional: edition-gated by a const flag.
            if (AppEdition.IsLimited && mode.IsFullEditionOnly()) continue;
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
        _monitorSoundEnabled = Prefs.MonitorSoundEnabled ?? true;
        _monitorSoundVolume = Math.Clamp((Prefs.MonitorSoundVolume ?? 60) / 100.0, 0.0, 1.0);

        // Restore the runtime role + hidden-item sets (Full edition; absent/malformed ⇒ defaults, i.e.
        // User role with nothing hidden = today's behavior). Assign the field directly so loading does
        // not re-persist through the Role setter.
        if (Enum.TryParse<AppRole>(Prefs.AppRoleName, out var savedRole)) _role = savedRole;
        LoadHiddenSet(Prefs.HiddenModes, _hiddenModes);
        LoadHiddenSet(Prefs.HiddenBlocks, _hiddenBlocks);
    }

    private static void LoadHiddenSet<T>(string? json, HashSet<T> into) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            if (JsonSerializer.Deserialize<string[]>(json) is not { } names) return;
            foreach (var name in names)
                if (Enum.TryParse<T>(name, out var value)) into.Add(value);
        }
        catch (JsonException) { /* ignore a malformed prefs value — fall back to nothing hidden */ }
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

    // ── Admin / User role transitions + configuration ──────────────────────

    /// <summary>Sets (or replaces) the admin PIN: a fresh per-install random salt plus the salted
    /// SHA-256 of <paramref name="pin"/>, persisted to Prefs. Not a hard security boundary — the
    /// prefs file can be deleted to reset — just a guard against a student flipping back to Admin.</summary>
    public void SetAdminPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        Prefs.AdminPinSalt = Convert.ToBase64String(salt);
        Prefs.AdminPinHash = HashPin(pin, salt);
        OnPropertyChanged(nameof(HasAdminPin));
    }

    /// <summary>Whether <paramref name="pin"/> matches the stored admin PIN (false if none is set).</summary>
    public bool VerifyAdminPin(string pin)
    {
        if (Prefs.AdminPinHash is not { Length: > 0 } hash || Prefs.AdminPinSalt is not { Length: > 0 } salt)
            return false;
        try
        {
            return HashPin(pin, Convert.FromBase64String(salt)) == hash;
        }
        catch (FormatException)
        {
            return false; // corrupted salt — treat as no valid PIN
        }
    }

    private static string HashPin(string pin, byte[] salt)
    {
        var bytes = new byte[salt.Length + Encoding.UTF8.GetByteCount(pin)];
        salt.CopyTo(bytes, 0);
        Encoding.UTF8.GetBytes(pin, 0, pin.Length, bytes, salt.Length);
        return Convert.ToBase64String(SHA256.HashData(bytes));
    }

    /// <summary>Enters Admin mode if <paramref name="pin"/> verifies; returns false (no change) otherwise.</summary>
    public bool EnterAdmin(string pin)
    {
        if (!VerifyAdminPin(pin)) return false;
        Role = AppRole.Admin;
        return true;
    }

    /// <summary>Drops back to User mode (no PIN required). If the currently selected screen is now
    /// hidden from users, redirects to Teaching (the guaranteed-visible home screen).</summary>
    public void ExitAdmin()
    {
        Role = AppRole.User;
        if (SelectedOperatingMode.Id.IsHideable() && _hiddenModes.Contains(SelectedOperatingMode.Id))
        {
            UpdateOperatingMode(OperatingModes.First(m => m.Id == OperatingMode.Teaching));
        }
    }

    /// <summary>Hides/shows a whole screen from User mode (no-op for the non-hideable Teaching screen).
    /// Persists and refreshes <see cref="VisibleOperatingModes"/>.</summary>
    public void SetModeHidden(OperatingMode mode, bool hidden)
    {
        if (!mode.IsHideable()) return;
        var changed = hidden ? _hiddenModes.Add(mode) : _hiddenModes.Remove(mode);
        if (!changed) return;
        Prefs.HiddenModes = SerializeNames(_hiddenModes);
        OnPropertyChanged(nameof(VisibleOperatingModes));
    }

    /// <summary>Hides/shows an in-screen block from User mode. Persists; screens re-read
    /// <see cref="IsBlockVisible"/> on their next build.</summary>
    public void SetBlockHidden(AppBlock block, bool hidden)
    {
        var changed = hidden ? _hiddenBlocks.Add(block) : _hiddenBlocks.Remove(block);
        if (!changed) return;
        Prefs.HiddenBlocks = SerializeNames(_hiddenBlocks);
    }

    private static string SerializeNames<T>(IEnumerable<T> values) where T : struct, Enum =>
        JsonSerializer.Serialize(values.Select(v => v.ToString()).ToArray());

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
        OnPropertyChanged(nameof(EffectiveTeachingPathologies));
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

    /// <summary>Enables/disables the monitor pulse beep and persists it (raises PropertyChanged so the
    /// monitor and Settings stay in sync).</summary>
    public void UpdateMonitorSoundEnabled(bool enabled)
    {
        MonitorSoundEnabled = enabled;
        Prefs.MonitorSoundEnabled = enabled;
    }

    /// <summary>Sets the monitor pulse-beep volume (0..1) and persists it as a 0–100 percentage.</summary>
    public void UpdateMonitorSoundVolume(double volume)
    {
        volume = Math.Clamp(volume, 0.0, 1.0);
        MonitorSoundVolume = volume;
        Prefs.MonitorSoundVolume = (int)Math.Round(volume * 100);
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

    /// <summary>
    /// Resets the pathology dataset to the bundled default pack (<see cref="BundledPathologyPak"/>),
    /// clearing any user-selected pack path stored in <see cref="DataSourcePrefs.TreeUri"/>.
    /// </summary>
    public async Task ResetDataFolderToDefaultAsync()
    {
        Prefs.TreeUri = null;
        IsDataConfirmed = false;
        SelectCourse(null);
        if (File.Exists(AppPaths.PathologyOverlayPak))
        {
            try { File.Delete(AppPaths.PathologyOverlayPak); } catch { }
        }
        await TrySeedEncryptedDatasetAsync(BundledPathologyPak, AppPaths.PathologyOverlayPak);
    }

    /// <summary>
    /// Resets the course dataset to the bundled default pack (<see cref="BundledCoursePak"/>),
    /// clearing any user-selected course pack path stored in <see cref="DataSourcePrefs.CoursesTreeUri"/>.
    /// </summary>
    public async Task ResetCourseFolderToDefaultAsync()
    {
        Prefs.CoursesTreeUri = null;
        if (File.Exists(AppPaths.CourseOverlayPak))
        {
            try { File.Delete(AppPaths.CourseOverlayPak); } catch { }
        }
        var loaded = await Task.Run(() => TrySeedEncryptedCourses(BundledCoursePak, AppPaths.CourseOverlayPak));
        if (loaded)
        {
            RunOnUi(() => ReopenAfterCourseReload(null, null, null, null, null));
        }
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
    public Task<ExportOutcome> ExportZipAsync(
        string destPath,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => TryWritePack(Repository.Source, destPath, progress, cancellationToken), cancellationToken);

    /// <summary>Re-packs the current course bundle as an encrypted <c>.pak</c>. Cancellable and reports
    /// per-entry <see cref="ExportProgress"/> so a long export can be watched and interrupted.</summary>
    public Task<ExportOutcome> ExportCoursesZipAsync(
        string destPath,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => TryWritePack(CourseRepository.Source, destPath, progress, cancellationToken), cancellationToken);

    /// <summary>
    /// Streams <paramref name="source"/> to <paramref name="destPath"/> as an encrypted pack. Returns an
    /// <see cref="ExportOutcome"/> instead of throwing: this runs from an <c>async void</c> click
    /// handler, and the loaded pack's file is held open, so exporting over the pack currently in use
    /// fails with a sharing violation that must not take the app down. A user cancellation surfaces as
    /// <see cref="ExportOutcome.Canceled"/> rather than <see cref="ExportOutcome.Failed"/>.
    /// </summary>
    private static ExportOutcome TryWritePack(
        object source,
        string destPath,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (source is not IContentPackExportable exportable) return ExportOutcome.Failed;
        try
        {
            ContentPackWriter.WriteEncryptedPack(exportable, destPath, progress, cancellationToken);
            return ExportOutcome.Success;
        }
        catch (OperationCanceledException)
        {
            return ExportOutcome.Canceled;
        }
        catch
        {
            return ExportOutcome.Failed;
        }
    }

    // â”€â”€ TCP link â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Socket? _tcpSocket;
    private CancellationTokenSource? _connectionCts;
    private CancellationTokenSource? _streamCts;

    /// <summary>Whether the user's last start/stop press was start (<see cref="SendStartCommand"/> sets it,
    /// <see cref="SendStopCommand"/> clears it). Recorded even while disconnected, since it is the user's intent:
    /// together with the monitor still running, it tells <see cref="SendRhythmData"/> that a new selection must
    /// switch the server's playback over (query → stop → start) rather than only deliver the data.</summary>
    private volatile bool _playbackRequested;

    /// <summary>Always-on, bounded log of the TCP server conversation ("Server message log" window).</summary>
    /// <remarks>Recording is synchronous, lock-cheap and never throws, so the send/receive paths below call it
    /// inline without awaits. Outgoing frames are recorded inside <see cref="_sendLock"/> right before their bytes
    /// hit the socket, so log order equals wire order (a <c>query</c> is always logged before its reply).</remarks>
    public TcpTrafficLog TcpTraffic { get; } = new();

    // Written on the UI thread (ConnectTcp / DisconnectTcp); read by the pool threads of the send/receive paths.
    private volatile bool _isTcpLinkOn;
    private string? _activeTcpEndpoint;

    /// <summary>Consecutive connect attempts of the running loop that did not connect. Written only by
    /// <see cref="ConnectionLoopAsync"/> (reset by <see cref="ConnectTcp"/>); read by <see cref="DisconnectTcp"/>
    /// for its log line.</summary>
    private volatile int _tcpFailureStreak;

    /// <summary>True while the user has the link switched on (Connect pressed, Disconnect not yet), including
    /// while the loop waits between reconnect attempts and <see cref="TcpConnectionState"/> reads Disconnected, so a
    /// Connect/Disconnect button must key off this rather than the socket state. Also decides whether a skipped or
    /// failed send is worth logging: a send that fails because the user just disconnected, or while the link is
    /// simply off, is not an error. Changes (and raises PropertyChanged) on the UI thread only.</summary>
    public bool IsTcpLinkOn
    {
        get => _isTcpLinkOn;
        private set
        {
            if (_isTcpLinkOn == value) return;
            _isTcpLinkOn = value;
            OnPropertyChanged(nameof(IsTcpLinkOn));
        }
    }

    /// <summary>The <c>ip:port</c> the running connection loop was started with (captured in
    /// <see cref="ConnectTcp"/>), or null while the link is off. Unlike <see cref="TcpIp"/>/<see cref="TcpPort"/>
    /// it ignores later edits in Settings, which only take effect on the next connect.</summary>
    public string? ActiveTcpEndpoint
    {
        get => _activeTcpEndpoint;
        private set => SetProperty(ref _activeTcpEndpoint, value);
    }

    /// <summary>"retrying in N s" suffix for connection-loop log events (invariant culture).</summary>
    private string TcpRetryHint =>
        "retrying in " +
        (_tcpReconnectIntervalMs / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
        " s";

    /// <summary>During a run of identical connect failures, every this-many-th one still logs a "still retrying" row.</summary>
    private const int ConnectFailedReminderEvery = 12;

    /// <summary>" after N failed attempts" once a failure streak is long enough to be worth stating (N ≥ 2), else empty.</summary>
    private static string FailedAttemptsSuffix(int failures) =>
        failures >= 2 ? $" after {failures} failed attempts" : string.Empty;

    /// <summary>One-line reason for a failed connect / lost connection: the OS message plus the stable
    /// <see cref="SocketError"/> token (Windows localizes the message, the token stays English).</summary>
    private static string DescribeTcpFailure(Exception ex) => ex is SocketException se
        ? $"{se.Message.Trim()} ({se.SocketErrorCode})"
        : ex.Message.Trim();

    // Cache handshake: each `query` we send registers a waiter here; the server's OK / no_data reply
    // (read by ReceiveLoopAsync) completes it. true = the server has no copy → send the rhythm;
    // false = the server already cached this rhythm → skip it. A waiter resolves by echoed id when the
    // server provides one, otherwise in FIFO order (replies to `query`s are 1:1 and in order on the socket).
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> _cachePending = new();
    private readonly Queue<string> _cacheOrder = new();

    /// <summary>How long to wait for the server's cache reply before failing open (sending the points
    /// anyway) — a silent or slow server must never leave the peer without the rhythm.</summary>
    private const int CacheReplyTimeoutMs = 4000;

    /// <summary>The host's snapshot of the selected rhythm, used for the on-connect push.</summary>
    public sealed record RhythmSelection(string Pathology, string? Name, EcgCalibration? Calibration);

    /// <summary>Supplies the currently-selected rhythm so a fresh connection can push it right after the
    /// manifest — the app always points at some rhythm, and the server should show it without waiting for a
    /// re-selection. Set by the host (<c>MainScreen</c>), which owns the rhythm view-model; returns null when
    /// nothing is selected yet.</summary>
    private Func<RhythmSelection?>? _currentRhythmProvider;

    public void SetCurrentRhythmProvider(Func<RhythmSelection?>? provider) => _currentRhythmProvider = provider;

    public void ToggleTcpConnection()
    {
        // Keyed off the user's intent, not the socket state: between reconnect attempts the state reads
        // Disconnected while the loop is still live, and that click must stop the loop, not restart it.
        if (IsTcpLinkOn)
        {
            DisconnectTcp();
        }
        else
        {
            ConnectTcp();
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
        _tcpFailureStreak = 0;
        ActiveTcpEndpoint = $"{ip}:{port}";
        IsTcpLinkOn = true;
        _ = Task.Run(() => ConnectionLoopAsync(ip, port, cts.Token));
    }

    private void DisconnectTcp()
    {
        // Logged before the teardown so it precedes anything the close itself might surface.
        if (IsTcpLinkOn)
        {
            TcpTraffic.RecordEvent(TcpTrafficEvent.UserDisconnect,
                "disconnected by user" + FailedAttemptsSuffix(_tcpFailureStreak));
        }

        // Off before the socket closes, so the in-flight write the close aborts isn't logged as a send failure.
        IsTcpLinkOn = false;
        ActiveTcpEndpoint = null;
        StopRhythmSend();
        _connectionCts?.Cancel();
        try { _tcpSocket?.Close(); } catch { /* ignore */ }
        _tcpSocket = null;
        SetConnectionState(new TcpState.Disconnected());
    }

    private async Task ConnectionLoopAsync(string ip, int port, CancellationToken ct)
    {
        var endpoint = $"{ip}:{port}";

        // Retry-noise control for the log only (reconnect timing is untouched): against a dead server every attempt
        // would add a Connecting + ConnectFailed pair and soon push the last real exchange out of the log. A failure
        // streak is the run of consecutive attempts that did not connect. Its first failure, and any failure whose
        // reason changes, are logged in full; identical repeats are only counted (plus a "still retrying" row every
        // ConnectFailedReminderEvery-th); the attempt that ends the streak says how long it was. A live connection
        // that drops starts a fresh streak.
        var failureStreak = 0;
        string? lastLoggedReason = null;

        while (!ct.IsCancellationRequested)
        {
            SetConnectionState(new TcpState.Connecting(), ct);
            // Mid-streak, whether this attempt is worth a Connecting row (it connects, or fails for a new reason) is
            // only known once it ends, so that row is written then and carries the end time, not the start time.
            var connectingLogged = failureStreak == 0;
            if (connectingLogged) TcpTraffic.RecordEvent(TcpTrafficEvent.Connecting, endpoint);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            // Log-only bookkeeping for how this attempt ended; the retry flow below doesn't read it.
            var connected = false;
            var connectTimedOut = false;
            Exception? failure = null;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(_tcpReconnectIntervalMs);
                try
                {
                    await socket.ConnectAsync(ip, port, connectCts.Token);
                }
                catch when (connectCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Our own connect timeout fired (not the user's disconnect). Tag it for the log and
                    // rethrow unchanged into the usual retry path.
                    connectTimedOut = true;
                    throw;
                }

                connected = true;
                string local;
                try { local = socket.LocalEndPoint?.ToString() ?? "?"; } catch { local = "?"; }
                if (!connectingLogged) TcpTraffic.RecordEvent(TcpTrafficEvent.Connecting, endpoint);
                TcpTraffic.RecordEvent(TcpTrafficEvent.Connected,
                    $"{endpoint} (local {local}){FailedAttemptsSuffix(failureStreak)}");
                failureStreak = 0;
                lastLoggedReason = null;
                _tcpFailureStreak = 0;

                _tcpSocket = socket;
                SetConnectionState(new TcpState.Connected(), ct);

                await SendManifestAsync(socket, ct);

                // The app always points at some rhythm — push it now so the server shows it without waiting
                // for the user to re-select. Ordered after the manifest (same thread, awaited above) so the
                // catalog lands first; the query/rhythm send then runs concurrently with the receive loop
                // that reads its cache verdict. Started with the socket this loop just connected rather than via
                // SendRhythmData, whose gate reads TcpConnectionState: from this pool thread the Connected state
                // set above is only queued to the UI thread and may not have landed yet, silently skipping the push.
                if (_tcpSocket == socket && _currentRhythmProvider?.Invoke() is { } selection)
                {
                    BeginRhythmSend(socket, selection.Pathology, selection.Name, selection.Calibration, restartPlayback: false);
                }

                // Read the server's replies (OK / no_data cache verdicts) until EOF/disconnect.
                await ReceiveLoopAsync(socket, ct);
            }
            catch (Exception ex)
            {
                // Connection lost or failed to connect — fall through to retry.
                failure = ex;
            }
            finally
            {
                StopRhythmSend();
                ClearCacheWaiters();
                try { socket.Close(); } catch { /* ignore */ }
                if (_tcpSocket == socket) _tcpSocket = null;
            }

            if (!ct.IsCancellationRequested)
            {
                // Only an unplanned ending is logged: the user's own disconnect cancels ct and skips this.
                if (!connected)
                {
                    var reason = connectTimedOut
                        ? $"timed out after {_tcpReconnectIntervalMs} ms"
                        : failure is not null ? DescribeTcpFailure(failure) : "unknown error";
                    failureStreak++;
                    _tcpFailureStreak = failureStreak;
                    if (connectingLogged || reason != lastLoggedReason)
                    {
                        // The streak's first failure, or a new reason: logged in full, after the deferred Connecting.
                        if (!connectingLogged) TcpTraffic.RecordEvent(TcpTrafficEvent.Connecting, endpoint);
                        var attempt = failureStreak > 1 ? $" (attempt {failureStreak})" : string.Empty;
                        TcpTraffic.RecordEvent(TcpTrafficEvent.ConnectFailed,
                            $"{endpoint} — {reason}; {TcpRetryHint}{attempt}", isError: true);
                        lastLoggedReason = reason;
                    }
                    else if (failureStreak % ConnectFailedReminderEvery == 0)
                    {
                        // An identical repeat is only counted, bar this periodic sign that the loop is still going.
                        TcpTraffic.RecordEvent(TcpTrafficEvent.ConnectFailed,
                            $"{endpoint} — {reason}; still retrying (attempt {failureStreak})", isError: true);
                    }
                }
                else if (failure is null)
                {
                    // ReceiveLoopAsync returned normally with ct live → a 0-byte read (EOF).
                    TcpTraffic.RecordEvent(TcpTrafficEvent.Disconnected,
                        $"peer closed the connection; {TcpRetryHint}", isError: true);
                }
                else
                {
                    TcpTraffic.RecordEvent(TcpTrafficEvent.Disconnected,
                        $"connection lost — {DescribeTcpFailure(failure)}; {TcpRetryHint}", isError: true);
                }

                SetConnectionState(new TcpState.Disconnected(), ct);
                try { await Task.Delay(_tcpReconnectIntervalMs, ct); } catch { break; }
            }
        }
    }

    /// <summary>
    /// Reads newline-delimited reply lines from the server for the life of the connection and routes each
    /// to <see cref="HandleReplyLine"/>. Doubles as the disconnect detector: a 0-byte read (EOF) ends the
    /// loop, which unwinds the connection loop into its reconnect delay.
    /// </summary>
    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var acc = new List<byte>();
        while (!ct.IsCancellationRequested)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
            if (read == 0) break; // EOF — peer closed.
            for (var i = 0; i < read; i++) acc.Add(buffer[i]);

            int nl;
            while ((nl = acc.IndexOf((byte)'\n')) >= 0)
            {
                var line = Encoding.UTF8.GetString(acc.GetRange(0, nl).ToArray()).Trim();
                acc.RemoveRange(0, nl + 1);
                if (line.Length > 0)
                {
                    // Logged before it is acted on; the byte count includes the "\n" (and any trimmed "\r").
                    TcpTraffic.RecordIncoming(line, nl + 1);
                    HandleReplyLine(line);
                }
            }
            // A peer that never sends newlines must not grow this unbounded.
            if (acc.Count > 64 * 1024)
            {
                TcpTraffic.RecordEvent(TcpTrafficEvent.ReceiveOverflow,
                    $"{acc.Count} bytes without a newline discarded", isError: true);
                acc.Clear();
            }
        }
    }

    /// <summary>
    /// Parses one reply line into a cache verdict and completes the matching pending <c>query</c>. Accepts
    /// both the bare-token form the server dev specified — <c>OK</c> / <c>no_data</c> — and a JSON form
    /// <c>{"id":"…","status":"ok"|"no_data"}</c> that additionally correlates by the <c>query</c>'s id.
    /// Anything else (e.g. an ack for the manifest upload) is ignored.
    /// </summary>
    private void HandleReplyLine(string line)
    {
        string? id = null;
        bool? needData = null;

        if (line.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            needData = false;
        }
        else if (line.Equals("no_data", StringComparison.OrdinalIgnoreCase) ||
                 line.Equals("nodata", StringComparison.OrdinalIgnoreCase))
        {
            needData = true;
        }
        else if (line.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    id = idEl.GetString();
                if (root.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String)
                {
                    var st = stEl.GetString();
                    if (string.Equals(st, "ok", StringComparison.OrdinalIgnoreCase)) needData = false;
                    else if (string.Equals(st, "no_data", StringComparison.OrdinalIgnoreCase)) needData = true;
                }
            }
            catch { /* malformed JSON — ignore */ }
        }

        if (needData is bool nd) CompleteCacheWaiter(id, nd);
    }

    /// <summary>Registers a waiter for the reply to the <c>query</c> with this id, before the send, so no
    /// reply can arrive before the waiter exists.</summary>
    private TaskCompletionSource<bool> RegisterCacheWaiter(string id)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cacheGate)
        {
            _cachePending[id] = tcs;
            _cacheOrder.Enqueue(id);
        }
        return tcs;
    }

    /// <summary>Completes the waiter a reply belongs to — by echoed id when present, else the oldest one
    /// still pending (FIFO). A superseded selection leaves its waiter here as a tombstone so the reply the
    /// server still sends for it is consumed in order rather than misattributed to the next selection.</summary>
    private void CompleteCacheWaiter(string? id, bool needData)
    {
        lock (_cacheGate)
        {
            TaskCompletionSource<bool>? tcs = null;
            if (id is not null && _cachePending.Remove(id, out var byId))
            {
                tcs = byId;
            }
            else if (id is null)
            {
                while (_cacheOrder.Count > 0)
                {
                    var front = _cacheOrder.Dequeue();
                    if (_cachePending.Remove(front, out var f)) { tcs = f; break; }
                }
            }
            tcs?.TrySetResult(needData);
        }
    }

    /// <summary>Drops a waiter whose <c>start</c> never made it onto the wire (nothing will reply to it).</summary>
    private void CancelCacheWaiter(string id)
    {
        lock (_cacheGate)
        {
            if (_cachePending.Remove(id, out var tcs)) tcs.TrySetResult(true);
        }
    }

    /// <summary>Releases every pending waiter on disconnect (fail open); the socket is gone, so a follow-up
    /// points send simply no-ops.</summary>
    private void ClearCacheWaiters()
    {
        lock (_cacheGate)
        {
            foreach (var tcs in _cachePending.Values) tcs.TrySetResult(true);
            _cachePending.Clear();
            _cacheOrder.Clear();
        }
    }

    /// <summary>Awaits the server's cache verdict, failing open (return true → send the points) if no reply
    /// arrives within <see cref="CacheReplyTimeoutMs"/>. A cancel from a newer selection or a disconnect
    /// propagates as <see cref="OperationCanceledException"/> so the caller sends nothing further.</summary>
    private async Task<bool> AwaitCacheReplyAsync(TaskCompletionSource<bool> waiter, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CacheReplyTimeoutMs);
        try
        {
            return await waiter.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TcpTraffic.RecordEvent(TcpTrafficEvent.ReplyTimeout,
                $"no reply to query within {CacheReplyTimeoutMs} ms — sending rhythm anyway (fail-open)", isError: true);
            return true; // timed out — fail open
        }
    }

    /// <summary>
    /// On every connect, sends the dataset <b>catalog only</b> — the merged <c>manifest.txt</c> — as an
    /// <c>upload</c> header line followed by its UTF-8 bytes. The server learns every rhythm's id, title
    /// and metadata up front, but no sample bodies: those arrive one rhythm at a time from
    /// <see cref="SendRhythm"/> as the user selects them. The manifest is the live (overlay-merged) one,
    /// so it reflects the instructor's edits, not just the shipped Assets copy.
    ///
    /// <para>Nothing on this path is encrypted: the TCP target is user-editable in every edition, so
    /// whoever the app is pointed at receives the catalog in the clear. Confidentiality is left to the
    /// transport.</para>
    /// </summary>
    private async Task SendManifestAsync(Socket socket, CancellationToken ct)
    {
        try
        {
            // Use the already-loaded merged manifest and serialize it exactly as the pack export would
            // have written the manifest.txt entry. It is loaded on startup, long before a TCP connection
            // exists; don't reload from this background thread (ManifestChanged has UI subscribers). If it
            // is somehow absent, skip the catalog — same best-effort spirit as the former bulk upload.
            var manifest = Repository.Manifest();
            if (manifest is null)
            {
                TcpTraffic.RecordEvent(TcpTrafficEvent.NotSent, "manifest.txt: catalog not loaded");
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(PathologyParser.SerializeManifest(manifest));
            await _sendLock.WaitAsync(ct);
            var recorded = false;
            try
            {
                // Cancelled while waiting for the lock: the write would put nothing on the wire, so log nothing.
                if (ct.IsCancellationRequested) return;

                var upload = new TcpMessage.UploadMessage
                {
                    Id = Guid.NewGuid().ToString(),
                    Filename = "manifest.txt",
                    Size = bytes.Length,
                };
                // Encoded once: the same JSON feeds the log entry and the wire bytes.
                var headerJson = TcpProtocol.Encode(upload);
                TcpTraffic.RecordOutgoing(upload, headerJson);
                recorded = true;
                await SendAllAsync(socket, Encoding.UTF8.GetBytes(headerJson + "\n"), ct);
                TcpTraffic.RecordOutgoingPayload("manifest.txt", bytes);
                await SendAllAsync(socket, bytes, ct);
            }
            catch (OperationCanceledException) when (recorded)
            {
                // Still inside the lock, so the note lands right after the rows it corrects.
                RecordSendCancelled("upload manifest.txt");
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a failed catalog push must not tear down an otherwise usable command channel.
            RecordSendFailure(ex, ct);
        }
    }

    /// <summary>
    /// On a user rhythm selection: probes the server's cache with a <c>query</c> (pathology + content
    /// <see cref="WaveformHash">hash</see>), and only if it replies <c>no_data</c> sends the whole rhythm as
    /// a single <c>rhythm</c> message carrying every stored lead's <b>raw</b> <c>.dat</c> samples. This is
    /// <b>not</b> the play command — that is <see cref="SendStartCommand"/>, sent from the start button.
    /// Reading the raw file, hashing and sending run off the UI thread; selecting another rhythm supersedes
    /// an in-flight send (<see cref="StopRhythmSend"/>).
    ///
    /// <para>If the rhythm is playing — <paramref name="isMonitorRunning"/> and the user's last start/stop press
    /// was start — the server is still showing the previous rhythm, so the selection also switches it over:
    /// <c>query</c> → (<c>rhythm</c> on <c>no_data</c>) → <c>stop</c> → <c>start</c>. The playback pair waits for
    /// the verdict so <c>start</c> always follows data the server has.</para>
    /// </summary>
    public void SendRhythmData(
        string? pathology, string? name = null, EcgCalibration? calibration = null, bool isMonitorRunning = false)
    {
        var socket = _tcpSocket;
        if (pathology is null) return;
        if (socket is null || TcpConnectionState is not TcpState.Connected)
        {
            // Worth a log line only when the user expects the link to be up (connecting / between retries).
            if (IsTcpLinkOn) TcpTraffic.RecordEvent(TcpTrafficEvent.NotSent, $"query pathology={pathology}: not connected");
            return;
        }

        BeginRhythmSend(socket, pathology, name, calibration, restartPlayback: isMonitorRunning && _playbackRequested);
    }

    /// <summary>Starts the off-thread query → verdict → rhythm (→ stop → start) send on <paramref name="socket"/>,
    /// superseding any send still in flight. The caller has already established that the socket is the live
    /// connection: <see cref="SendRhythmData"/> through its state gate, the connection loop because it just
    /// connected it.</summary>
    private void BeginRhythmSend(
        Socket socket, string pathology, string? name, EcgCalibration? calibration, bool restartPlayback)
    {
        var rate = calibration?.SampleRateHz ?? new EcgCalibration().SampleRateHz;

        // A fresh selection supersedes any send still in flight for the previous rhythm.
        StopRhythmSend();
        var cts = new CancellationTokenSource();
        _streamCts = cts;
        _ = Task.Run(() => SendRhythmDataAsync(socket, pathology, name, rate, restartPlayback, cts.Token));
    }

    /// <summary>Sends the <c>start</c> ("play the selected rhythm") command. Invoked from the start button; the
    /// rhythm's samples were already pushed by <see cref="SendRhythmData"/>, which also re-sends <c>start</c> (after
    /// a <c>stop</c>) when the user selects another rhythm while it plays.</summary>
    public void SendStartCommand(string? pathology = null, string? name = null, EcgCalibration? calibration = null)
    {
        _playbackRequested = true;

        var socket = _tcpSocket;
        if (socket is null || TcpConnectionState is not TcpState.Connected)
        {
            if (IsTcpLinkOn)
            {
                TcpTraffic.RecordEvent(TcpTrafficEvent.NotSent,
                    (pathology is null ? "start" : $"start pathology={pathology}") + ": not connected");
            }
            return;
        }

        var rate = calibration?.SampleRateHz ?? new EcgCalibration().SampleRateHz;
        _ = SendLineAsync(socket, BuildStartCommand(pathology, name, rate));
    }

    private static TcpMessage.StartCommand BuildStartCommand(string? pathology, string? name, float sampleRateHz)
    {
        var paramsMap = new Dictionary<string, string>();
        if (pathology is not null) paramsMap["pathology"] = pathology;
        if (name is not null) paramsMap["name"] = name;
        return new TcpMessage.StartCommand
        {
            Id = Guid.NewGuid().ToString(),
            SampleRate = (int)Math.Round(sampleRateHz),
            Params = paramsMap,
        };
    }

    public void SendStopCommand()
    {
        _playbackRequested = false;
        // Also cancels a selection's pending stop → start, so it can't restart playback behind this stop.
        StopRhythmSend();

        var socket = _tcpSocket;
        if (socket is null || TcpConnectionState is not TcpState.Connected)
        {
            if (IsTcpLinkOn) TcpTraffic.RecordEvent(TcpTrafficEvent.NotSent, "stop: not connected");
            return;
        }

        _ = SendLineAsync(socket, new TcpMessage.StopCommand { Id = Guid.NewGuid().ToString() });
    }

    /// <summary>
    /// Order-independent 64-bit fingerprint (FNV-1a, hex) of the raw samples, sent as the <c>query</c>'s
    /// <c>hash</c> and used as the cache key. An instructor's edit keeps the pathology id but changes the
    /// samples, so keying the server cache by (pathology, hash) makes an edited rhythm miss the cache and
    /// resend. Leads are visited in token order and delimited, so dictionary iteration order and lead
    /// boundaries cannot change the result.
    /// </summary>
    private static string WaveformHash(IReadOnlyDictionary<Lead, int[]> leads)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var h = offset;
        foreach (var lead in leads.Keys.OrderBy(l => l.ToString(), StringComparer.Ordinal))
        {
            foreach (var ch in lead.ToString()) h = (h ^ ch) * prime;
            h = (h ^ (byte)'|') * prime; // delimiter so lead boundaries can't blur together
            foreach (var s in leads[lead])
            {
                var u = (uint)s;
                h = (h ^ (byte)u) * prime;
                h = (h ^ (byte)(u >> 8)) * prime;
                h = (h ^ (byte)(u >> 16)) * prime;
                h = (h ^ (byte)(u >> 24)) * prime;
            }
        }
        return h.ToString("x16");
    }

    /// <summary>Cancels the in-flight one-shot rhythm dump, if any. Called before starting the next one,
    /// on stop, and on disconnect.</summary>
    private void StopRhythmSend()
    {
        var cts = _streamCts;
        _streamCts = null;
        try { cts?.Cancel(); cts?.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Reads the selected rhythm's raw <c>.dat</c> (overlay-merged, so it reflects instructor edits), probes
    /// the cache with <c>query</c>, and waits for the verdict: <c>OK</c> (already cached → send nothing) or
    /// <c>no_data</c> (or a timed-out/absent reply, which fails open) → send the whole rhythm as a single
    /// <c>rhythm</c> message — every stored lead's raw ADC samples, not a stream. With
    /// <paramref name="restartPlayback"/> it then sends <c>stop</c> → <c>start</c> so the playing server switches to
    /// this rhythm. Bails the moment the socket changes, disconnects, or the send is superseded by a newer selection
    /// or a Stop.
    /// </summary>
    private async Task SendRhythmDataAsync(
        Socket socket,
        string pathology,
        string? name,
        float sampleRateHz,
        bool restartPlayback,
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        var waiter = RegisterCacheWaiter(id);
        try
        {
            // Raw stored samples straight from the .dat (baseline-centered on 1024), not the monitor's
            // baseline-zeroed / derived-lead render. Empty when the file is missing → nothing to send.
            var leads = ReadRawLeads(pathology);
            if (leads.Count == 0)
            {
                CancelCacheWaiter(id);
                TcpTraffic.RecordEvent(TcpTrafficEvent.NotSent, $"rhythm {pathology}: no raw samples");
                return;
            }

            var query = new TcpMessage.QueryCommand
            {
                Id = id,
                Pathology = pathology,
                Hash = WaveformHash(leads),
            };
            if (!await SendLineAsync(socket, query, ct))
            {
                CancelCacheWaiter(id);
                return;
            }

            // Gate the data on the verdict: skip when the server already has this (pathology, hash).
            var needData = await AwaitCacheReplyAsync(waiter, ct);
            // Socket identity alone: both disconnect paths (DisconnectTcp, the loop's finally) clear _tcpSocket
            // synchronously, whereas TcpConnectionState reaches this pool thread through the UI queue and can be stale.
            if (_tcpSocket != socket) return;

            var rate = sampleRateHz > 0 ? sampleRateHz : new EcgCalibration().SampleRateHz;
            if (needData)
            {
                var sent = await SendLineAsync(socket, new TcpMessage.RhythmMessage
                {
                    Id = Guid.NewGuid().ToString(),
                    Pathology = pathology,
                    SampleRate = (int)Math.Round(rate),
                    Leads = leads,
                }, ct);
                if (!sent) return;
            }

            // The rhythm is playing: the server still shows the previous one, so switch it over. Sent only now,
            // after the verdict and any rhythm message, so the start follows data the server has.
            if (!restartPlayback) return;
            if (!await SendLineAsync(socket, new TcpMessage.StopCommand { Id = Guid.NewGuid().ToString() }, ct)) return;
            await SendLineAsync(socket, BuildStartCommand(pathology, name, rate), ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection, or disconnected.
        }
        catch
        {
            // Socket died mid-send; the connection loop handles the reconnect.
        }
    }

    /// <summary>Reads one pathology's stored leads as raw ADC samples, dropping empty leads. Runs off the UI
    /// thread (the connection pump calls this); a missing or unparseable file yields an empty map.</summary>
    private IReadOnlyDictionary<Lead, int[]> ReadRawLeads(string pathology)
    {
        var result = new Dictionary<Lead, int[]>();
        try
        {
            var file = Repository.ReadPathology(pathology);
            if (file is null) return result;
            foreach (var (lead, stream) in file.Leads)
            {
                if (stream.Samples.Length > 0) result[lead] = stream.Samples;
            }
        }
        catch { /* leave empty on any read/parse error */ }
        return result;
    }

    /// <summary>Encodes and sends one newline-terminated frame. Returns false if the socket failed or the send
    /// was cancelled, which the caller treats as end-of-send.</summary>
    private async Task<bool> SendLineAsync(Socket socket, TcpMessage message, CancellationToken ct = default)
    {
        // Encoded once: the JSON string feeds both the log entry (a bounded preview) and the wire bytes, which
        // are written straight into one buffer so a multi-MB rhythm line isn't copied again just to append "\n".
        var line = TcpProtocol.Encode(message);
        var bytes = new byte[Encoding.UTF8.GetByteCount(line) + 1];
        Encoding.UTF8.GetBytes(line, 0, line.Length, bytes, 0);
        bytes[^1] = (byte)'\n';
        try
        {
            await _sendLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        var recorded = false;
        try
        {
            // Superseded while waiting for the lock: SendAsync with a cancelled token puts nothing on the wire,
            // so don't log a frame that never went out.
            if (ct.IsCancellationRequested) return false;

            // Inside the lock, immediately before the bytes go out: log order == wire order.
            TcpTraffic.RecordOutgoing(message, line);
            recorded = true;
            await SendAllAsync(socket, bytes, ct);
            return true;
        }
        catch (OperationCanceledException) when (recorded)
        {
            // Cancelled mid-write: the row above claims the whole frame, but only part of it may have gone out.
            RecordSendCancelled(message.Type);
            return false;
        }
        catch (Exception ex)
        {
            RecordSendFailure(ex, ct);
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Logs a failed send as <see cref="TcpTrafficEvent.SendFailed"/> unless it was really a
    /// cancellation: a superseded selection, or the user's own disconnect (which switches the link off before it
    /// closes the socket, so the in-flight write surfaces as a disposed/aborted socket).</summary>
    private void RecordSendFailure(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException || ct.IsCancellationRequested || !IsTcpLinkOn) return;
        TcpTraffic.RecordEvent(TcpTrafficEvent.SendFailed, $"{ex.GetType().Name}: {ex.Message}", isError: true);
    }

    /// <summary>Notes that a frame already logged as outgoing had its write cancelled (a newer selection, Stop, or
    /// the link switched off): part of it may be on the wire, so its row overstates what the server received.</summary>
    private void RecordSendCancelled(string what) =>
        TcpTraffic.RecordEvent(TcpTrafficEvent.NotSent,
            $"{what}: send cancelled (superseded or stopped) — frame may be incomplete");

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

    /// <summary>Marshals a connection-state change onto the UI thread (sockets run on the pool). A change queued by a
    /// connection loop is dropped if that loop's <paramref name="ct"/> was cancelled before it ran: DisconnectTcp /
    /// ConnectTcp cancel the old loop and then set their own state synchronously, so a stale "Connected" still sitting
    /// in the queue must not land on top of it (the header would read Connected while the link is off).</summary>
    private void SetConnectionState(TcpState state, CancellationToken ct = default)
    {
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (!ct.IsCancellationRequested) TcpConnectionState = state;
            });
        }
        else
        {
            TcpConnectionState = state;
        }
    }
}
