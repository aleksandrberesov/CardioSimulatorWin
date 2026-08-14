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

/// <summary>Which adaptive-plan bucket a task falls in (drives its colour, label, and badge).</summary>
public enum PlanTaskType { Critical, Growth, Fix }

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
/// State + logic for the Learning Scale («Шкала обучения») dashboard. The course map — which sections
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

    private readonly List<LsSection> _sections;
    private readonly HashSet<string> _completed = new();

    /// <summary>The real mastery rolled up from graded exam attempts, or null in demo-seed mode. When
    /// present and non-empty, subtopic/section progress is derived from it rather than the seed.</summary>
    private readonly MasteryReport? _report;

    /// <summary>True when driven by real results (a non-empty <see cref="_report"/>). In this mode
    /// progress is computed from attempts each launch, "mark as solved" doesn't fabricate mastery, and
    /// stats read from the report.</summary>
    private readonly bool _realData;

    /// <summary>True once the student has solved at least one task this run (drives "updated just now").</summary>
    public bool HasInteracted { get; private set; }

    /// <summary>Demo baseline mirrored from the prototype (average answer time, in seconds).</summary>
    public const int AvgSeconds = 47;

    /// <summary>True when the dashboard is showing real, results-driven mastery (vs. the demo seed).</summary>
    public bool IsRealData => _realData;

    /// <summary>
    /// Creates the dashboard over the <paramref name="course"/> actually loaded in the app (its
    /// Темы/Подтемы). <paramref name="language"/> selects which localized node names to show. Pass the
    /// <see cref="MasteryReport"/> rolled up from exam results to fill in real per-subtopic mastery;
    /// with none (or none mapped) every subtopic simply shows as "not started" (—). Nothing is
    /// fabricated; a null course yields an empty map (the screen shows a "load a course" prompt).
    /// </summary>
    public LearningScaleViewModel(Course? course, Language language = Language.RU, MasteryReport? report = null)
    {
        _sections = BuildCourse(course, language);
        _report = report;
        _realData = report is { HasData: true };

        // Overlay whatever real mastery we have (an empty report marks every subtopic "no data").
        ApplyReport(report ?? MasteryReport.Empty);
        // Only the adaptive-plan acknowledgement flags persist; progress is always derived from results.
        LoadCompleted();
    }

    public IReadOnlyList<LsSection> Sections => _sections;

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
    /// Generates today's adaptive plan: the weakest subtopics bucketed into critical (&lt;30%),
    /// growth (30–60%) and reinforcement (≥70%) bands — 3 / 2 / 2 of each — ordered
    /// critical → growth → fix, with already-solved tasks filtered out. Port of the prototype's
    /// <c>generateTasks</c>.
    /// </summary>
    public IReadOnlyList<PlanTask> GenerateTasks()
    {
        var all = _sections
            .SelectMany(section => section.Subtopics.Select(sub => (section, sub)))
            // Only recommend subtopics that have actually been assessed — never send the student to
            // drill a topic we have no measurement for (so a fresh install proposes nothing).
            .Where(x => x.sub.HasData)
            .OrderBy(x => x.sub.Progress)
            .ToList();

        var critical = all.Where(x => x.sub.Progress < 30).Take(3)
            .Select(x => Make(x.section, x.sub, PlanTaskType.Critical, "c"));
        var growth = all.Where(x => x.sub.Progress is >= 30 and < 60).Take(2)
            .Select(x => Make(x.section, x.sub, PlanTaskType.Growth, "g"));
        var fix = all.Where(x => x.sub.Progress >= 70).Take(2)
            .Select(x => Make(x.section, x.sub, PlanTaskType.Fix, "f"));

        return critical.Concat(growth).Concat(fix)
            .Where(t => !_completed.Contains(t.Id))
            .ToList();

        static PlanTask Make(LsSection section, LsSubtopic sub, PlanTaskType type, string prefix) =>
            new($"{prefix}-{section.Id}-{sub.Id}", section.Id, section.Name, sub.Key ?? sub.Id, sub.Name, type, sub.Progress);
    }

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

    /// <summary>The mastery band used when a section's progress is recomputed (prototype thresholds).</summary>
    private static SectionStatus BandFor(int progress) =>
        progress >= 80 ? SectionStatus.Good : progress >= 50 ? SectionStatus.Warning : SectionStatus.Critical;

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
                section.Progress = assessed.Count > 0 ? (int)Math.Round(assessed.Average()) : 0;
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
            var subtopics = CourseTopicFlyout.Subtopics(course, topic.Id)
                .Select(l => Subtopic(l.Id, l.Subsection, CourseTopicFlyout.LectureName(l, russian)));
            // A leaf Тема (Course → Тема) is itself content, not a grouping — a section carrying its own
            // mastery, no subtopics. An empty group likewise has nothing to expand.
            sections.Add(Section(++ordinal, CourseTopicFlyout.TopicName(topic, russian), subtopics,
                topic.IsLeaf ? Key(topic.Subsection) : null));
        }

        var ungrouped = CourseTopicFlyout.UngroupedLectures(course);
        if (ungrouped.Count > 0)
            sections.Add(Section(++ordinal, russian ? course.NameRu ?? course.TitleEn : course.TitleEn,
                ungrouped.Select(l => Subtopic(l.Id, l.Subsection, CourseTopicFlyout.LectureName(l, russian)))));

        return sections;
    }

    private static LsSection Section(int id, string name, IEnumerable<LsSubtopic> subtopics, string? key = null) => new()
    {
        Id = id,
        Name = name,
        Key = key,
        Progress = 0,
        Status = SectionStatus.Critical,
        HasData = false,
        Subtopics = subtopics.ToList(),
    };

    private static LsSubtopic Subtopic(string id, string? subsection, string name) => new()
    {
        Id = id,
        Key = Key(subsection),
        Name = name,
        Progress = 0,
        HasData = false,
    };

    /// <summary>The taxonomy subtopic key (<c>X.Y</c>) a course node's authored <c>subsection:</c> maps
    /// to, or null when it carries none (so it can't be scored).</summary>
    private static string? Key(string? subsection) =>
        string.IsNullOrWhiteSpace(subsection) ? null : Taxonomy.SubtopicKeyOf(subsection!);
}
