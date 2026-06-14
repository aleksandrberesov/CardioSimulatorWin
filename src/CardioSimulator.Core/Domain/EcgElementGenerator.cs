using CardioSimulator.Core.Data;

namespace CardioSimulator.Core.Domain;

/// <summary>The building-block ECG elements an author can drop into a lead.</summary>
public enum EcgElement
{
    /// <summary>Flat segment at the isoline (PR/TP segment, or an eraser).</summary>
    Baseline,
    /// <summary>Atrial depolarization — a small positive rounded hump.</summary>
    PWave,
    /// <summary>Ventricular depolarization — small Q, dominant R spike, S dip.</summary>
    QrsComplex,
    /// <summary>Ventricular repolarization — a broad, low positive hump.</summary>
    TWave,
    /// <summary>A constant offset from the isoline (ST elevation/depression).</summary>
    StSegment,
}

/// <summary>Width (duration) + height (amplitude) knobs for a generated element, in clinical units.</summary>
public sealed record EcgElementParams(float DurationMs, float AmplitudeMv);

/// <summary>
/// Generates short ADC waveform segments for individual ECG elements (P, QRS, T, ST, flat baseline)
/// so an author assembles an ECG from clinical building blocks instead of moving raw points. Output
/// is baseline-centered ADC, mapped from clinical units (ms / mV) via <see cref="EcgCalibration"/>.
/// A generated segment is meant to be written into a lead and then fine-tuned with the existing
/// point editor. Phase 4.0 of docs/plans/active/2026-06-further-development-plan.md (WS4).
/// </summary>
public static class EcgElementGenerator
{
    public const int AdcMin = 0;
    public const int AdcMax = 2048;

    /// <summary>Clinically reasonable starting width/height for each element.</summary>
    public static EcgElementParams Defaults(EcgElement element) => element switch
    {
        EcgElement.PWave      => new EcgElementParams(90f, 0.15f),
        EcgElement.QrsComplex => new EcgElementParams(90f, 1.0f),
        EcgElement.TWave      => new EcgElementParams(160f, 0.30f),
        EcgElement.StSegment  => new EcgElementParams(80f, 0.10f),
        EcgElement.Baseline   => new EcgElementParams(120f, 0f),
        _                     => new EcgElementParams(100f, 0.2f),
    };

    /// <summary>
    /// Generates <paramref name="element"/> as baseline-centered ADC samples. The sample count
    /// derives from <c>DurationMs</c> and the calibration's sample rate; amplitude maps mV→ADC via
    /// <c>AdcCountsPerMv</c>. Values are clamped to the ADC range. Returns a single baseline sample
    /// for a non-positive duration.
    /// </summary>
    public static int[] Generate(
        EcgElement element, EcgElementParams parameters, EcgCalibration calibration, int baseline)
    {
        var count = (int)MathF.Round(parameters.DurationMs / 1000f * calibration.SampleRateHz);
        if (count < 1) count = 1;

        var amplitudeAdc = parameters.AmplitudeMv * calibration.AdcCountsPerMv;
        var shape = ShapeFor(element);

        var samples = new int[count];
        for (var i = 0; i < count; i++)
        {
            var t = count == 1 ? 0f : (float)i / (count - 1);
            var value = baseline + shape(t) * amplitudeAdc;
            samples[i] = Math.Clamp((int)MathF.Round(value), AdcMin, AdcMax);
        }
        return samples;
    }

    /// <summary>Normalized morphology: t in [0,1] → amplitude in units of the element's height.</summary>
    private static Func<float, float> ShapeFor(EcgElement element) => element switch
    {
        EcgElement.Baseline   => _ => 0f,
        EcgElement.PWave      => t => MathF.Sin(MathF.PI * t),
        EcgElement.TWave      => t => MathF.Sin(MathF.PI * t),
        EcgElement.StSegment  => _ => 1f,
        EcgElement.QrsComplex => QrsShape,
        _                     => _ => 0f,
    };

    // Normalized QRS: small Q dip, dominant R spike (= full height), S dip, back to the isoline.
    private static readonly (float T, float V)[] QrsPoints =
    {
        (0.00f, 0f), (0.15f, -0.10f), (0.30f, 0f),
        (0.50f, 1.00f), (0.70f, -0.25f), (0.85f, 0f), (1.00f, 0f),
    };

    private static float QrsShape(float t)
    {
        for (var i = 1; i < QrsPoints.Length; i++)
        {
            if (t <= QrsPoints[i].T)
            {
                var (t0, v0) = QrsPoints[i - 1];
                var (t1, v1) = QrsPoints[i];
                var f = t1 > t0 ? (t - t0) / (t1 - t0) : 0f;
                return v0 + (v1 - v0) * f;
            }
        }
        return 0f;
    }
}
