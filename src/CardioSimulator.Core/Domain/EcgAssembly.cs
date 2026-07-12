using System;
using System.Collections.Generic;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// One contiguous slice of an ECG trace for the «Собери ЭКГ» question type — a short run of
/// <em>baseline-zeroed</em> samples (0 = isoline). Parts are cut from a single rhythm's lead, so placing
/// them back in their original order reconstructs one continuous strip.
/// </summary>
public sealed record EcgAssemblyPart(IReadOnlyList<int> Samples)
{
    /// <summary>Samples (never null).</summary>
    public IReadOnlyList<int> SampleList => Samples ?? Array.Empty<int>();
}

/// <summary>
/// The «Собери ЭКГ» (assemble-the-ECG) specification attached to a <see cref="TestQuestion"/>. A single
/// rhythm's trace is cut into <see cref="Parts"/> contiguous pieces stored in their <em>correct</em>
/// (original) order; the runtime shuffles them and the student drags them back into order to rebuild the
/// continuous strip. Deliberately simple — no P/QRS/T detection — so it works for <em>any</em> rhythm,
/// including ones whose wave boundaries can't be detected. Snapshot semantics: the parts are stored on
/// the question, so the Testing runtime needs no signal processing and the source rhythm isn't live-tracked.
/// </summary>
public sealed record EcgAssembly(
    int SampleRateHz,
    IReadOnlyList<EcgAssemblyPart> Parts,
    string? SourcePathologyId = null,
    Lead SliceLead = Lead.II)
{
    /// <summary>The parts in correct order (never null).</summary>
    public IReadOnlyList<EcgAssemblyPart> PartList => Parts ?? Array.Empty<EcgAssemblyPart>();

    /// <summary>How many parts the trace was cut into.</summary>
    public int PartCount => PartList.Count;

    /// <summary>True when there are at least two parts to reorder.</summary>
    public bool IsComplete => PartCount >= 2;
}
