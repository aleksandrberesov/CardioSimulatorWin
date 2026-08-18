using System;
using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;

namespace CardioSimulator.Core.Domain;

/// <summary>A wave amplitude (mV, baseline-relative) measured on one lead at a marked peak.</summary>
public sealed record LeadAmplitude(Lead Lead, double AmplitudeMv);

/// <summary>
/// Clinical interval/segment durations (seconds) derived from one lead's significant-point markup,
/// plus heart rate (mean R-R and the 6-second method), the Bazett-corrected QT, and per-lead P/Q
/// amplitudes. Each field is <c>null</c> when the boundary points (or waveforms) it needs are
/// missing. Consumed by the monitor's measurements readout (the translucent "values column", aka the
/// pQRSt readout), which replaces the crammed on-trace interval labels.
/// </summary>
public sealed record EcgMeasurementSet(
    double? HeartRateBpm = null,
    double? HeartRate6SecBpm = null,
    double? RrSeconds = null,
    double? PSeconds = null,
    double? PqSeconds = null,
    double? PrSeconds = null,
    double? QrsSeconds = null,
    double? QtSeconds = null,
    double? QtcSeconds = null,
    double? StSeconds = null,
    double? TSeconds = null,
    IReadOnlyList<LeadAmplitude>? PAmplitudesMv = null,
    IReadOnlyList<LeadAmplitude>? QAmplitudesMv = null)
{
    /// <summary>True when at least one measurement could be computed.</summary>
    public bool HasAny =>
        HeartRateBpm is not null || HeartRate6SecBpm is not null || RrSeconds is not null ||
        PSeconds is not null || PqSeconds is not null || PrSeconds is not null ||
        QrsSeconds is not null || QtSeconds is not null || QtcSeconds is not null ||
        StSeconds is not null || TSeconds is not null ||
        PAmplitudesMv is { Count: > 0 } || QAmplitudesMv is { Count: > 0 };
}

/// <summary>Derives <see cref="EcgMeasurementSet"/> values from significant-point markup.</summary>
public static class EcgMeasurements
{
    /// <summary>The 6-second heart-rate window (seconds): rate = complexes in the window scaled to 60 s.</summary>
    private const double SixSecondWindow = 6.0;

