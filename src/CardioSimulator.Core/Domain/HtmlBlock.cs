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

    /// <summary>
    /// An opaque HTML fragment the block editor does not decompose (nested/unknown markup such as a
    /// <c>&lt;div&gt;</c> wrapper with SVG, or a whole pasted standalone document). Its <see cref="Html"/>
    /// round-trips <b>verbatim</b> through <see cref="HtmlCompiler.Compile"/> — no <c>&lt;p&gt;</c>
    /// wrapping — so nested structure survives edits. The visual editor exposes its inner DOM as a
    /// navigable tree (see <c>HtmlStructure</c>) so authors can target and edit nested elements.
    /// </summary>
    public sealed record Raw(string Html) : HtmlBlock;

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

    /// <summary>
    /// A short <b>segment</b> (strip) of a single lead of a pathology — a piece of a real ECG recording,
    /// windowed by <see cref="StartSec"/>/<see cref="DurationSec"/>. Meant to replace a decorative/hand-drawn
    /// ECG sketch with a real snippet from the dataset.
    /// </summary>
    public sealed record EcgSegment(
        string Pathology,
        Lead Lead,
        double StartSec,
        double DurationSec,
        string Caption) : HtmlBlock
    {
        /// <summary>Authored annotation overlays (guide lines, text labels, points) drawn on the strip.
        /// Their <see cref="TipPoint.Sample"/> indices are absolute (relative to the full lead), so they stay
        /// anchored to a waveform feature even if the window moves; the renderer offsets by the window start.</summary>
        public IReadOnlyList<TipOverlay> Tips { get; init; } = Array.Empty<TipOverlay>();
    }

    public sealed record Table(IReadOnlyList<IReadOnlyList<string>> Rows) : HtmlBlock;

    // ── Structural components (the kinds of elements pages are built from) ─────────
    // These compile via HtmlComponents (class "lecture-*") and are recognized back by HtmlCompiler.Parse,
    // so they round-trip as first-class blocks instead of opaque Raw HTML.

    /// <summary>A bulleted or numbered list; each item is an <c>&lt;li&gt;</c>'s inner HTML.</summary>
    public sealed record List(IReadOnlyList<string> Items, bool Numbered) : HtmlBlock;

    /// <summary>A block quote (<see cref="Html"/> is its inner content, which may include a <c>&lt;cite&gt;</c>).</summary>
    public sealed record Quote(string Html) : HtmlBlock;

    /// <summary>A coloured callout box; <see cref="Variant"/> is one of info/tip/warning/important.</summary>
    public sealed record Note(string Variant, string Html) : HtmlBlock;

    /// <summary>An elevated card with an optional <see cref="Title"/> and a body.</summary>
    public sealed record Card(string Title, string Html) : HtmlBlock;

    /// <summary>A titled section (a heading over a body).</summary>
    public sealed record Section(string Title, string Html) : HtmlBlock;

    /// <summary>A captioned figure frame (its body can hold an image/diagram/ECG).</summary>
    public sealed record Figure(string Html, string Caption) : HtmlBlock;

    /// <summary>A horizontal rule / divider (no content).</summary>
    public sealed record Divider : HtmlBlock;

    /// <summary>
    /// A neutral grouping box (no visual styling of its own) whose <see cref="Html"/> body holds nested
    /// content. Like <see cref="Raw"/> it is edited through the visual structure tree, but it is a
    /// first-class component you can add from the palette to start a nested structure from scratch.
    /// </summary>
    public sealed record Container(string Html) : HtmlBlock;
}
