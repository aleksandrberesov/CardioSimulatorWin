using System;
using System.Collections.Generic;
using BioSPPy.Net.Signals.Ecg;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Analysis;

/// <summary>
/// The electrical-axis result plus the QRS spans (per lead) it was measured from, so the trace can
/// shade those segments while the ЭОС window is open.
/// </summary>
public sealed record EosAnalysis(
    EosResult Result,
    IReadOnlyDictionary<Lead, IReadOnlyList<EcgSpan>> HighlightSpans);

/// <summary>
/// Measures the QRS deflections of leads I and aVF from the live waveforms and derives the
/// electrical axis (α angle) via <see cref="EosAxis"/>. Detection reuses the same BioSPPy pipeline
/// as the Constructor's Auto-Detect (Hamilton R-peaks + fiducial landmarks). Deflections are
/// averaged across every detected complex for stability, then converted to millimetres on the
/// standard grid using the monitor calibration.
/// </summary>
public static class EosAnalyzer
{
    /// <summary>
    /// Computes the axis and QRS highlight spans from <paramref name="waveforms"/> (baseline-zeroed
    /// samples per lead), or returns <c>null</c> when leads I/aVF are missing or no QRS complex can
    /// be detected.
    /// </summary>
    public static EosAnalysis? Analyze(IReadOnlyDictionary<Lead, Points>? waveforms, EcgCalibration calibration)
    {
        if (waveforms is null) return null;
        var i = Measure(waveforms, Lead.I, calibration);
        var f = Measure(waveforms, Lead.aVF, calibration);
        if (i is null || f is null) return null;

        var result = EosAxis.From(i.Value.Measure, f.Value.Measure);
        var spans = new Dictionary<Lead, IReadOnlyList<EcgSpan>>
        {
            [Lead.I] = i.Value.Spans,
            [Lead.aVF] = f.Value.Spans,
        };
        return new EosAnalysis(result, spans);
    }

    private static (EosLeadMeasure Measure, IReadOnlyList<EcgSpan> Spans)? Measure(
        IReadOnlyDictionary<Lead, Points> waveforms, Lead lead, EcgCalibration calibration)
    {
        if (!waveforms.TryGetValue(lead, out var points) || points.Values.Count == 0) return null;
        double fs = calibration.SampleRateHz;
        if (fs <= 0 || calibration.AdcCountsPerMv <= 0) return null;

        var sig = new double[points.Values.Count];
        for (var n = 0; n < sig.Length; n++) sig[n] = points.Values[n]; // already baseline-zeroed

        IReadOnlyList<EcgLandmarks> landmarks;
        try
        {
            var rpeaks = QrsSegmenters.HamiltonSegmenter(sig, fs);
            rpeaks = QrsSegmenters.CorrectRPeaks(sig, rpeaks, fs, 0.05);
            if (rpeaks.Length == 0) return null;
            landmarks = FiducialPoints.GetLandmarks(sig, rpeaks, fs);
        }
        catch
        {
            return null; // detection can throw on degenerate/flat signals — treat as "no data"
        }
        if (landmarks.Count == 0) return null;

        // mm per ADC count on the standard grid (samples are already relative to baseline).
        double toMm = calibration.GainMmPerMv / calibration.AdcCountsPerMv;

        double rSum = 0, qSum = 0, sSum = 0;
        int nR = 0, nQ = 0, nS = 0;
        var spans = new List<EcgSpan>();
        foreach (var lm in landmarks)
        {
            if (InRange(lm.RPeak, sig.Length)) { rSum += Math.Max(0, sig[lm.RPeak] * toMm); nR++; }
            if (InRange(lm.QPeak, sig.Length)) { qSum += Math.Max(0, -sig[lm.QPeak] * toMm); nQ++; }
            if (InRange(lm.SPeak, sig.Length)) { sSum += Math.Max(0, -sig[lm.SPeak] * toMm); nS++; }

            if (InRange(lm.QrsStart, sig.Length) && InRange(lm.QrsEnd, sig.Length) && lm.QrsEnd > lm.QrsStart)
                spans.Add(new EcgSpan(lm.QrsStart, lm.QrsEnd));
        }
        if (nR == 0) return null;

        var measure = new EosLeadMeasure(
            QMm: nQ > 0 ? qSum / nQ : 0,
            RMm: rSum / nR,
            SMm: nS > 0 ? sSum / nS : 0);
        return (measure, spans);
    }

    private static bool InRange(int index, int length) => index >= 0 && index < length;
}
