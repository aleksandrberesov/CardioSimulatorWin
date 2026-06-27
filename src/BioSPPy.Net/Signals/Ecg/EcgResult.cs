namespace BioSPPy.Net.Signals.Ecg;

public record EcgResult(
    double[] TimeAxis,         // ts (seconds)
    double[] Filtered,         // filtered signal
    int[] RPeaks,              // corrected R-peak indices
    double[] TemplatesTime,    // templates time axis relative to R-peak
    double[,] Templates,       // 2D array [heartbeats, samples] of templates
    double[] HeartRateTime,    // heart rate time axis reference
    double[] HeartRate         // instantaneous heart rate (bpm)
);
