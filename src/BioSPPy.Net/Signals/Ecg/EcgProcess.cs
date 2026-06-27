using System;
using System.Collections.Generic;
using System.Linq;
using BioSPPy.Net.Signals.Tools;

namespace BioSPPy.Net.Signals.Ecg;

public static class EcgProcess
{
    public static (double[,] templates, int[] newR) ExtractHeartbeats(
        double[] signal,
        int[] rpeaks,
        double samplingRate = 1000.0,
        double before = 0.2,
        double after = 0.4)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        if (rpeaks == null) throw new ArgumentNullException(nameof(rpeaks));

        int beforeSamples = (int)(before * samplingRate);
        int afterSamples = (int)(after * samplingRate);
        int winSize = beforeSamples + afterSamples;

        List<double[]> list = new List<double[]>();
        List<int> validR = new List<int>();

        int[] sortedR = (int[])rpeaks.Clone();
        Array.Sort(sortedR);

        foreach (int r in sortedR)
        {
            int a = r - beforeSamples;
            if (a < 0) continue;
            int b = r + afterSamples;
            if (b > signal.Length) break;

            double[] template = new double[winSize];
            Array.Copy(signal, a, template, 0, winSize);
            list.Add(template);
            validR.Add(r);
        }

        double[,] templates = new double[list.Count, winSize];
        for (int i = 0; i < list.Count; i++)
        {
            for (int j = 0; j < winSize; j++)
            {
                templates[i, j] = list[i][j];
            }
        }

        return (templates, validR.ToArray());
    }

    public static EcgResult Process(double[] signal, double samplingRate = 1000.0)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));

        // 1. Filter signal
        int order = (int)(0.3 * samplingRate);
        if (order % 2 == 0) order++; // enforce odd for FIR filter

        var filterRes = Filtering.FilterSignal(
            signal,
            ftype: "FIR",
            band: "bandpass",
            order: order,
            frequency: new double[] { 3.0, 45.0 },
            sampling_rate: samplingRate
        );
        double[] filtered = filterRes.signal;

        // 2. Segment (Hamilton segmenter)
        int[] rpeaks = QrsSegmenters.HamiltonSegmenter(filtered, samplingRate);

        // 3. Correct R-peaks
        rpeaks = QrsSegmenters.CorrectRPeaks(filtered, rpeaks, samplingRate, tol: 0.05);

        // 4. Extract templates
        double before = 0.2;
        double after = 0.4;
        var (templates, validR) = ExtractHeartbeats(filtered, rpeaks, samplingRate, before, after);

        // 5. Compute heart rate
        var (hrIndex, hr) = Dsp.GetHeartRate(validR, samplingRate, smooth: true, size: 3);

        // 6. Construct time axes
        double[] timeAxis = new double[signal.Length];
        for (int i = 0; i < signal.Length; i++)
        {
            timeAxis[i] = i / samplingRate;
        }

        int beforeSamples = (int)(before * samplingRate);
        int afterSamples = (int)(after * samplingRate);
        int winSize = beforeSamples + afterSamples;
        double[] templatesTime = new double[winSize];
        for (int i = 0; i < winSize; i++)
        {
            templatesTime[i] = -before + (double)i / samplingRate;
        }

        double[] heartRateTime = new double[hrIndex.Length];
        for (int i = 0; i < hrIndex.Length; i++)
        {
            heartRateTime[i] = hrIndex[i] / samplingRate;
        }

        return new EcgResult(
            TimeAxis: timeAxis,
            Filtered: filtered,
            RPeaks: validR,
            TemplatesTime: templatesTime,
            Templates: templates,
            HeartRateTime: heartRateTime,
            HeartRate: hr
        );
    }
}
