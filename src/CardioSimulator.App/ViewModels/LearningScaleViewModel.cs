using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CardioSimulator.App.Controls;
using CardioSimulator.App.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.ViewModels;

/// <summary>Mastery band of a course section, driving its badge/dot colour.</summary>
public enum SectionStatus { Good, Warning, Critical }

/// <summary>Which adaptive-plan bucket a task falls in (drives its colour, label, and badge). <see cref="Next"/>
/// is the gap-aware "continue from here" pointer; <see cref="Critical"/> = needs attention (&lt;40%);
/// <see cref="Growth"/> = in progress (40–80%). <see cref="Fix"/> is retained for compatibility (unused by
/// the current priority model).</summary>
public enum PlanTaskType { Next, Critical, Growth, Fix }

/// <summary>One subtopic (a course Подтема, or a standalone lecture/leaf Тема), with its 0–100 mastery.</summary>
public sealed class LsSubtopic
{
    /// <summary>Stable unique id of the underlying course node (its lecture/topic id) — used for task
    /// ids and expansion state, never shown.</summary>
    public required string Id { get; init; }

    /// <summary>The canonical taxonomy subtopic key (<c>X.Y</c>) this node teaches, resolved from the
    /// course node's <c>subsection:</c>, or null when the author didn't map it. This is the key student
    /// mastery is looked up by (see <see cref="MasteryReport.BySubtopic"/>) and the code shown before the
    /// name; a null key means the node simply can't be scored yet.</summary>
    public string? Key { get; init; }

    public required string Name { get; init; }
    public int Progress { get; set; }

    /// <summary>False when this subtopic has no graded attempts yet (or isn't mapped to the taxonomy) —
    /// the dashboard shows it as "not started" (—) rather than a misleading 0%/critical.</summary>
    public bool HasData { get; set; } = true;
}

/// <summary>One top-level course section (a Тема), its aggregate mastery, band, and subtopics.</summary>
public sealed class LsSection
{
    public required int Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Taxonomy subtopic key for a <b>leaf</b> Тема that is itself content (so the section
    /// carries its own mastery with no subtopics), or null for a normal grouping section whose mastery
    /// aggregates over its <see cref="Subtopics"/>.</summary>
    public string? Key { get; init; }

    /// <summary>The top-level taxonomy section number («Раздел N») this section maps to (from the Тема's
    /// own <c>subsection:</c>, else its first mapped subtopic), or 0 when none of its nodes carry a key.
    /// Used only as a fallback: a graded attempt tagged to the whole section still marks it even when its
    /// subsection matches no listed subtopic. See <see cref="ApplyReport"/>.</summary>
    public int Section { get; init; }

    public int Progress { get; set; }
    public SectionStatus Status { get; set; }
    public required List<LsSubtopic> Subtopics { get; init; }

    /// <summary>False when running on real results and no subtopic in this section has been assessed
    /// yet (e.g. pure-theory sections with no taxonomy coverage). Always true in the demo-seed mode.</summary>
    public bool HasData { get; set; } = true;
}

/// <summary>A generated adaptive-plan task (a subtopic to work on). Language-agnostic: the screen
/// composes its label/badge/detail from <see cref="Type"/> and the numbers.</summary>
public sealed record PlanTask(
    string Id,
    int SectionId,
    string SectionName,
    string SubtopicId,
    string SubtopicName,
    PlanTaskType Type,
    int Progress);

/// <summary>
/// State + logic for the Learning Quality («Качество обучения») dashboard. The course map — which sections
/// (Темы) and subtopics (Подтемы) exist — is built from the <b>real course package loaded into the
/// app</b> (<see cref="Course"/>, from the <see cref="Data.CourseRepository"/>). Progress is real
/// mastery: each subtopic maps to a taxonomy subtopic key via its authored <c>subsection:</c>, and its
/// score is rolled up from graded results (<see cref="MasteryRollup"/>). Subtopics with no attempts (or
/// no taxonomy mapping) show as "not started" (—) rather than a fabricated number, and nothing is
/// invented to pre-populate the screen. Adaptive-task generation (Leitner-style buckets), mark-as-solved
/// acknowledgement, and aggregate stats all read from that real data; only the acknowledged-task flags
/// persist (<see cref="AppPaths.LearningScaleFile"/>). The screen re-renders on <see cref="StateChanged"/>.
/// Section/subtopic names come from the course and stay in the source language; only chrome is localized.
/// </summary>
public sealed class LearningScaleViewModel
{
    /// <summary>Raised whenever the model changes (a task solved / progress updated) so the view re-renders.</summary>
    public event Action? StateChanged;

