using System;
using System.Collections.Generic;
using System.Linq;

namespace CardioSimulator.Core.Domain;

/// <summary>Answered/correct tally for one bucket (a subtopic, a section, or a group).</summary>
public readonly record struct MasteryStat(int Answered, int Correct)
{
    /// <summary>Mastery as a 0–100 percent (0 when nothing has been answered in this bucket).</summary>
    public int Progress => Answered == 0 ? 0 : (int)Math.Round(100.0 * Correct / Answered);

    public MasteryStat Add(bool isCorrect) => new(Answered + 1, Correct + (isCorrect ? 1 : 0));
}

/// <summary>
/// The result of rolling graded exam attempts up through the <see cref="Taxonomy"/>: how the student
/// is doing per course subtopic (<c>X.Y</c>), per top-level section, and per rhythm group. This is what
/// feeds the Learning Scale dashboard with <em>real</em> mastery instead of demo numbers.
/// </summary>
public sealed record MasteryReport(
    IReadOnlyDictionary<string, MasteryStat> BySubtopic,
    IReadOnlyDictionary<int, MasteryStat> BySection,
    IReadOnlyDictionary<string, MasteryStat> ByGroup,
    int TotalAnswered,
    int TotalCorrect)
{
    /// <summary>True once at least one graded, taxonomy-tagged question has been answered — the signal
    /// the dashboard uses to switch from the demo seed to real data.</summary>
    public bool HasData => TotalAnswered > 0;

    /// <summary>Mastery for a subtopic key (<c>X.Y</c>), or an empty tally when it has no attempts.</summary>
    public MasteryStat Subtopic(string subtopicKey) =>
        BySubtopic.TryGetValue(subtopicKey, out var s) ? s : default;

    /// <summary>Mastery for a top-level section, or an empty tally when it has no attempts.</summary>
    public MasteryStat Section(int section) =>
        BySection.TryGetValue(section, out var s) ? s : default;

    public static readonly MasteryReport Empty = new(
        new Dictionary<string, MasteryStat>(),
        new Dictionary<int, MasteryStat>(),
        new Dictionary<string, MasteryStat>(),
        0, 0);
}

/// <summary>
/// Rolls graded <see cref="ExamResult"/>s up into <see cref="MasteryReport"/> using the taxonomy.
/// Pure (no IO) so it is fully unit-testable. Each answered question is attributed — once per distinct
/// bucket, so a question carrying two acronyms in the same subtopic is not double-counted — to the
/// subtopic / section / group of every taxonomy acronym it was tagged with. Questions with no
/// recognized acronym are ignored (they cannot be placed on the course map).
/// </summary>
public static class MasteryRollup
{
    public static MasteryReport Compute(IEnumerable<ExamResult> results, Taxonomy taxonomy)
    {
        var bySubtopic = new Dictionary<string, MasteryStat>();
        var bySection = new Dictionary<int, MasteryStat>();
        var byGroup = new Dictionary<string, MasteryStat>(StringComparer.OrdinalIgnoreCase);
        var totalAnswered = 0;
        var totalCorrect = 0;

        foreach (var result in results)
        {
            foreach (var q in result.Questions)
            {
                if (q.AcronymList.Count == 0) continue;

                // Resolve this question's distinct buckets so multiple acronyms landing in the same
                // subtopic/section/group count the answer only once there.
                var subtopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sections = new HashSet<int>();
                var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var acronym in q.AcronymList)
                {
                    if (taxonomy.Find(acronym) is not { } e) continue;
                    subtopics.Add(e.SubtopicKey);
                    sections.Add(e.Section);
                    if (!string.IsNullOrEmpty(e.Group)) groups.Add(e.Group);
                }
                if (subtopics.Count == 0) continue; // no recognized acronym

                foreach (var key in subtopics) bySubtopic[key] = Get(bySubtopic, key).Add(q.IsCorrect);
                foreach (var sec in sections) bySection[sec] = Get(bySection, sec).Add(q.IsCorrect);
                foreach (var g in groups) byGroup[g] = Get(byGroup, g).Add(q.IsCorrect);

                totalAnswered++;
                if (q.IsCorrect) totalCorrect++;
            }
        }

        return new MasteryReport(bySubtopic, bySection, byGroup, totalAnswered, totalCorrect);
    }

    private static MasteryStat Get<TKey>(Dictionary<TKey, MasteryStat> map, TKey key)
        where TKey : notnull => map.TryGetValue(key, out var s) ? s : default;
}
