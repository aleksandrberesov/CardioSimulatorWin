namespace CardioSimulator.Core.Data;

/// <summary>
/// Physical ECG calibration constants. Treated as ground truth when mapping
/// raw ADC samples to standard ECG paper coordinates (mm/mV, mm/s).
/// </summary>
public sealed record EcgCalibration(
    float GainMmPerMv = 10f,
    float SampleRateHz = 500f,
    float AdcCountsPerMv = 256f);