    private List<LsSection> _sections;
    private readonly HashSet<string> _completed = new();

    // Course switching (A1, customer 28-08): the dashboard can offer a course dropdown and rebuild the
    // map for the picked course. Null reader = single fixed course (the old behaviour).
    private readonly Language _language;
    private readonly IReadOnlyList<CourseOption> _courses;
    private readonly Func<string, Course?>? _courseReader;
    private string? _selectedCourseId;

    /// <summary>The real mastery for the <see cref="SelectedStudent"/> (or the whole cohort when none is
    /// picked), rolled up from graded exam attempts. Recomputed on every pick. When present and
    /// non-empty, subtopic/section progress is derived from it rather than the seed.</summary>
    private MasteryReport? _report;

    /// <summary>True when driven by real results (a non-empty <see cref="_report"/>). In this mode
    /// progress is computed from attempts each launch, "mark as solved" doesn't fabricate mastery, and
    /// stats read from the report.</summary>
    private bool _realData;

    /// <summary>The instructor's registered-student roster (from the Students screen), newest first.
    /// Empty when none is registered — the dashboard then shows the whole-cohort aggregate and a
    /// placeholder chip instead of a picker.</summary>
    private readonly IReadOnlyList<Student> _roster;

    /// <summary>Rolls up the mastery report for a given student (or the whole cohort when null). Injected
    /// so the view-model stays free of the results store / taxonomy and can recompute on each pick.</summary>
    private readonly Func<Student?, MasteryReport> _masteryFor;

    /// <summary>The student whose progress is currently shown, or null for the whole-cohort aggregate.</summary>
    private Student? _selectedStudent;

    /// <summary>True once the student has solved at least one task this run (drives "updated just now").</summary>
    public bool HasInteracted { get; private set; }

    /// <summary>Demo baseline mirrored from the prototype (average answer time, in seconds).</summary>
    public const int AvgSeconds = 47;

    /// <summary>True when the dashboard is showing real, results-driven mastery (vs. the demo seed).</summary>
    public bool IsRealData => _realData;

    /// <summary>
    /// Creates the dashboard over the <paramref name="course"/> actually loaded in the app (its
    /// Темы/Подтемы). <paramref name="language"/> selects which localized node names to show.
    /// <paramref name="roster"/> is the instructor's registered students (from the Students screen), and
    /// <paramref name="masteryFor"/> rolls up the real per-subtopic mastery for a given student (or the
    /// whole cohort when null). The dashboard opens on the first registered student (newest first), or
    /// the cohort aggregate when the roster is empty. Nothing is fabricated; a null course yields an
    /// empty map (the screen shows a "load a course" prompt).
    /// </summary>
    public LearningScaleViewModel(
        Course? course,
        Language language,
        IReadOnlyList<Student> roster,
        Func<Student?, MasteryReport> masteryFor,
        Student? initialStudent = null,
        IReadOnlyList<CourseOption>? courses = null,
        Func<string, Course?>? courseReader = null,
        string? initialCourseId = null)
    {
        _language = language;
        _courses = courses ?? Array.Empty<CourseOption>();
        _courseReader = courseReader;
        _selectedCourseId = initialCourseId;
        _sections = BuildCourse(course, language);
        _roster = roster ?? Array.Empty<Student>();
        _masteryFor = masteryFor ?? (_ => MasteryReport.Empty);
        if (initialStudent is not null)
        {
            var match = _roster.FirstOrDefault(s => s.Id == initialStudent.Id ||
                (string.Equals(s.FullName?.Trim(), initialStudent.FullName?.Trim(), StringComparison.CurrentCultureIgnoreCase) &&
                 string.Equals(s.Group?.Trim(), initialStudent.Group?.Trim(), StringComparison.CurrentCultureIgnoreCase)));
            _selectedStudent = match ?? initialStudent;
        }
        else
        {
            // Open on the first registered student (roster is newest-first); cohort aggregate when empty.
            _selectedStudent = _roster.Count > 0 ? _roster[0] : null;
        }

        // Overlay the selected student's real mastery (an empty report marks every subtopic "no data").
        ApplySelectedReport();
        // Only the adaptive-plan acknowledgement flags persist; progress is always derived from results.
        LoadCompleted();
    }

