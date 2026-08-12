using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CardioSimulator.App.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.ViewModels;

/// <summary>Mastery band of a course section, driving its badge/dot colour.</summary>
public enum SectionStatus { Good, Warning, Critical }

/// <summary>Which adaptive-plan bucket a task falls in (drives its colour, label, and badge).</summary>
public enum PlanTaskType { Critical, Growth, Fix }

/// <summary>One subtopic of a course section, with its 0–100 mastery.</summary>
public sealed class LsSubtopic
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int Progress { get; set; }

    /// <summary>False when running on real results and this subtopic has no graded attempts yet — the
    /// dashboard shows it as "not started" (—) rather than a misleading 0%/critical. Always true in the
    /// demo-seed mode.</summary>
    public bool HasData { get; set; } = true;
}

/// <summary>One top-level course section, its aggregate mastery, band, and subtopics.</summary>
public sealed class LsSection
{
    public required int Id { get; init; }
    public required string Name { get; init; }
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
/// State + logic for the Learning Scale («Шкала обучения») dashboard — a faithful C# port of the
/// prototype's course model, adaptive-task generation (Leitner-style buckets), mark-as-solved
/// progression, aggregate stats, and <c>localStorage</c>-equivalent persistence
/// (<see cref="AppPaths.LearningScaleFile"/>). The screen re-renders on <see cref="StateChanged"/>.
/// Section/subtopic names are the course content itself and stay in the source language (Russian);
/// only the surrounding UI chrome is localized.
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
    /// Creates the dashboard. Pass the <see cref="MasteryReport"/> rolled up from real exam results to
    /// drive it from actual student performance; pass null (or an empty report) to fall back to the
    /// demo seed so a fresh install still has a populated dashboard.
    /// </summary>
    public LearningScaleViewModel(MasteryReport? report = null)
    {
        _sections = SeedCourse();
        _report = report;
        _realData = report is { HasData: true };

        if (_realData)
        {
            ApplyReport(report!);
            LoadCompleted(); // adaptive-plan "solved" flags still persist; progress does not
        }
        else
        {
            Load();
        }
    }

    public IReadOnlyList<LsSection> Sections => _sections;

    // ── Aggregate stats ─────────────────────────────────────────────────────

    private double AverageProgress =>
        _sections.Count == 0 ? 0 : _sections.Average(s => s.Progress);

    /// <summary>Overall course progress: real answer accuracy when driven by results, else the demo
    /// average of section progress. Rounded to a whole percent.</summary>
    public int GlobalProgressPercent => _realData && _report!.TotalAnswered > 0
        ? (int)Math.Round(100.0 * _report.TotalCorrect / _report.TotalAnswered)
        : (int)Math.Round(AverageProgress);

    /// <summary>Number of graded questions answered (real mode) or the demo baseline + solved tasks.</summary>
    public int CasesCount => _realData ? _report!.TotalAnswered : 184 + _completed.Count;

