namespace CardioSimulator.Core.Domain;

/// <summary>
/// The kinds of annotation overlay ("tip") an instructor authors onto an ECG in the Constructor.
/// Mirrors the customer's "Типы подсказок на ЭКГ" list: an arrow with a caption, a whole-lead
/// highlight, a rectangular or freeform area on the graph, a slice of one ECG segment,
/// vertical/horizontal guide lines, a free-standing label, and a set of points.
/// </summary>
public enum TipOverlayKind
{
    Arrow,
    LeadArea,
    GraphArea,      // rectangular area on the graph (Прямоугольная область)
    FreeformArea,   // freeform / curved area on the graph (Кривая область)
    EcgPart,
    VerticalLines,
    HorizontalLines,
    Label,
    Points,
}

/// <summary>End-cap style for the guide-line tip kinds: a plain line, dots on the ends, or arrows.</summary>
public enum TipLineEndCap
{
    Plain,
    Dots,
    Arrows,
}

/// <summary>
/// A point in ECG data space: <see cref="Sample"/> is a (fractional) sample index along the trace,
/// <see cref="Adc"/> is a <em>baseline-relative</em> ADC amplitude (0 = isoline, positive = up) — the
/// same zeroing the rendered trace uses, so it maps identically on the editable lead and the monitor
/// grid. Storing overlays in data space keeps them anchored to the waveform as speed / gain / zoom change.
/// </summary>
public readonly record struct TipPoint(float Sample, float Adc);

/// <summary>
/// An authored annotation overlay on a pathology. Its geometry (<see cref="Points"/>) is in ECG data
/// space (see <see cref="TipPoint"/>). Persisted via the <c>tips:</c> header field, alongside
/// <c>markers:</c>. <see cref="Text"/> is the caption for arrows/labels; <see cref="Lead"/> is the
/// highlighted lead for <see cref="TipOverlayKind.LeadArea"/>; <see cref="EndCap"/> applies to the
/// guide-line kinds.
/// </summary>
public sealed record TipOverlay(
    TipOverlayKind Kind,
    IReadOnlyList<TipPoint> Points,
    string? Text = null,
    Lead? Lead = null,
    TipLineEndCap EndCap = TipLineEndCap.Plain);