    /// <summary>Convenience overload for a fixed, pre-computed report and no roster (demo / cohort view).</summary>
    public LearningScaleViewModel(Course? course, Language language = Language.RU, MasteryReport? report = null)
        : this(course, language, Array.Empty<Student>(), _ => report ?? MasteryReport.Empty)
    {
    }

    public IReadOnlyList<LsSection> Sections => _sections;

    /// <summary>Resolves the key/«главный» test bound to a subtopic block key («X.Y»), or null when the block
    /// has none (A3, customer 28-08). Injected by the host, which owns the test repository; drives each
    /// block's «Сдать» affordance on the dashboard.</summary>
    public Func<string, Test?>? PrimaryTestFor { get; set; }

    /// <summary>Available teaching courses (id + display name), for the dashboard's course dropdown.
    /// Empty when the host didn't wire course switching (single fixed course).</summary>
    public IReadOnlyList<CourseOption> Courses => _courses;

    /// <summary>The course whose progress is shown, or null when course switching isn't wired.</summary>
    public string? SelectedCourseId => _selectedCourseId;

    /// <summary>Switches the dashboard to another course, rebuilding its section map and re-overlaying the
    /// selected student's mastery. No-op when switching isn't wired or the course is already shown.</summary>
    public void SelectCourse(string? courseId)
    {
        if (_courseReader is null || courseId is null || courseId == _selectedCourseId) return;
        _selectedCourseId = courseId;
        _sections = BuildCourse(_courseReader(courseId), _language);
        ApplySelectedReport();      // re-derive per-subtopic mastery onto the new map
        StateChanged?.Invoke();
    }

    /// <summary>One selectable course on the dashboard's course dropdown.</summary>
    public sealed record CourseOption(string Id, string Name);

    /// <summary>The instructor's registered students (newest first); empty when none are registered.</summary>
    public IReadOnlyList<Student> Roster => _roster;

    /// <summary>The student whose progress is shown, or null for the whole-cohort aggregate.</summary>
    public Student? SelectedStudent => _selectedStudent;

    /// <summary>Switches the dashboard to <paramref name="student"/> (null = whole-cohort aggregate),
    /// recomputing real mastery for them and re-rendering. No-op when already selected.</summary>
    public void SelectStudent(Student? student)
    {
        if (ReferenceEquals(student, _selectedStudent)) return;
        _selectedStudent = student;
        ApplySelectedReport();
        StateChanged?.Invoke();
    }

    /// <summary>Rolls up the selected student's (or cohort's) mastery and overlays it onto the course map.</summary>
    private void ApplySelectedReport()
    {
        _report = _masteryFor(_selectedStudent);
        _realData = _report is { HasData: true };
        ApplyReport(_report ?? MasteryReport.Empty);
    }

    // ── Aggregate stats ─────────────────────────────────────────────────────

