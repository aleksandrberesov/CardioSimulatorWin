using System;
using System.Collections.Generic;
using System.Linq;
using BioSPPy.Net.Signals.Tools;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Shared ECG display-filter math: builds Butterworth coefficients for the monitor's selectable
/// filter bands (0.5 Hz high-pass, 40 Hz low-pass, or the 0.5–40 Hz band) and applies them with a
/// zero-phase <c>filtfilt</c>. Extracted from <see cref="MonitorView"/> so the Constructor's preview
/// panes filter their traces with the exact same processing the Teaching monitor uses.
/// </summary>
internal static class EcgDisplayFilter
{
    /// <summary>Effective sample rate for the mode (falls back to 1 kHz when unknown).</summary>
    public static double SampleRate(MonitorModeModel mode)
        => mode.Calibration is { SampleRateHz: > 0 } cal ? cal.SampleRateHz : 1000.0;

    /// <summary>Butterworth coefficients for the chosen band, or null when unfiltered / on failure.</summary>
    public static (double[] b, double[] a)? Build(EcgFilterType filterType, double fs)
    {
        if (filterType == EcgFilterType.None) return null;
        double nyq = fs / 2.0;
        try
        {
            return filterType switch
            {
                EcgFilterType.Lowpass => Filtering.Butterworth(2, new[] { 40.0 / nyq }, "lowpass"),
                EcgFilterType.Highpass => Filtering.Butterworth(2, new[] { 0.5 / nyq }, "highpass"),
                EcgFilterType.Bandpass => Filtering.Butterworth(2, new[] { 0.5 / nyq, 40.0 / nyq }, "bandpass"),
                _ => ((double[] b, double[] a)?)null,
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Filter build failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Applies precomputed coefficients to a <see cref="Points"/> waveform (zero-phase).</summary>
    public static Points Apply(Points points, double[] b, double[] a) => new(Apply(points.Values, b, a));

    /// <summary>Applies precomputed coefficients to a sample list. Short signals pass through unchanged.</summary>
    public static float[] Apply(IReadOnlyList<float> values, double[] b, double[] a)
    {
        if (values.Count < 15) return values as float[] ?? values.ToArray(); // too short for filtfilt padding
        try
        {
            var sig = new double[values.Count];
            for (int i = 0; i < values.Count; i++) sig[i] = values[i];
            double[] filt = Filtering.FiltFilt(b, a, sig);
            var outp = new float[filt.Length];
            for (int i = 0; i < filt.Length; i++) outp[i] = (float)filt[i];
            return outp;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Filtering failed: {ex.Message}");
            return values as float[] ?? values.ToArray();
        }
    }

    /// <summary>One-shot convenience: filters a sample list for the mode's active band, or returns it
    /// unchanged when no filter is selected.</summary>
    public static IReadOnlyList<float> Filter(IReadOnlyList<float> values, MonitorModeModel mode)
        => Filter(values, mode.FilterType, SampleRate(mode));

    /// <summary>One-shot convenience: filters a sample list for the given band at sample rate
    /// <paramref name="fs"/>, or returns it unchanged when <paramref name="filterType"/> is
    /// <see cref="EcgFilterType.None"/> (or the coefficients fail to build).</summary>
    public static IReadOnlyList<float> Filter(IReadOnlyList<float> values, EcgFilterType filterType, double fs)
    {
        var coeffs = Build(filterType, fs);
        return coeffs is { } c ? Apply(values, c.b, c.a) : values;
    }
}
