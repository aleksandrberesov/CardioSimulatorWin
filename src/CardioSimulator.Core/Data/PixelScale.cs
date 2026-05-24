namespace CardioSimulator.Core.Data;

/// <summary>
/// Display-side scaling derived from pixel density and <see cref="EcgCalibration"/>.
/// <c>PxPerMm</c> is the single anchor: every other value is derived from it,
/// which keeps the paper grid and the waveform in the same coordinate system.
/// </summary>
public sealed record PixelScale(
    float PxPerMm,
    float PaperSpeedMmPerSec,
    float GainZoomY,
    EcgCalibration Cal)
{
    public float PxPerMv => Cal.GainMmPerMv * PxPerMm * GainZoomY;
    public float PxPerSec => PaperSpeedMmPerSec * PxPerMm;
    public float PxPerSample => PxPerSec / Cal.SampleRateHz;
    public float PxPerAdcCount => PxPerMv / Cal.AdcCountsPerMv;
    public float SmallGridStepPx => PxPerMm;
    public float LargeGridStepPx => PxPerMm * 5f;
}