    private double AverageProgress =>
        _sections.Count == 0 ? 0 : _sections.Average(s => s.Progress);

    /// <summary>Overall course progress: real answer accuracy when any question has been graded, else
    /// the average of section progress (0 on a fresh install). Rounded to a whole percent.</summary>
    public int GlobalProgressPercent => _report is { TotalAnswered: > 0 } r
        ? (int)Math.Round(100.0 * r.TotalCorrect / r.TotalAnswered)
        : (int)Math.Round(AverageProgress);

    /// <summary>Number of graded questions answered — real, straight from the results (0 when none).</summary>
    public int CasesCount => _report?.TotalAnswered ?? 0;

    /// <summary>Accuracy readout (invariant "78.4"-style, dot decimal), or <c>—</c> when nothing has
    /// been graded yet. Never a fabricated value.</summary>
    public string AccuracyDisplay => _report is { TotalAnswered: > 0 } r
        ? (100.0 * r.TotalCorrect / r.TotalAnswered).ToString("F1", CultureInfo.InvariantCulture)
        : "—";

    /// <summary>Trend chip next to the accuracy — always empty now; no delta is fabricated.</summary>
    public string AccuracyChange => string.Empty;

    /// <summary>Rank readout: a trophy once the student climbs off the ladder, else "#N" (1 = top).</summary>
    public string RankDisplay
    {
        get
        {
            var idx = Math.Min((int)Math.Floor(_completed.Count / 1.5), 6);
            return idx >= 6 ? "🏆" : "#" + (6 - idx);
        }
    }

    // ── Adaptive plan ───────────────────────────────────────────────────────

    /// <summary>
    /// Generates today's adaptive plan by the customer's priority model (28-08-2026):
    /// <list type="number">
    /// <item><b>Next step</b> — the gap-aware "continue from here" pointer: walking blocks in course order,
    /// the last <em>un-started</em> block up to the furthest started one (i.e. the last gap), else the block
    /// right after the furthest started. Only proposed once the student has started at least one block.</item>
    /// <item><b>Needs attention</b> — assessed blocks below 40%, weakest first.</item>
    /// <item><b>In progress</b> — assessed blocks in the 40–80% band.</item>
    /// </list>
    /// A "block" is a subtopic; a leaf section with no subtopics is its own block. Already-acknowledged
    /// tasks are filtered out, and ids are namespaced by the picked student.
    /// </summary>
    public IReadOnlyList<PlanTask> GenerateTasks()
    {
        // Blocks in course order (a subtopic, or a leaf section standing in as its own block).
        var ordered = new List<(LsSection section, LsSubtopic sub)>();
        foreach (var section in _sections)
        {
            if (section.Subtopics.Count == 0)
                ordered.Add((section, LeafBlock(section)));
            else
                foreach (var sub in section.Subtopics) ordered.Add((section, sub));
        }

        // Namespace task ids by the picked student so a task acknowledged for one student never hides
        // the same subtopic in another student's plan ("all" for the cohort aggregate).
        var studentKey = _selectedStudent?.Id ?? "all";
        var tasks = new List<PlanTask>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        // ── 1. Next step (gap-aware) ──
        var lastStarted = -1;
        for (var i = 0; i < ordered.Count; i++)
            if (ordered[i].sub.HasData) lastStarted = i;
        if (lastStarted >= 0)
        {
            var lastGap = -1;
            for (var i = 0; i < lastStarted; i++)
                if (!ordered[i].sub.HasData) lastGap = i; // keep the highest-index gap before the furthest started
            var nextIdx = lastGap >= 0 ? lastGap : lastStarted + 1;
            if (nextIdx >= 0 && nextIdx < ordered.Count)
            {
                var (sec, sub) = ordered[nextIdx];
                tasks.Add(Make(sec, sub, PlanTaskType.Next, "n"));
                used.Add(sub.Id);
            }
        }

        // ── 2. Needs attention (assessed, <40%, weakest first) ──
        foreach (var (sec, sub) in ordered
                     .Where(x => x.sub.HasData && x.sub.Progress < 40 && !used.Contains(x.sub.Id))
                     .OrderBy(x => x.sub.Progress).Take(3))
        {
            tasks.Add(Make(sec, sub, PlanTaskType.Critical, "c"));
            used.Add(sub.Id);
        }

        // ── 3. In progress (assessed, 40–80%) ──
        foreach (var (sec, sub) in ordered
                     .Where(x => x.sub.HasData && x.sub.Progress is >= 40 and <= 80 && !used.Contains(x.sub.Id))
                     .OrderBy(x => x.sub.Progress).Take(3))
        {
            tasks.Add(Make(sec, sub, PlanTaskType.Growth, "g"));
            used.Add(sub.Id);
        }

        return tasks.Where(t => !_completed.Contains(t.Id)).ToList();

        PlanTask Make(LsSection section, LsSubtopic sub, PlanTaskType type, string prefix) =>
            new($"{prefix}-{studentKey}-{section.Id}-{sub.Id}", section.Id, section.Name, sub.Key ?? sub.Id, sub.Name, type, sub.Progress);
    }

