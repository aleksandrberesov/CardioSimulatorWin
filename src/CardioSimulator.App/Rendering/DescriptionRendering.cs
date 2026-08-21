using System.Collections.Generic;
using System.Text.RegularExpressions;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Rendering;

/// <summary>
/// Helpers for showing a pathology <c>Description</c> — which may now be an HTML fragment authored in
/// the constructor — through the shared lecture renderer (<see cref="Controls.LectureWebView"/>), so
/// the description picks up the app's component styling, theme, KaTeX, and <c>&lt;ecg&gt;</c> embeds.
/// </summary>
public static class DescriptionRendering
{
    // A description that carries an element tag is treated as HTML and rendered; one with none is plain
    // prose and stays a lightweight TextBlock. Requires a letter after '<' and a name-terminator after
    // the tag name, so ordinary comparisons ("HR < 60", "QT > 400") don't read as markup.
    private static readonly Regex HtmlTag = new(@"<[a-zA-Z][a-zA-Z0-9]*[\s/>]", RegexOptions.Compiled);

    /// <summary>
    /// Wraps a raw description body as a throwaway <see cref="Lecture"/> so <c>LectureWebView</c> can
    /// render it. The lecture carries no course id (a description's <c>&lt;ecg&gt;</c> embeds reference
    /// pathologies directly, resolved by the repository), so course-asset resolution simply 404s — which
    /// is fine here. <see cref="Lecture.WithReconciledLayout"/> flags a pasted full document as standalone
    /// so it is served verbatim.
    /// </summary>
    public static Lecture AsLecture(string? html)
    {
        var fm = new LectureFrontMatter(string.Empty, 0, string.Empty, 1, new Dictionary<string, string>());
        return new Lecture(string.Empty, string.Empty, "en", fm, html ?? string.Empty).WithReconciledLayout();
    }

    /// <summary>True when <paramref name="text"/> looks like it carries HTML markup, so it should be
    /// rendered rather than shown as plain text.</summary>
    public static bool LooksLikeHtml(string? text) =>
        !string.IsNullOrWhiteSpace(text) && HtmlTag.IsMatch(text);
}
