using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Data;

/// <summary>
/// The Theme/Section catalog derived from the loaded course package(s) — the sections («Тема») and
/// sub-topics («Подтема») that actually exist in the courses, localized to the UI language and
/// de-duplicated (case-insensitive, first occurrence wins). These names are exactly what questions
/// store as their <see cref="TestQuestion.Theme"/>, so they drive every theme picker — question-bank
/// authoring, test generation, and the Testing / Examination launchers — instead of a hand-managed
/// catalog. Ordered like the teaching navigation: each section immediately followed by its sub-topics,
/// then any ungrouped sub-topics.
/// </summary>
public static class CourseThemeCatalog
{
    /// <summary>A selectable classification pulled from the course package: a section («Тема») or a
    /// sub-topic («Подтема»). <see cref="Value"/> is the clean name stored on the question;
    /// <see cref="Display"/> indents sub-topics under their parent in a dropdown.</summary>
    public readonly record struct Section(string Value, bool IsSub)
    {
        public string Display => IsSub ? "    ↳ " + Value : Value;
    }

    /// <summary>
    /// The distinct section + sub-topic names across every loaded course, localized to
    /// <paramref name="lang"/>. Empty when no course package is loaded. Reads and parses each course, so
    /// callers that need it repeatedly should cache the result (invalidating on
    /// <see cref="CourseRepository.ManifestChanged"/> and language changes).
    /// </summary>
    public static IReadOnlyList<Section> Sections(CourseRepository courses, Language lang)
    {
        var ru = lang == Language.RU;
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var sections = new List<Section>();

        string? Name(string? nameRu, string titleEn)
        {
            var n = (ru ? (nameRu ?? titleEn) : titleEn)?.Trim();
            return string.IsNullOrEmpty(n) ? null : n;
        }

        void Add(string? name, bool isSub)
        {
            if (name is not null && seen.Add(name)) sections.Add(new Section(name, isSub));
        }

        foreach (var entry in courses.Courses)
        {
            if (courses.ReadCourse(entry.Id) is not { } course) continue;
            var known = new HashSet<string>(course.Topics.Select(t => t.Id), StringComparer.Ordinal);

            // Section (Тема), then its sub-topics (Подтемы), mirroring the teaching navigation order.
            foreach (var topic in course.Topics)
            {
                var section = Name(topic.NameRu, topic.TitleEn);
                Add(section, isSub: false);
                foreach (var lec in course.Lectures.Where(l => l.Topic == topic.Id))
                {
                    var sub = Name(lec.NameRu, lec.TitleEn);
                    if (IsTableOfContents(sub, section)) continue; // overview page — redundant with its section
                    Add(sub, isSub: true);
                }
            }
            // Ungrouped sub-topics (legacy lectures with no / unknown parent topic).
            foreach (var lec in course.Lectures.Where(l => string.IsNullOrEmpty(l.Topic) || !known.Contains(l.Topic!)))
                Add(Name(lec.NameRu, lec.TitleEn), isSub: true);
        }

        return sections;
    }

    /// <summary>
    /// True when a sub-topic looks like its section's "table of contents"/overview page: it has no leading
    /// numeration (real sub-topics are numbered, e.g. <c>4.6.1 …</c>) <em>and</em> its whole name is contained
    /// in the parent section's name. Such pages just repeat the section, so they are dropped — the section
    /// entry alone stands in for them. Sections themselves are never dropped this way.
    /// </summary>
    private static bool IsTableOfContents(string? subName, string? sectionName)
    {
        if (string.IsNullOrEmpty(subName) || string.IsNullOrEmpty(sectionName)) return false;
        if (HasLeadingNumeration(subName)) return false;
        return sectionName.Contains(subName, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>True when a display name starts (after optional whitespace) with a numeration prefix — a
    /// digit, e.g. <c>4</c>, <c>4.6</c>, <c>4.6.1 …</c>.</summary>
    private static bool HasLeadingNumeration(string name)
    {
        foreach (var ch in name)
        {
            if (char.IsWhiteSpace(ch)) continue;
            return char.IsDigit(ch);
        }
        return false;
    }
}