    /// <summary>Wraps a leaf section (no subtopics — it carries its own mastery) as a single block so the
    /// adaptive plan can point at it like any subtopic.</summary>
    private static LsSubtopic LeafBlock(LsSection section) => new()
    {
        Id = $"sec-{section.Id}",
        Key = section.Key,
        Name = section.Name,
        Progress = section.Progress,
        HasData = section.HasData,
    };

    public bool IsCompleted(string taskId) => _completed.Contains(taskId);

    /// <summary>
    /// Acknowledges a plan task as handled: records the flag (so it drops off the plan), persists, and
    /// notifies. Real mastery only moves when the student re-takes a graded test, so this never
    /// fabricates progress. Returns the section's current progress (for the toast), or null if the task
    /// was unknown / already acknowledged.
    /// </summary>
    public int? MarkDone(string taskId)
    {
        if (_completed.Contains(taskId)) return null;
        var task = GenerateTasks().FirstOrDefault(t => t.Id == taskId);
        if (task is null) return null;

        _completed.Add(taskId);
        HasInteracted = true;

        Save();
        StateChanged?.Invoke();
        return _sections.FirstOrDefault(s => s.Id == task.SectionId)?.Progress;
    }

    /// <summary>The mastery band a section's progress falls in, matching the dashboard legend and chart
    /// colours: Освоено ≥80 (Good) · В процессе 40–80 (Warning) · Требует внимания &lt;40 (Critical). The 40
    /// floor aligns with the chart histogram (<see cref="LearningScaleScreen"/>) and the legend text
    /// (<c>ls_chart_legend_*</c>); it replaces a stale 50 that only affected the section badges/dots
    /// (customer request 28-08-2026 — grade per the mockup legend).</summary>
    private static SectionStatus BandFor(int progress) =>
        progress >= 80 ? SectionStatus.Good : progress >= 40 ? SectionStatus.Warning : SectionStatus.Critical;

    // ── Real mastery (from graded results) ──────────────────────────────────