    /// <summary>
    /// Computes interval/segment durations, heart rate, QTc (Bazett) and — when
    /// <paramref name="waveforms"/> is supplied — per-lead P/Q amplitudes, from
    /// <paramref name="points"/>. When a wave boundary is marked more than once, the last marker of
    /// each type wins (matching the on-graph overlay's <c>associateBy</c>). R-R and the mean heart
    /// rate use the mean spacing across all R peaks; the 6-second heart rate counts R peaks in the
    /// leading window (scaled to 60 s, using the whole strip when it is shorter than 6 s). Returns an
    /// empty set when there are no points or the sample rate is unknown.
    /// </summary>
    /// <param name="waveforms">Baseline-zeroed lead waveforms, used only for P/Q amplitudes. When
    /// null (or empty), the amplitude fields stay null and everything else is unaffected.</param>
    /// <param name="adcCountsPerMv">ADC counts per millivolt, from the active calibration.</param>
    public static EcgMeasurementSet Compute(
        IReadOnlyList<SignificantPoint> points,
        double sampleRateHz,
        IReadOnlyDictionary<Lead, Points>? waveforms = null,
        double adcCountsPerMv = 1024.0)
    {
        if (points is null || points.Count == 0 || sampleRateHz <= 0)
            return new EcgMeasurementSet();

        var map = new Dictionary<EcgPointType, int>();
        foreach (var pt in points) map[pt.Type] = pt.Index;

        double? Interval(EcgPointType s, EcgPointType e) =>
            map.TryGetValue(s, out var si) && map.TryGetValue(e, out var ei) && ei > si
                ? (ei - si) / sampleRateHz
                : null;

        var rPeaks = points.Where(p => p.Type == EcgPointType.R_PEAK)
            .Select(p => p.Index).OrderBy(i => i).ToList();
        double? rr = null;
        if (rPeaks.Count >= 2)
        {
            double sum = 0;
            for (var i = 0; i + 1 < rPeaks.Count; i++) sum += rPeaks[i + 1] - rPeaks[i];
            var meanSamples = sum / (rPeaks.Count - 1);
            if (meanSamples > 0) rr = meanSamples / sampleRateHz;
        }
        double? hr = rr is > 0 ? 60.0 / rr : null;

        // 6-second method: count R peaks in the leading window and scale to a minute. Falls back to the
        // whole strip when it is shorter than 6 s, so short rhythms still yield an honest average rate.
        double? hr6 = null;
        if (rPeaks.Count >= 1)
        {
            var stripSamples = 0;
            if (waveforms is not null)
                foreach (var p in waveforms.Values)
                    if (p.Values.Count > stripSamples) stripSamples = p.Values.Count;
            if (stripSamples <= 0) stripSamples = rPeaks[^1] + 1;
            var windowSamples = Math.Min(SixSecondWindow * sampleRateHz, stripSamples);
            var windowSec = windowSamples / sampleRateHz;
            if (windowSec > 0)
            {
                var count = rPeaks.Count(i => i < windowSamples);
                if (count > 0) hr6 = count * 60.0 / windowSec;
            }
        }

        var qt = Interval(EcgPointType.QRS_START, EcgPointType.T_END);
        // Bazett-corrected QT: QTc = QT / sqrt(RR), both in seconds.
        double? qtc = qt is { } qtv && rr is > 0 ? qtv / Math.Sqrt(rr.Value) : null;

        return new EcgMeasurementSet(
            HeartRateBpm: hr,
            HeartRate6SecBpm: hr6,
            RrSeconds: rr,
            PSeconds: Interval(EcgPointType.P_START, EcgPointType.P_END),
            PqSeconds: Interval(EcgPointType.P_START, EcgPointType.Q_PEAK),
            PrSeconds: Interval(EcgPointType.P_START, EcgPointType.QRS_START),
            QrsSeconds: Interval(EcgPointType.QRS_START, EcgPointType.QRS_END),
            QtSeconds: qt,
            QtcSeconds: qtc,
            StSeconds: Interval(EcgPointType.QRS_END, EcgPointType.T_START),
            TSeconds: Interval(EcgPointType.T_START, EcgPointType.T_END),
            PAmplitudesMv: AmplitudesAt(EcgPointType.P_PEAK, map, waveforms, adcCountsPerMv),
            QAmplitudesMv: AmplitudesAt(EcgPointType.Q_PEAK, map, waveforms, adcCountsPerMv));
    }

    /// <summary>
    /// Baseline-relative amplitude (mV) at the marked <paramref name="peak"/> for every lead whose
    /// waveform is loaded, in canonical lead order. Waveforms are baseline-zeroed, so the amplitude is
    /// the sample value divided by <paramref name="adcCountsPerMv"/> (typically negative for Q).
    /// Returns null when the peak is unmarked, no waveforms are supplied, or no lead has a sample at
    /// that index.
    /// </summary>
    private static IReadOnlyList<LeadAmplitude>? AmplitudesAt(
        EcgPointType peak,
        IReadOnlyDictionary<EcgPointType, int> map,
        IReadOnlyDictionary<Lead, Points>? waveforms,
        double adcCountsPerMv)
    {
        if (waveforms is null || waveforms.Count == 0 || adcCountsPerMv <= 0) return null;
        if (!map.TryGetValue(peak, out var idx) || idx < 0) return null;

        var list = new List<LeadAmplitude>();
        foreach (var lead in Leads.All)
        {
            if (waveforms.TryGetValue(lead, out var pts) && idx < pts.Values.Count)
                list.Add(new LeadAmplitude(lead, pts.Values[idx] / adcCountsPerMv));
        }
        return list.Count > 0 ? list : null;
    }
}
