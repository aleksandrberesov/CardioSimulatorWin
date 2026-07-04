using System;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// Electrical-axis (ЭОС) classification bands. Ranges follow the customer's teaching definitions
/// (partly unconventional / overlapping) — see <see cref="EosAxis.Classify"/>.
/// </summary>
public enum EosAxisClass
{
    Normal,
    Horizontal,
    Vertical,
    LeftDeviation,
    RightDeviation,
    ExtremeDeviation,
}

/// <summary>
/// One lead's QRS deflection magnitudes on standard ECG paper (mm), and the net QRS height used as
/// that lead's projection: <c>R - (q + S)</c>. <see cref="QMm"/>/<see cref="SMm"/> are depths
/// (positive magnitudes of the downward q and S waves); <see cref="RMm"/> is the R height.
/// </summary>
public sealed record EosLeadMeasure(double QMm, double RMm, double SMm)
{
    /// <summary>Net QRS height = R minus the q and S depths (may be negative).</summary>
    public double NetMm => RMm - (QMm + SMm);
}

/// <summary>Outcome of the electrical-axis determination from leads I and aVF.</summary>
public sealed record EosResult(
    EosLeadMeasure LeadI,
    EosLeadMeasure LeadAvf,
    double AngleDeg,
    EosAxisClass AxisClass);

/// <summary>An inclusive sample range within a lead's waveform (a QRS complex, for highlighting).</summary>
public sealed record EcgSpan(int StartSample, int EndSample);

/// <summary>
/// Electrical-axis (α angle) math: projects the net QRS of leads I and aVF onto the hexaxial frame
/// (lead I at 0°, aVF at +90° / inferior) and classifies the resulting angle. Pure and unit-tested;
/// the waveform analysis that measures q/R/S lives in the app layer (BioSPPy QRS detection).
/// </summary>
public static class EosAxis
{
    /// <summary>
    /// The α angle in degrees, normalized to (-180, 180]. Lead I's net QRS is the x-projection
    /// (0° axis) and aVF's is the y-projection (+90°, pointing to the inferior/bottom of the frame).
    /// </summary>
    public static double AngleDegrees(double netI, double netAvf) =>
        Math.Atan2(netAvf, netI) * 180.0 / Math.PI;

    /// <summary>
    /// Classifies an α angle into the customer's six bands: Horizontal [0,29], Normal [30,69],
    /// Vertical [70,90], Right deviation (90,180], Left deviation (-90,0), Extreme deviation
    /// [-180,-90]. The full circle is partitioned so every angle maps to exactly one band.
    /// </summary>
    public static EosAxisClass Classify(double angleDeg)
    {
        var a = angleDeg;
        while (a > 180) a -= 360;
        while (a <= -180) a += 360;

        if (a >= 0 && a <= 29) return EosAxisClass.Horizontal;
        if (a >= 30 && a <= 69) return EosAxisClass.Normal;
        if (a >= 70 && a <= 90) return EosAxisClass.Vertical;
        if (a > 90) return EosAxisClass.RightDeviation;      // (90, 180]
        if (a > -90) return EosAxisClass.LeftDeviation;      // (-90, 0)
        return EosAxisClass.ExtremeDeviation;                // [-180, -90]
    }

    /// <summary>Builds a full <see cref="EosResult"/> from the two lead measurements.</summary>
    public static EosResult From(EosLeadMeasure leadI, EosLeadMeasure leadAvf)
    {
        var angle = AngleDegrees(leadI.NetMm, leadAvf.NetMm);
        return new EosResult(leadI, leadAvf, angle, Classify(angle));
    }
}
