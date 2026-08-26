using System.Text.RegularExpressions;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Recovers a course node's numbering from its <em>title</em> when the pack carries no explicit
/// <c>subsection:</c> field. Many authored courses keep the numbering in the display text only —
/// e.g. a Тема titled «Раздел 1. …» or a Подтема titled «2.1. Зубец Р» — so the Learning Scale and the
/// test-constructor picker fall back to this to still map such nodes onto the taxonomy
/// (<see cref="Taxonomy.SubtopicKeyOf"/>). Purely lexical, so it is unit-testable and shared verbatim by
/// every caller (map building and the picker) — they must agree on the derived key or a graded answer
/// wouldn't land on the node it was tagged to.
/// </summary>
public static class CourseNumbering
{
    // Optional leading section word («Раздел»/«Тема»/Section/…), then a dotted number that must be
    // terminated by a dot or the end of the token — so a real numeration («2.1.», «Раздел 1.») matches
    // but an incidental leading number in prose («12 отведений …») does not.
    private static readonly Regex Prefix = new(
        @"^\s*(?:(?:раздел|тема|часть|section|part|chapter)\s+)?(\d+(?:\.\d+)*)(?=\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The leading dotted-number of a course node title, or null when it carries none:
    /// «2.1. Зубец Р» → <c>2.1</c>, «Раздел 1. Теоретические основы…» → <c>1</c>,
    /// «6.13. ЧПЭС» → <c>6.13</c>, «Нарушения ритма сердца.» → null. The result is a subsection-style
    /// string; run it through <see cref="Taxonomy.SubtopicKeyOf"/> to get the 2-level Learning-Scale key.
    /// </summary>
    public static string? NumberPrefix(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var m = Prefix.Match(title);
        return m.Success ? m.Groups[1].Value : null;
    }
}