    /// <summary>Overlays real, results-driven mastery onto the seeded course outline: each subtopic
    /// takes its rolled-up accuracy (0 and flagged "no data" when it has no attempts), and each
    /// section aggregates over only its assessed subtopics so an all-theory section with no test isn't
    /// dragged to a false "critical".</summary>
    private void ApplyReport(MasteryReport report)
    {
        foreach (var section in _sections)
        {
            var assessed = new List<int>();
            foreach (var sub in section.Subtopics)
            {
                var has = sub.Key is { } key && report.BySubtopic.TryGetValue(key, out var stat)
                    ? (true, stat.Progress)
                    : (false, 0);
                sub.HasData = has.Item1;
                sub.Progress = has.Item2;
                if (has.Item1) assessed.Add(has.Item2);
            }

            // A leaf section (no subtopics) carries its own mastery via its Key; a grouping section
            // aggregates over its assessed subtopics only.
            if (section.Subtopics.Count == 0 && section.Key is { } sectionKey && report.BySubtopic.TryGetValue(sectionKey, out var own))
            {
                section.HasData = true;
                section.Progress = own.Progress;
            }
            else
            {
                section.HasData = assessed.Count > 0;
                // A3 (customer 28-08): a subsection with no attempt counts as 0% toward its block — the
                // section % averages over ALL its subtopics (empty ones drag it down), not just the assessed.
                section.Progress = section.Subtopics.Count > 0
                    ? (int)Math.Round(section.Subtopics.Average(s => (double)s.Progress))
                    : 0;

                // Fallback: a graded attempt tagged to this whole section (a subsection whose 2-level key
                // matches no listed subtopic — e.g. «тест по разделу N») still marks the section, so it
                // no longer reads as "not started" once it has been assessed at the section level.
                if (!section.HasData && section.Section > 0 &&
                    report.BySection.TryGetValue(section.Section, out var secStat) && secStat.Answered > 0)
                {
                    section.HasData = true;
                    section.Progress = secStat.Progress;
                }
            }
            section.Status = section.HasData ? BandFor(section.Progress) : SectionStatus.Critical;
        }
    }

    // ── Persistence (localStorage equivalent) ───────────────────────────────

    /// <summary>Loads the adaptive-plan acknowledgement flags — the only state that persists, since
    /// progress is always re-derived from results.</summary>
    private void LoadCompleted()
    {
        try
        {
            if (!File.Exists(AppPaths.LearningScaleFile)) return;
            var dto = JsonSerializer.Deserialize<StateDto>(File.ReadAllText(AppPaths.LearningScaleFile));
            foreach (var id in dto?.CompletedTasks ?? new List<string>())
                _completed.Add(id);
        }
        catch
        {
            // A corrupt file just means no acknowledgement flags restored.
        }
    }

    private void Save()
    {
        try
        {
            AppPaths.EnsureRoot();
            // Only the acknowledgement flags persist — progress is derived from results each launch, so
            // persisting it would masquerade as real mastery if results later disappear.
            var dto = new StateDto { CompletedTasks = _completed.ToList() };
            File.WriteAllText(AppPaths.LearningScaleFile, JsonSerializer.Serialize(dto));
        }
        catch
        {
            // Best-effort persistence; a failed write just means the flags aren't saved this time.
        }
    }

    private sealed class StateDto
    {
        [JsonPropertyName("completedTasks")] public List<string>? CompletedTasks { get; set; }
    }

    // ── Course map (from the loaded course package) ──────────────────────────

