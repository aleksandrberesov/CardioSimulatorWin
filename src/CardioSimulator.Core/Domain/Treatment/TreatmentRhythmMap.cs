using System;
using System.Collections.Generic;

namespace CardioSimulator.Core.Domain.Treatment;

/// <summary>
/// Maps an abstract <see cref="ClinicalRhythmState"/> to the taxonomy acronyms that best represent it in the
/// dataset, in preference order — the host resolves the first acronym that has a concrete rhythm
/// (<c>Taxonomy.ResolvePathologyIdsForAcronyms</c>) and displays it. Pure/data-only so it is unit-testable.
///
/// <para><b>Coverage vs the shipped pak (verified 28-08-2026):</b> Sinus/SinusTachycardia/AFib/SVT/VT/VFib/
/// CompleteAvBlock/Paced all resolve to real rhythms. <see cref="ClinicalRhythmState.Asystole"/> has no
/// acronym and no waveform in the pak — the host renders a synthesized isoelectric flat line for it (empty
/// acronym list, <see cref="IsSynthesizedFlatline"/> true). <see cref="ClinicalRhythmState.Torsades"/> has no
/// dedicated <c>TDP</c> tag yet, so it falls back to <c>PVT</c> (a VT) as an approximation; author a real TDP
/// rhythm + acronym and re-tag the pak to make it exact.</para>
/// </summary>
public static class TreatmentRhythmMap
{
    private static readonly IReadOnlyDictionary<ClinicalRhythmState, string[]> Map =
        new Dictionary<ClinicalRhythmState, string[]>
        {
            [ClinicalRhythmState.Sinus] = new[] { "SR" },
            [ClinicalRhythmState.SinusTachycardia] = new[] { "ST" },
            [ClinicalRhythmState.AtrialFibrillation] = new[] { "AFIB" },
            // Rate-controlled AFib is still AFib on the map until a slower-rate variant is curated.
            [ClinicalRhythmState.AtrialFibrillationRateControlled] = new[] { "AFIB" },
            [ClinicalRhythmState.Svt] = new[] { "SVT", "AVNRT", "AVRT" },
            [ClinicalRhythmState.VentricularTachycardia] = new[] { "PVT" },
            [ClinicalRhythmState.PulselessVt] = new[] { "PVT" },
            [ClinicalRhythmState.VentricularFibrillation] = new[] { "VFIB" },
            // No dedicated Torsades tag in the pak yet — approximate with a monomorphic VT.
            [ClinicalRhythmState.Torsades] = new[] { "TDP", "PVT" },
            // No asystole waveform/acronym in the pak — the host synthesizes a flat line.
            [ClinicalRhythmState.Asystole] = Array.Empty<string>(),
            [ClinicalRhythmState.CompleteAvBlock] = new[] { "3AVB" },
            [ClinicalRhythmState.Paced] = new[] { "APACE" },
        };

    /// <summary>The acronyms to try for <paramref name="state"/>, in preference order (empty for a state the
    /// host must synthesize, e.g. asystole).</summary>
    public static IReadOnlyList<string> AcronymsFor(ClinicalRhythmState state) =>
        Map.TryGetValue(state, out var acr) ? acr : Array.Empty<string>();

    /// <summary>
    /// Inverse of <see cref="Map"/>: classifies a real rhythm (by its taxonomy acronyms) into the ACLS
    /// category the engine reasons over, so the treatment panel can seed its state from the currently-displayed
    /// rhythm instead of an abstract picker. Returns null when the rhythm is not a treatable/arrest rhythm the
    /// engine has rules for (most diagnostic ECGs) — the caller treats that as "no applicable category".
    /// Matching is case-insensitive; the more specific arrest/shockable codes are tested first.
    /// </summary>
    public static ClinicalRhythmState? ClassifyByAcronyms(IEnumerable<string> acronyms)
    {
        if (acronyms is null) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in acronyms)
            if (!string.IsNullOrWhiteSpace(a)) set.Add(a.Trim());
        if (set.Count == 0) return null;

        // Order matters: check specific/dangerous categories before generic ones.
        if (set.Contains("VFIB") || set.Contains("VFL")) return ClinicalRhythmState.VentricularFibrillation;
        if (set.Contains("TDP")) return ClinicalRhythmState.Torsades;
        if (set.Contains("PVT")) return ClinicalRhythmState.VentricularTachycardia; // pak has no pulsed/pulseless split
        if (set.Contains("3AVB")) return ClinicalRhythmState.CompleteAvBlock;
        if (set.Contains("SVT") || set.Contains("AVNRT") || set.Contains("AVRT")) return ClinicalRhythmState.Svt;
        if (set.Contains("AFIB")) return ClinicalRhythmState.AtrialFibrillation;
        if (set.Contains("APACE")) return ClinicalRhythmState.Paced;
        if (set.Contains("ST")) return ClinicalRhythmState.SinusTachycardia;
        if (set.Contains("SR")) return ClinicalRhythmState.Sinus;
        return null;
    }

    /// <summary>True when the state has no dataset rhythm and must be drawn synthetically (asystole → a flat
    /// isoelectric line).</summary>
    public static bool IsSynthesizedFlatline(ClinicalRhythmState state) =>
        state == ClinicalRhythmState.Asystole;

    /// <summary>True for a pulseless cardiac-arrest rhythm (VF, pulseless VT, asystole) — the states where
    /// ACLS calls for chest compressions. The treatment panel uses this to prompt CPR. (Pulsed VT and
    /// Torsades are unstable but not classed as pulseless arrest here.)</summary>
    public static bool IsArrestRhythm(ClinicalRhythmState state) =>
        state is ClinicalRhythmState.VentricularFibrillation
              or ClinicalRhythmState.PulselessVt
              or ClinicalRhythmState.Asystole;
}
