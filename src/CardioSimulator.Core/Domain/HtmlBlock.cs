using System;
using System.Collections.Generic;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// A discrete structural unit of a lecture body, used by the visual block editor. Port of the
/// Android <c>HtmlBlock</c>. <see cref="Id"/> round-trips to the element's <c>id</c> attribute.
/// </summary>
public abstract record HtmlBlock
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public sealed record Header(int Level, string Text) : HtmlBlock;

    public sealed record Paragraph(string Html) : HtmlBlock;

    public sealed record Image(string Src, string Caption) : HtmlBlock;

    public sealed record KaTeX(string Expression, bool DisplayMode) : HtmlBlock;

    public sealed record Ecg(string Pathology, string? Lead, string Caption) : HtmlBlock;

    public sealed record Table(IReadOnlyList<IReadOnlyList<string>> Rows) : HtmlBlock;
}
