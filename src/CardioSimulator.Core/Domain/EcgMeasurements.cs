using System.Collections.Generic;
using System.Linq;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Clinical interval/segment durations (seconds) derived from one lead's significant-point markup,
/// plus the heart rate implied by the R-R spacing. Each field is <c>null</c> when the boundary
/// points it needs are missing. Consumed by the monitor's measurements readout (the translucent
/// "values column"), which replaces the crammed on-trace interval labels.
/// </summary>
public sealed record EcgMeasurementSet(
    double? HeartRateBpm = null,
    double? RrSeconds = null,
    double? PSeconds = null,
    double? PrSeconds = null,
    double? QrsSeconds = null,
    double? QtSeconds = null,
    double? StSeconds = null,
    double? TSeconds = null)
{
    /// <summary>True when at least one measurement could be computed.</summary>
    public bool HasAny =>
        HeartRateBpm is not null || RrSeconds is not null || PSeconds is not null ||
        PrSeconds is not null || QrsSeconds is not null || QtSeconds is not null ||
        StSeconds is not null || TSeconds is not null;
}

/// <summary>Derives <see cref="EcgMeasurementSet"/> values from significant-point markup.</summary>
public static class EcgMeasurements
{
    /// <summary>
    /// Computes interval/segment durations and heart rate from <paramref name="points"/>. When a
    /// wave boundary is marked more than once, the last marker of each type wins (matching the
    /// on-graph overlay's <c>associateBy</c>). R-R and heart rate use the mean spacing across all
    /// R peaks. Returns an empty set when there are no points or the sample rate is unknown.
    /// </summary>
    public static EcgMeasurementSet Compute(IReadOnlyList<SignificantPoint> points, double sampleRateHz)
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

        return new EcgMeasurementSet(
            HeartRateBpm: hr,
            RrSeconds: rr,
            PSeconds: Interval(EcgPointType.P_START, EcgPointType.P_END),
            PrSeconds: Interval(EcgPointType.P_START, EcgPointType.QRS_START),
            QrsSeconds: Interval(EcgPointType.QRS_START, EcgPointType.QRS_END),
            QtSeconds: Interval(EcgPointType.QRS_START, EcgPointType.T_END),
            StSeconds: Interval(EcgPointType.QRS_END, EcgPointType.T_START),
            TSeconds: Interval(EcgPointType.T_START, EcgPointType.T_END));
    }
}
