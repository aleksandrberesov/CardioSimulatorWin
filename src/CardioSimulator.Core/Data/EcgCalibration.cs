namespace CardioSimulator.Core.Data;

/// <summary>
/// Physical ECG calibration constants. Treated as ground truth when mapping
/// raw ADC samples to standard ECG paper coordinates (mm/mV, mm/s).
/// </summary>
/// <remarks>
/// Samples span the 0..2048 ADC range (<see cref="Domain.EcgElementGenerator.AdcMin"/>/
/// <c>AdcMax</c>) centered on baseline 1024. At 1024 counts/mV that is ±1 mV full scale;
/// raising this constant shrinks every waveform's height against the grid (each mV spans
/// fewer pixels), lowering it makes traces taller.
/// </remarks>
public sealed record EcgCalibration(
    float GainMmPerMv = 10f,
    float SampleRateHz = 500f,
    float AdcCountsPerMv = 1024f);
