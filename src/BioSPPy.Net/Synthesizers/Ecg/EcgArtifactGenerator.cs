using System;
using BioSPPy.Net.Signals.Tools;

namespace BioSPPy.Net.Synthesizers.Ecg;

/// <summary>
/// The kinds of recording artifact / noise that can be added to a clean ECG trace. These mirror the
/// classic categories used in ECG quality literature (and the BioSPPy SQI work): muscle/EMG tremor,
/// power-line (mains) interference, baseline wander, electrode-contact noise, and motion artifact.
/// </summary>
public enum EcgArtifactKind
{
    /// <summary>Electromyographic (muscle tremor) noise — broadband, high-frequency fuzz.</summary>
    Muscle,
    /// <summary>Power-line (mains) interference — a steady 50&#160;Hz sinusoid plus a small harmonic.</summary>
    Mains,
    /// <summary>Baseline wander — slow, low-frequency drift of the isoelectric line (&lt;1&#160;Hz).</summary>
    Baseline,
    /// <summary>Electrode-contact noise — sparse sharp transients ("pops") that decay quickly.</summary>
    Contact,
    /// <summary>Motion artifact — occasional large, low-frequency excursions from patient movement.</summary>
    Motion,
}

/// <summary>
/// Generates additive recording-artifact noise for an ECG signal, built on the BioSPPy.Net DSP tools
/// (<see cref="Filtering"/> for band-limiting, Gaussian white noise for the stochastic kinds). Every
/// artifact's amplitude is expressed as a fraction of the host signal's peak-to-peak range so the
/// noise stays visually proportionate regardless of the signal's units (mV, ADC counts, …).
/// </summary>
public static class EcgArtifactGenerator
{
    /// <summary>
    /// Returns a copy of <paramref name="signal"/> with the given artifact added. The noise is scaled
    /// to the signal's own peak-to-peak amplitude; <paramref name="intensity"/> linearly scales it
    /// (1.0 = the default, visually obvious-but-not-destructive level). <paramref name="seed"/> makes
    /// the (stochastic) output reproducible so re-rendering the same trace yields the same noise.
    /// </summary>
    public static double[] Apply(
        double[] signal,
        EcgArtifactKind kind,
        double samplingRate,
        double intensity = 1.0,
        int seed = 0)
    {
        if (signal is null) throw new ArgumentNullException(nameof(signal));
        int n = signal.Length;
        if (n == 0) return Array.Empty<double>();

        double reference = PeakToPeak(signal);
        double[] noise = Generate(n, kind, samplingRate, reference, intensity, seed);

        var result = new double[n];
        for (int i = 0; i < n; i++) result[i] = signal[i] + noise[i];
        return result;
    }

    /// <summary>
    /// Builds a length-<paramref name="n"/> additive-noise array for the given artifact.
    /// <paramref name="referenceAmplitude"/> is the host signal's peak-to-peak range that the noise is
    /// scaled against.
    /// </summary>
    public static double[] Generate(
        int n,
        EcgArtifactKind kind,
        double samplingRate,
        double referenceAmplitude,
        double intensity,
        int seed)
    {
        if (n <= 0) return Array.Empty<double>();
        if (samplingRate <= 0) samplingRate = 1000.0;
        double refAmp = referenceAmplitude > 1e-9 ? referenceAmplitude : 1.0;
        var rng = new Random(seed);

        return kind switch
        {
            EcgArtifactKind.Mains => Mains(n, samplingRate, refAmp, intensity),
            EcgArtifactKind.Muscle => Muscle(n, samplingRate, refAmp, intensity, rng),
            EcgArtifactKind.Baseline => Baseline(n, samplingRate, refAmp, intensity, rng),
            EcgArtifactKind.Motion => Motion(n, samplingRate, refAmp, intensity, rng),
            EcgArtifactKind.Contact => Contact(n, samplingRate, refAmp, intensity, rng),
            _ => new double[n],
        };
    }

