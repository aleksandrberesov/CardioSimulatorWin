using System;
using System.Collections.Generic;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Cuts a lead's samples into equal contiguous <em>baseline-zeroed</em> parts for the «Собери ЭКГ»
/// question type. Pure and deterministic — no wave detection, so it works for every rhythm. The parts
/// are returned in original order (part 0 first); the runtime shuffles them for the student to reorder.
/// </summary>
public static class EcgAssemblySlicer
{
    public const int MinParts = 2;
    public const int MaxParts = 8;

    /// <summary>
    /// Slices the leading <paramref name="windowSamples"/> of <paramref name="samples"/> (clamped to the
    /// buffer; ≤0 = whole lead) into <paramref name="partCount"/> equal contiguous parts, each value minus
    /// <paramref name="baseline"/> so 0 = isoline. Any trailing samples that don't divide evenly are
    /// dropped so all parts share one length (uniform tiles + a continuous join when reordered right).
    /// Returns null if there aren't enough samples for one sample per part.
    /// </summary>
    public static IReadOnlyList<int[]>? Split(
        IReadOnlyList<int> samples, int baseline, int partCount, int windowSamples = 0)
    {
        if (samples is null) return null;
        if (partCount < MinParts) partCount = MinParts;
        if (partCount > MaxParts) partCount = MaxParts;

        var total = samples.Count;
        if (total < partCount) return null;

        var window = windowSamples > 0 ? Math.Min(windowSamples, total) : total;
        var per = window / partCount;   // floor — equal parts, remainder dropped
        if (per < 1) return null;

        var parts = new List<int[]>(partCount);
        for (var k = 0; k < partCount; k++)
        {
            var start = k * per;
            var seg = new int[per];
            for (var i = 0; i < per; i++) seg[i] = samples[start + i] - baseline;
            parts.Add(seg);
        }
        return parts;
    }
}
