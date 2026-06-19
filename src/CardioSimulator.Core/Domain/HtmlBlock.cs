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

    /// <summary>
    /// An embedded ECG reference. <see cref="Leads"/> lists the leads to display in canonical
    /// order; an empty list means "all 12 leads". <see cref="Scheme"/> controls how they are
    /// laid out (one column / two columns / grid), mirroring the monitor control panel.
    /// </summary>
    public sealed record Ecg(
        string Pathology,
        IReadOnlyList<Lead> Leads,
        SeriesScheme Scheme,
        string Caption) : HtmlBlock;

    public sealed record Table(IReadOnlyList<IReadOnlyList<string>> Rows) : HtmlBlock;
}