    // ── Mains: 50 Hz power-line hum + a smaller 3rd harmonic ────────────────────
    private static double[] Mains(int n, double fs, double refAmp, double intensity)
    {
        const double f0 = 50.0;
        double amp = 0.06 * refAmp * intensity;
        double nyq = fs / 2.0;
        var noise = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / fs;
            double v = Math.Sin(2.0 * Math.PI * f0 * t);
            if (3.0 * f0 < nyq) v += 0.2 * Math.Sin(2.0 * Math.PI * 3.0 * f0 * t); // mild 150 Hz harmonic
            noise[i] = amp * v;
        }
        return noise;
    }

    // ── Muscle / EMG: white noise high-pass filtered into the EMG band (>25 Hz) ─
    private static double[] Muscle(int n, double fs, double refAmp, double intensity, Random rng)
    {
        var white = WhiteNoise(n, rng);
        double cut = 25.0;
        double nyq = fs / 2.0;
        double[] band = white;
        if (n > 30 && cut < nyq)
        {
            try
            {
                var (b, a) = Filtering.Butterworth(order: 2, Wn: new[] { cut / nyq }, band: "highpass");
                band = Filtering.FiltFilt(b, a, white);
            }
            catch
            {
                band = white; // signal too short for filtfilt padding — fall back to raw white noise
            }
        }
        // Normalize to a target standard deviation so the fuzz reads consistently across signals.
        double targetStd = 0.05 * refAmp * intensity;
        return ScaleToStd(band, targetStd);
    }

    // ── Baseline wander: sum of slow sinusoids (<1 Hz) with random phases ────────
    private static double[] Baseline(int n, double fs, double refAmp, double intensity, Random rng)
    {
        double amp = 0.22 * refAmp * intensity;
        double[] freqs = { 0.15, 0.3, 0.5 };
        double[] weights = { 1.0, 0.5, 0.3 };
        double[] phases = { rng.NextDouble() * Tau, rng.NextDouble() * Tau, rng.NextDouble() * Tau };
        double weightSum = weights[0] + weights[1] + weights[2];

        var noise = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / fs;
            double v = 0.0;
            for (int k = 0; k < freqs.Length; k++)
                v += weights[k] * Math.Sin(2.0 * Math.PI * freqs[k] * t + phases[k]);
            noise[i] = amp * v / weightSum;
        }
        return noise;
    }

    // ── Motion: a handful of large, smooth low-frequency excursions ─────────────
    private static double[] Motion(int n, double fs, double refAmp, double intensity, Random rng)
    {
        double amp = 0.5 * refAmp * intensity;
        double durationSec = n / fs;
        int bumps = Math.Max(1, (int)Math.Round(durationSec * 0.35)); // ~1 every ~3 s

        var noise = new double[n];
        for (int b = 0; b < bumps; b++)
        {
            int center = rng.Next(n);
            double widthSec = 0.2 + rng.NextDouble() * 0.4; // 0.2–0.6 s wide
            double widthSamples = widthSec * fs;
            double sign = rng.NextDouble() < 0.5 ? -1.0 : 1.0;
            double mag = amp * (0.5 + rng.NextDouble() * 0.5);
            int span = (int)(widthSamples * 3.0);
            for (int i = Math.Max(0, center - span); i < Math.Min(n, center + span); i++)
            {
                double d = (i - center) / widthSamples;
                noise[i] += sign * mag * Math.Exp(-0.5 * d * d); // Gaussian bump
            }
        }
        return noise;
    }

    // ── Contact: sparse sharp transients ("electrode pops") with fast decay ─────
    private static double[] Contact(int n, double fs, double refAmp, double intensity, Random rng)
    {
        double amp = 0.6 * refAmp * intensity;
        double durationSec = n / fs;
        int pops = Math.Max(1, (int)Math.Round(durationSec * 0.8)); // ~1 pop per ~1.25 s
        double tau = 0.04 * fs; // ~40 ms decay

        var noise = new double[n];
        for (int p = 0; p < pops; p++)
        {
            int pos = rng.Next(n);
            double sign = rng.NextDouble() < 0.5 ? -1.0 : 1.0;
            double mag = amp * (0.5 + rng.NextDouble() * 0.5);
            int span = (int)(tau * 6.0);
            for (int i = pos; i < Math.Min(n, pos + span); i++)
            {
                noise[i] += sign * mag * Math.Exp(-(i - pos) / tau);
            }
        }
        return noise;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private const double Tau = 2.0 * Math.PI;

    private static double[] WhiteNoise(int n, Random rng)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++)
        {
            // Box-Muller standard normal.
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            w[i] = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(Tau * u2);
        }
        return w;
    }

    private static double PeakToPeak(double[] signal)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int i = 0; i < signal.Length; i++)
        {
            if (signal[i] < min) min = signal[i];
            if (signal[i] > max) max = signal[i];
        }
        return max - min;
    }

    private static double[] ScaleToStd(double[] x, double targetStd)
    {
        int n = x.Length;
        if (n == 0 || targetStd <= 0) return new double[n];
        double mean = 0.0;
        for (int i = 0; i < n; i++) mean += x[i];
        mean /= n;
        double var = 0.0;
        for (int i = 0; i < n; i++) { double d = x[i] - mean; var += d * d; }
        var /= n;
        double std = Math.Sqrt(var);
        if (std < 1e-12) return new double[n];
        double scale = targetStd / std;
        var result = new double[n];
        for (int i = 0; i < n; i++) result[i] = (x[i] - mean) * scale;
        return result;
    }
}
