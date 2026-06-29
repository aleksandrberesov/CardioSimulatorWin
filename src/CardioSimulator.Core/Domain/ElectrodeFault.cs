using System.Collections.Generic;
using CardioSimulator.Core.Data;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Applies an <see cref="ElectrodeState"/> hookup fault to a set of lead waveforms for the
/// "Электроды" teaching window. Pure lead algebra on baseline-zeroed samples (see
/// <c>PathologyRepository.LeadWaveform</c>, which centres each lead on zero):
/// <list type="bullet">
///   <item><see cref="ElectrodeState.Swapped"/> — the classic RA/LA limb-electrode reversal.
///     Because the standard leads derive from the RA/LA/LL potentials, exchanging RA and LA
///     inverts lead I, swaps II↔III and aVR↔aVL, and leaves aVF and every precordial lead
///     untouched (the Wilson central terminal, the average of RA+LA+LL, is unchanged by the swap).</item>
///   <item><see cref="ElectrodeState.Displacement"/> — precordial electrodes off their landmarks:
///     the V1–V6 leads lose amplitude (poor R-wave progression); limb leads are unaffected.</item>
/// </list>
/// The returned map is a copy; the input is never mutated. Missing source leads are skipped, so a
/// 6-limb-only recording still transforms correctly.
/// </summary>
public static class ElectrodeFault
{
    /// <summary>Fraction of amplitude a misplaced precordial electrode still records.</summary>
    private const float DisplacementGain = 0.55f;

    private static readonly Lead[] Precordial =
        { Lead.V1, Lead.V2, Lead.V3, Lead.V4, Lead.V5, Lead.V6 };

    /// <summary>
    /// Returns <paramref name="waveforms"/> transformed for <paramref name="state"/>. For
    /// <see cref="ElectrodeState.Ok"/> (or an empty map) the original instance is returned unchanged.
    /// </summary>
    public static IReadOnlyDictionary<Lead, Points> Apply(
        IReadOnlyDictionary<Lead, Points> waveforms, ElectrodeState state)
    {
        if (state == ElectrodeState.Ok || waveforms.Count == 0) return waveforms;

        var result = new Dictionary<Lead, Points>(waveforms);
        switch (state)
        {
            case ElectrodeState.Swapped:
                if (waveforms.TryGetValue(Lead.I, out var leadI)) result[Lead.I] = Scale(leadI, -1f);
                Swap(result, waveforms, Lead.II, Lead.III);
                Swap(result, waveforms, Lead.aVR, Lead.aVL);
                // aVF and V1..V6 are unchanged by an RA/LA exchange.
                break;

            case ElectrodeState.Displacement:
                foreach (var v in Precordial)
                    if (waveforms.TryGetValue(v, out var pts)) result[v] = Scale(pts, DisplacementGain);
                break;
        }
        return result;
    }

    /// <summary>Exchanges the waveforms assigned to leads <paramref name="a"/> and <paramref name="b"/>,
    /// reading from the untouched <paramref name="source"/> so the swap is order-independent.</summary>
    private static void Swap(
        IDictionary<Lead, Points> result, IReadOnlyDictionary<Lead, Points> source, Lead a, Lead b)
    {
        var hasA = source.TryGetValue(a, out var pa);
        var hasB = source.TryGetValue(b, out var pb);
        if (hasA) result[b] = pa!;
        if (hasB) result[a] = pb!;
    }

    private static Points Scale(Points points, float factor)
    {
        var values = points.Values;
        var scaled = new float[values.Count];
        for (var i = 0; i < values.Count; i++) scaled[i] = values[i] * factor;
        return new Points(scaled);
    }
}