    /// <summary>Accuracy readout (invariant "78.4"-style, dot decimal). Real answer accuracy when
    /// driven by results, else the prototype's derived demo value.</summary>
    public string AccuracyDisplay => _realData && _report!.TotalAnswered > 0
        ? (100.0 * _report.TotalCorrect / _report.TotalAnswered).ToString("F1", CultureInfo.InvariantCulture)
        : (78.4 + (AverageProgress - 50) * 0.2).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>Trend chip next to the accuracy. Suppressed in real mode (no fabricated delta).</summary>
    public string AccuracyChange =>
        _realData ? string.Empty
        : _completed.Count > 0
            ? "▲" + (2.1 + _completed.Count * 0.1).ToString("F1", CultureInfo.InvariantCulture) + "%"
            : "▲2.1%";

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
            // In real-data mode only recommend subtopics that have actually been assessed — never send
            // the student to drill a topic we have no measurement for.
            .Where(x => !_realData || x.sub.HasData)
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
            new($"{prefix}-{section.Id}-{sub.Id}", section.Id, section.Name, sub.Id, sub.Name, type, sub.Progress);
    }

    public bool IsCompleted(string taskId) => _completed.Contains(taskId);

    /// <summary>
    /// Marks a task solved: bumps its subtopic (+8, capped at 100), recomputes the section's
    /// aggregate + band, persists, and notifies. Returns the section's new progress (for the toast),
    /// or null if the task was unknown / already solved.
    /// </summary>
    public int? MarkDone(string taskId)
    {
        if (_completed.Contains(taskId)) return null;
        var task = GenerateTasks().FirstOrDefault(t => t.Id == taskId);
        if (task is null) return null;

        _completed.Add(taskId);
        HasInteracted = true;

        var section = _sections.FirstOrDefault(s => s.Id == task.SectionId);
        if (section is null) { Save(); StateChanged?.Invoke(); return null; }

        // Real mastery only moves when the student re-takes a graded test — acknowledging a plan task
        // must not fabricate progress. In demo-seed mode keep the prototype's +8 gamification.
        if (!_realData)
        {
            var sub = section.Subtopics.FirstOrDefault(s => s.Id == task.SubtopicId);
            if (sub is not null) sub.Progress = Math.Min(100, sub.Progress + 8);

            section.Progress = (int)Math.Round(section.Subtopics.Average(s => s.Progress));
            section.Status = BandFor(section.Progress);
        }

        Save();
        StateChanged?.Invoke();
        return section.Progress;
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
                var has = report.BySubtopic.TryGetValue(sub.Id, out var stat);
                sub.HasData = has;
                sub.Progress = has ? stat.Progress : 0;
                if (has) assessed.Add(stat.Progress);
            }
            section.HasData = assessed.Count > 0;
            section.Progress = assessed.Count > 0 ? (int)Math.Round(assessed.Average()) : 0;
            section.Status = section.HasData ? BandFor(section.Progress) : SectionStatus.Critical;
        }
    }

    // ── Persistence (localStorage equivalent) ───────────────────────────────

    /// <summary>Loads only the adaptive-plan "solved" flags (used in real-data mode, where progress is
    /// derived from results rather than restored from disk).</summary>
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
            // A corrupt file just means no solved-flags restored.
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.LearningScaleFile)) return;
            var dto = JsonSerializer.Deserialize<StateDto>(File.ReadAllText(AppPaths.LearningScaleFile));
            if (dto is null) return;

            foreach (var s in dto.Sections ?? new List<SectionDto>())
            {
                var section = _sections.FirstOrDefault(x => x.Id == s.Id);
                if (section is null) continue;
                section.Progress = s.Progress;
                section.Status = Enum.TryParse<SectionStatus>(s.Status, ignoreCase: true, out var band) ? band : section.Status;
                foreach (var sub in s.Subtopics ?? new List<SubtopicDto>())
                {
                    var target = section.Subtopics.FirstOrDefault(x => x.Id == sub.Id);
                    if (target is not null) target.Progress = sub.Progress;
                }
            }

            foreach (var id in dto.CompletedTasks ?? new List<string>())
                _completed.Add(id);
        }
        catch
        {
            // A corrupt/partial file must not break the screen — fall back to the seeded course.
        }
    }

    private void Save()
    {
        try
        {
            AppPaths.EnsureRoot();
            var dto = new StateDto
            {
                // In real-data mode progress is derived from results each launch — don't persist it (it
                // would masquerade as demo progress if results later disappear). Only the plan flags.
                Sections = _realData ? null : _sections.Select(s => new SectionDto
                {
                    Id = s.Id,
                    Progress = s.Progress,
                    Status = s.Status.ToString().ToLowerInvariant(),
                    Subtopics = s.Subtopics.Select(sub => new SubtopicDto { Id = sub.Id, Progress = sub.Progress }).ToList(),
                }).ToList(),
                CompletedTasks = _completed.ToList(),
            };
            File.WriteAllText(AppPaths.LearningScaleFile, JsonSerializer.Serialize(dto));
        }
        catch
        {
            // Best-effort persistence; a failed write just means progress isn't saved this time.
        }
    }

    private sealed class StateDto
    {
        [JsonPropertyName("sections")] public List<SectionDto>? Sections { get; set; }
        [JsonPropertyName("completedTasks")] public List<string>? CompletedTasks { get; set; }
    }

    private sealed class SectionDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("progress")] public int Progress { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("subtopics")] public List<SubtopicDto>? Subtopics { get; set; }
    }

    private sealed class SubtopicDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("progress")] public int Progress { get; set; }
    }

    // ── Seed (the ЭКГ course structure, from the prototype) ──────────────────

    private static List<LsSection> SeedCourse() => new()
    {
        new LsSection { Id = 1, Name = "Теоретические основы и техника регистрации ЭКГ", Progress = 92, Status = SectionStatus.Good, Subtopics = new()
        {
            new() { Id = "1.1", Name = "Мембранная теория биопотенциалов", Progress = 95 },
            new() { Id = "1.2", Name = "Основные функции сердца", Progress = 90 },
            new() { Id = "1.3", Name = "Формирование нормальной ЭКГ", Progress = 88 },
            new() { Id = "1.4", Name = "ЭКГ-аппаратура", Progress = 92 },
            new() { Id = "1.5", Name = "ЭКГ-отведения", Progress = 90 },
            new() { Id = "1.6", Name = "Техника регистрации ЭКГ", Progress = 94 },
            new() { Id = "1.7", Name = "Функциональные пробы", Progress = 85 },
            new() { Id = "1.8", Name = "Дополнительные методы", Progress = 80 },
        } },
        new LsSection { Id = 2, Name = "Анализ нормальной ЭКГ", Progress = 85, Status = SectionStatus.Good, Subtopics = new()
        {
            new() { Id = "2.1", Name = "Зубец P", Progress = 88 },
            new() { Id = "2.2", Name = "Интервал P–Q(R)", Progress = 85 },
            new() { Id = "2.3", Name = "Желудочковый комплекс QRST", Progress = 82 },
            new() { Id = "2.4", Name = "Анализ ритма и проводимости", Progress = 80 },
            new() { Id = "2.5", Name = "Повороты сердца вокруг осей", Progress = 78 },
            new() { Id = "2.6", Name = "Анализ предсердного зубца P", Progress = 90 },
            new() { Id = "2.7", Name = "Анализ желудочкового комплекса", Progress = 85 },
            new() { Id = "2.8", Name = "ЭКГ-заключение", Progress = 82 },
        } },
        new LsSection { Id = 3, Name = "Нарушения ритма сердца", Progress = 45, Status = SectionStatus.Warning, Subtopics = new()
        {
            new() { Id = "3.1", Name = "Нарушения автоматизма СА-узла", Progress = 55 },
            new() { Id = "3.2", Name = "Эктопические (гетеротопные) ритмы", Progress = 45 },
            new() { Id = "3.3", Name = "Экстрасистолия", Progress = 40 },
            new() { Id = "3.4", Name = "Пароксизмальная тахикардия", Progress = 35 },
            new() { Id = "3.5", Name = "Трепетание предсердий", Progress = 30 },
            new() { Id = "3.6", Name = "Фибрилляция предсердий", Progress = 25 },
            new() { Id = "3.7", Name = "Трепетание и фибрилляция желудочков", Progress = 20 },
            new() { Id = "3.8", Name = "Холтеровское мониторирование", Progress = 50 },
        } },
        new LsSection { Id = 4, Name = "Нарушения функции проводимости", Progress = 30, Status = SectionStatus.Critical, Subtopics = new()
        {
            new() { Id = "4.1", Name = "Синдром слабости СА-узла", Progress = 35 },
            new() { Id = "4.2", Name = "Синоатриальная блокада", Progress = 30 },
            new() { Id = "4.3", Name = "Остановка СА-узла", Progress = 25 },
            new() { Id = "4.4", Name = "Синдром брадикардии-тахикардии", Progress = 28 },
            new() { Id = "4.5", Name = "Межпредсердная блокада", Progress = 20 },
            new() { Id = "4.6", Name = "АВ-блокады (I, II, III степени)", Progress = 25 },
            new() { Id = "4.7", Name = "Синдром Морганьи–Адамса–Стокса", Progress = 30 },
            new() { Id = "4.8", Name = "Синдром Фредерика", Progress = 20 },
            new() { Id = "4.9", Name = "Электрограмма пучка Гиса", Progress = 15 },
            new() { Id = "4.10", Name = "Блокады ножек пучка Гиса", Progress = 25 },
            new() { Id = "4.11", Name = "Синдромы преждевременного возбуждения", Progress = 20 },
        } },
        new LsSection { Id = 5, Name = "Гипертрофия предсердий и желудочков", Progress = 65, Status = SectionStatus.Warning, Subtopics = new()
        {
            new() { Id = "5.1", Name = "Гипертрофия левого предсердия", Progress = 70 },
            new() { Id = "5.2", Name = "Гипертрофия правого предсердия", Progress = 65 },
            new() { Id = "5.3", Name = "Перегрузка предсердий", Progress = 60 },
            new() { Id = "5.4", Name = "Гипертрофия левого желудочка", Progress = 68 },
            new() { Id = "5.5", Name = "Гипертрофия правого желудочка", Progress = 55 },
            new() { Id = "5.6", Name = "Комбинированная гипертрофия", Progress = 50 },
            new() { Id = "5.7", Name = "Перегрузка желудочков", Progress = 60 },
        } },
        new LsSection { Id = 6, Name = "Ишемическая болезнь сердца и инфаркт", Progress = 55, Status = SectionStatus.Warning, Subtopics = new()
        {
            new() { Id = "6.1", Name = "Общие сведения об ИБС", Progress = 65 },
            new() { Id = "6.2", Name = "ЭКГ при ишемии и повреждении", Progress = 55 },
            new() { Id = "6.3", Name = "ЭКГ при остром ИМ с ST↑", Progress = 50 },
            new() { Id = "6.4", Name = "Локализация ИМ (передняя/задняя)", Progress = 45 },
            new() { Id = "6.5", Name = "Аневризма сердца", Progress = 40 },
            new() { Id = "6.6", Name = "ИМ без подъема ST", Progress = 45 },
            new() { Id = "6.7", Name = "Нестабильная стенокардия", Progress = 55 },
            new() { Id = "6.8", Name = "Стабильная стенокардия", Progress = 60 },
            new() { Id = "6.9", Name = "Хроническая ИБС", Progress = 50 },
        } },
        new LsSection { Id = 7, Name = "ЭКГ при заболеваниях сердца и синдромах", Progress = 20, Status = SectionStatus.Critical, Subtopics = new()
        {
            new() { Id = "7.1", Name = "Приобретенные пороки сердца", Progress = 25 },
            new() { Id = "7.2", Name = "Острое легочное сердце", Progress = 20 },
            new() { Id = "7.3", Name = "Перикардиты", Progress = 18 },
            new() { Id = "7.4", Name = "Миокардиты", Progress = 15 },
            new() { Id = "7.5", Name = "Нарушения электролитного обмена", Progress = 20 },
            new() { Id = "7.6", Name = "Передозировка гликозидов", Progress = 10 },
            new() { Id = "7.7", Name = "Имплантированный ЭКС", Progress = 15 },
        } },
    };
}
