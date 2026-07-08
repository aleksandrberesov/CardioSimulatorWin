using System;
using System.Collections.Generic;
using System.Linq;

namespace CardioSimulator.Core.Domain;

/// <summary>The six wave boundaries of a single cardiac cycle, in sample indices into one lead.</summary>
public readonly record struct BeatFiducials(
    int PStart, int PEnd, int QrsStart, int QrsEnd, int TStart, int TEnd)
{
    /// <summary>
    /// True when every boundary is present (non-negative) and ordered so the three segments are
    /// non-empty and non-overlapping: P &lt; QRS &lt; T with each start before its end.
    /// </summary>
    public bool IsValid =>
        PStart >= 0 && PEnd > PStart &&
        QrsStart >= PEnd && QrsEnd > QrsStart &&
        TStart >= QrsEnd && TEnd > TStart;
}

/// <summary>
/// Cuts one representative beat out of a lead's samples into P / QRS / T fragments, for the
/// «Собери ЭКГ» question type. Pure and deterministic: it works from wave-boundary markers
/// (<see cref="SignificantPoint"/>s), whether those were hand-authored on the pathology or produced by
/// the App's auto-detector and handed in as a flat list. The heavy signal processing (R-peak /
/// landmark detection) lives in the App layer (BioSPPy) — this only associates and slices.
/// </summary>
public static class EcgBeatSlicer
{
    /// <summary>
    /// Associates a flat marker list into beats and returns the most usable one — the complete beat
    /// whose QRS sits nearest the middle of the record (avoiding edge artifacts). Returns null when no
    /// single beat carries all six P/QRS/T boundaries in order.
    /// </summary>
    public static BeatFiducials? BestBeat(IReadOnlyList<SignificantPoint> points, int sampleCount)
    {
        if (points is null || points.Count == 0) return null;

        var pStart = Sorted(points, EcgPointType.P_START);
        var pEnd = Sorted(points, EcgPointType.P_END);
        var qrsStart = Sorted(points, EcgPointType.QRS_START);
        var qrsEnd = Sorted(points, EcgPointType.QRS_END);
        var tStart = Sorted(points, EcgPointType.T_START);
        var tEnd = Sorted(points, EcgPointType.T_END);
        if (qrsStart.Count == 0 || qrsEnd.Count == 0) return null;

        var center = sampleCount > 0 ? sampleCount / 2 : 0;
        BeatFiducials? best = null;
        var bestDistance = int.MaxValue;

        // Anchor each candidate beat on a QRS_START; gather the boundaries that frame it.
        foreach (var qs in qrsStart)
        {
            var qe = FirstAfter(qrsEnd, qs);
            if (qe < 0) continue;

            var ps = LastBefore(pStart, qs);
            var pe = ps < 0 ? -1 : InRange(pEnd, ps, qs);
            var ts = FirstAtOrAfter(tStart, qe);
            var te = ts < 0 ? -1 : FirstAfter(tEnd, ts);

            var beat = new BeatFiducials(ps, pe, qs, qe, ts, te);
            if (!beat.IsValid) continue;

            var distance = Math.Abs((qs + qe) / 2 - center);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = beat;
            }
        }
        return best;
    }

    /// <summary>
    /// Slices <paramref name="samples"/> into P / QRS / T pieces of <em>baseline-zeroed</em> values
    /// (each value minus <paramref name="baseline"/>, so 0 = isoline). Returns null if the beat is
    /// invalid or runs past the sample buffer.
    /// </summary>
    public static (int[] P, int[] Qrs, int[] T)? Slice(
        IReadOnlyList<int> samples, int baseline, BeatFiducials beat)
    {
        if (samples is null || !beat.IsValid) return null;
        if (beat.TEnd > samples.Count) return null;

        int[] Seg(int start, int end)
        {
            var seg = new int[end - start];
            for (var i = 0; i < seg.Length; i++) seg[i] = samples[start + i] - baseline;
            return seg;
        }

        return (Seg(beat.PStart, beat.PEnd), Seg(beat.QrsStart, beat.QrsEnd), Seg(beat.TStart, beat.TEnd));
    }

    // ── marker-list helpers ──────────────────────────────────────────────────

    private static List<int> Sorted(IReadOnlyList<SignificantPoint> points, EcgPointType type) =>
        points.Where(p => p.Type == type).Select(p => p.Index).OrderBy(i => i).ToList();

    /// <summary>First index strictly greater than <paramref name="after"/>, or -1.</summary>
    private static int FirstAfter(List<int> sorted, int after)
    {
        foreach (var i in sorted) if (i > after) return i;
        return -1;
    }

    /// <summary>First index at or after <paramref name="at"/>, or -1.</summary>
    private static int FirstAtOrAfter(List<int> sorted, int at)
    {
        foreach (var i in sorted) if (i >= at) return i;
        return -1;
    }

    /// <summary>Last index strictly less than <paramref name="before"/>, or -1.</summary>
    private static int LastBefore(List<int> sorted, int before)
    {
        var found = -1;
        foreach (var i in sorted) { if (i < before) found = i; else break; }
        return found;
    }

    /// <summary>First index in the open interval (<paramref name="low"/>, <paramref name="high"/>), or -1.</summary>
    private static int InRange(List<int> sorted, int low, int high)
    {
        foreach (var i in sorted) if (i > low && i < high) return i;
        return -1;
    }
}
