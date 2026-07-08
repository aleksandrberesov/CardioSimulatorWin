using System;
using System.Collections.Generic;
using System.Linq;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// The three building blocks a beat is split into for the «Собери ЭКГ» (assemble-the-ECG) question
/// type. The learner drags one candidate piece into each block's slot, left→right, to reconstruct the
/// complex called for by the assignment. Declaration order is the canonical strip order (P, then QRS,
/// then T).
/// </summary>
public enum EcgBlock
{
    P,
    QRS,
    T,
}

/// <summary>
/// One draggable waveform fragment: a short run of <em>baseline-zeroed</em> samples (0 = isoline) for a
/// single <see cref="EcgBlock"/>, sliced out of a real rhythm at authoring time. Stored samples are the
/// render source of truth so the runtime needs no signal processing. <see cref="SourceId"/> records the
/// pathology it was cut from (for re-editing / debugging), never used for grading.
/// </summary>
public sealed record EcgBlockPiece(EcgBlock Block, IReadOnlyList<int> Samples, string? SourceId = null)
{
    /// <summary>Samples (never null).</summary>
    public IReadOnlyList<int> SampleList => Samples ?? Array.Empty<int>();
}

/// <summary>
/// One slot of an <see cref="EcgAssembly"/>: the single <see cref="Correct"/> piece (sliced from the
/// target rhythm) plus the author-selected <see cref="Distractors"/> (sliced from other rhythms). The
/// runtime shuffles <see cref="AllPieces"/> into the block's palette; the learner must drag the correct
/// one into this slot.
/// </summary>
public sealed record EcgAssemblyBlock(
    EcgBlock Block,
    EcgBlockPiece Correct,
    IReadOnlyList<EcgBlockPiece> Distractors)
{
    /// <summary>Distractors (never null).</summary>
    public IReadOnlyList<EcgBlockPiece> DistractorList => Distractors ?? Array.Empty<EcgBlockPiece>();

    /// <summary>The correct piece first, then the distractors — the palette pool before shuffling.</summary>
    public IReadOnlyList<EcgBlockPiece> AllPieces =>
        new[] { Correct }.Concat(DistractorList).ToList();
}

/// <summary>
/// The full «Собери ЭКГ» specification attached to a <see cref="TestQuestion"/>. Holds the three
/// <see cref="Blocks"/> (P, QRS, T) with their correct + distractor pieces, plus the authoring
/// provenance (<see cref="TargetPathologyId"/> / <see cref="DistractorPathologyIds"/> /
/// <see cref="SliceLead"/>) so the Test Constructor can rebuild it after the source rhythms change.
/// Snapshot semantics — like bank questions, an assembly does not live-track its source rhythms.
/// </summary>
public sealed record EcgAssembly(
    int SampleRateHz,
    IReadOnlyList<EcgAssemblyBlock> Blocks,
    string? TargetPathologyId = null,
    IReadOnlyList<string>? DistractorPathologyIds = null,
    Lead SliceLead = Lead.II)
{
    /// <summary>Blocks (never null).</summary>
    public IReadOnlyList<EcgAssemblyBlock> BlockList => Blocks ?? Array.Empty<EcgAssemblyBlock>();

    /// <summary>The distractor rhythm ids (never null).</summary>
    public IReadOnlyList<string> DistractorIds => DistractorPathologyIds ?? Array.Empty<string>();

    /// <summary>The block for a given wave, or null if the assembly is missing it.</summary>
    public EcgAssemblyBlock? Of(EcgBlock block) => BlockList.FirstOrDefault(b => b.Block == block);

    /// <summary>True when all three waves (P, QRS, T) are present and each has at least one piece.</summary>
    public bool IsComplete =>
        Of(EcgBlock.P) is { } p && p.AllPieces.Count > 0 &&
        Of(EcgBlock.QRS) is { } q && q.AllPieces.Count > 0 &&
        Of(EcgBlock.T) is { } t && t.AllPieces.Count > 0;
}