    /// <summary>
    /// Builds the section→subtopic map from the loaded <see cref="Course"/>, mirroring how the rest of
    /// the app groups course content (<see cref="CourseTopicFlyout"/>): each group <b>Тема</b> becomes a
    /// section over its <b>Подтемы</b>; standalone content (leaf Темы and lectures filed under no Тема)
    /// is gathered into a trailing section named after the course. Progress starts unfilled and is
    /// overlaid from the <see cref="MasteryReport"/> (see <see cref="ApplyReport"/>); each node's
    /// taxonomy key comes from its authored <c>subsection:</c>. A null course yields an empty map.
    /// </summary>
    private static List<LsSection> BuildCourse(Course? course, Language language)
    {
        var sections = new List<LsSection>();
        if (course is null) return sections;

        var russian = language == Language.RU;
        var ordinal = 0;

        // Order mirrors the Teaching screen's Тема dropdown exactly (CourseTopicFlyout.BuildTopics):
        // every course Тема in authored order — a group Тема over its Подтемы, a leaf Тема as its own
        // (childless) section row kept in place — then the lectures filed under no Тема.
        foreach (var topic in course.Topics)
        {
            var topicName = CourseTopicFlyout.TopicName(topic, russian);
            var subtopics = CourseTopicFlyout.Subtopics(course, topic.Id)
                .Select(l => Subtopic(l.Id, l.Subsection, CourseTopicFlyout.LectureName(l, russian)))
                .Where(s => !CourseThemeCatalog.IsTableOfContents(s.Name, topicName))
                .ToList();
            // A leaf Тема (Course → Тема) is itself content, not a grouping — a section carrying its own
            // mastery, no subtopics. An empty group likewise has nothing to expand.
            sections.Add(Section(++ordinal, topicName, subtopics,
                topic.IsLeaf ? ResolveKey(topic.Subsection, topicName) : null,
                SectionNumberOf(topic.Subsection ?? CourseNumbering.NumberPrefix(topicName), subtopics)));
        }

        var ungrouped = CourseTopicFlyout.UngroupedLectures(course);
        if (ungrouped.Count > 0)
        {
            var subtopics = ungrouped
                .Select(l => Subtopic(l.Id, l.Subsection, CourseTopicFlyout.LectureName(l, russian)))
                .ToList();
            sections.Add(Section(++ordinal, russian ? course.NameRu ?? course.TitleEn : course.TitleEn,
                subtopics, key: null, SectionNumberOf(null, subtopics)));
        }

        return sections;
    }

    /// <summary>The top-level taxonomy section number a Тема maps to: its own <c>subsection:</c> when set,
    /// else the section of its first mapped subtopic, else 0. Only a fallback signal (see
    /// <see cref="ApplyReport"/>), never used to override real per-subtopic aggregation.</summary>
    private static int SectionNumberOf(string? topicSubsection, IEnumerable<LsSubtopic> subtopics)
    {
        var own = SectionOf(topicSubsection);
        if (own > 0) return own;
        foreach (var s in subtopics)
        {
            var n = SectionOf(s.Key);
            if (n > 0) return n;
        }
        return 0;
    }

    private static int SectionOf(string? subsection)
    {
        if (string.IsNullOrWhiteSpace(subsection)) return 0;
        var head = subsection.Split('.')[0].Trim();
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static LsSection Section(int id, string name, IEnumerable<LsSubtopic> subtopics, string? key = null, int section = 0) => new()
    {
        Id = id,
        Name = name,
        Key = key,
        Section = section,
        Progress = 0,
        Status = SectionStatus.Critical,
        HasData = false,
        Subtopics = subtopics.ToList(),
    };

    private static LsSubtopic Subtopic(string id, string? subsection, string name) => new()
    {
        Id = id,
        Key = ResolveKey(subsection, name),
        Name = name,
        Progress = 0,
        HasData = false,
    };

    /// <summary>The taxonomy subtopic key (<c>X.Y</c>) a course node's authored <c>subsection:</c> maps
    /// to, or null when it carries none (so it can't be scored).</summary>
    private static string? Key(string? subsection) =>
        string.IsNullOrWhiteSpace(subsection) ? null : Taxonomy.SubtopicKeyOf(subsection!);

    /// <summary>The subtopic key for a course node, preferring its explicit <c>subsection:</c> and
    /// falling back to the numbering carried in its <paramref name="title"/> (e.g. «2.1. Зубец Р» →
    /// <c>2.1</c>) — so courses that keep numbering in the title text still map onto the Learning Scale.
    /// The test-constructor picker resolves keys the same way (<see cref="Taxonomy.SubtopicKeyOf"/> over
    /// <see cref="CourseNumbering.NumberPrefix"/>), so a tagged answer lands on the matching node.</summary>
    private static string? ResolveKey(string? subsection, string? title) =>
        Key(subsection) ?? Key(CourseNumbering.NumberPrefix(title));
}
