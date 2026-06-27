using System;
using System.Collections.Generic;
using System.Linq;
using BioSPPy.Net.Signals.Tools;

namespace BioSPPy.Net.Signals.Ecg;

public static class QrsSegmenters
{
    public static (int[] extrema, double[] values) FindExtrema(double[] signal, string mode = "both")
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        if (signal.Length < 3) return (Array.Empty<int>(), Array.Empty<double>());

        int n = signal.Length;
        int[] diffSign = new int[n - 2];
        int lastSign = Math.Sign(signal[1] - signal[0]);

        for (int i = 1; i < n - 1; i++)
        {
            int currentSign = Math.Sign(signal[i + 1] - signal[i]);
            diffSign[i - 1] = currentSign - lastSign;
            lastSign = currentSign;
        }

        List<int> extremaList = new List<int>();
        for (int i = 0; i < diffSign.Length; i++)
        {
            if (mode == "both")
            {
                if (Math.Abs(diffSign[i]) > 0) extremaList.Add(i + 1);
            }
            else if (mode == "max")
            {
                if (diffSign[i] < 0) extremaList.Add(i + 1);
            }
            else if (mode == "min")
            {
                if (diffSign[i] > 0) extremaList.Add(i + 1);
            }
        }

        int[] extrema = extremaList.ToArray();
        double[] values = new double[extrema.Length];
        for (int i = 0; i < extrema.Length; i++)
        {
            values[i] = signal[extrema[i]];
        }

        return (extrema, values);
    }

    public static int[] CorrectRPeaks(double[] signal, int[] rpeaks, double samplingRate = 1000.0, double tol = 0.05)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        if (rpeaks == null) throw new ArgumentNullException(nameof(rpeaks));

        int toleranceSamples = (int)(tol * samplingRate);
        int length = signal.Length;
        List<int> newR = new List<int>();

        foreach (int r in rpeaks)
        {
            int a = r - toleranceSamples;
            if (a < 0) continue;
            int b = r + toleranceSamples;
            if (b > length) break;

            // Find argmax of signal[a..b]
            double maxVal = double.MinValue;
            int argmax = a;
            for (int i = a; i < b; i++)
            {
                if (signal[i] > maxVal)
                {
                    maxVal = signal[i];
                    argmax = i;
                }
            }
            newR.Add(argmax);
        }

        return newR.Distinct().OrderBy(x => x).ToArray();
    }

    private static double Median(double[] arr)
    {
        if (arr == null || arr.Length == 0) return 0.0;
        double[] copy = (double[])arr.Clone();
        Array.Sort(copy);
        int mid = copy.Length / 2;
        if (copy.Length % 2 == 0)
        {
            return (copy[mid - 1] + copy[mid]) / 2.0;
        }
        else
        {
            return copy[mid];
        }
    }

    public static int[] HamiltonSegmenter(double[] signal, double samplingRate = 1000.0)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        int length = signal.Length;
        double duration = length / samplingRate;

        int v1s = (int)(1.0 * samplingRate);
        int v100ms = (int)(0.1 * samplingRate);
        int thElapsed = (int)Math.Ceiling(0.36 * samplingRate);
        int smSize = (int)(0.08 * samplingRate);
        int initEcg = 8;
        if (duration < initEcg) initEcg = (int)duration;
        if (initEcg < 1) initEcg = 1;

        // Bandpass filter (25Hz lowpass, 3Hz highpass)
        var lpCoeff = Filtering.Butterworth(4, new double[] { 2.0 * 25.0 / samplingRate }, "lowpass");
        double[] filteredLp = Filtering.FiltFilt(lpCoeff.b, lpCoeff.a, signal);

        var hpCoeff = Filtering.Butterworth(4, new double[] { 2.0 * 3.0 / samplingRate }, "highpass");
        double[] filtered = Filtering.FiltFilt(hpCoeff.b, hpCoeff.a, filteredLp);

        // Absolute derivative scaled by sampling rate
        double[] dx = new double[length - 1];
        for (int i = 0; i < length - 1; i++)
        {
            dx[i] = Math.Abs((filtered[i + 1] - filtered[i]) * samplingRate);
        }

        // Smooth with Hamming window
        dx = Dsp.Smoother(dx, "hamming", smSize, true);

        // Initialize buffers
        double[] qrsPeakBuffer = new double[initEcg];
        double[] noisePeakBuffer = new double[initEcg];
        double[] rrInterval = new double[initEcg];
        for (int i = 0; i < initEcg; i++) rrInterval[i] = samplingRate;

        int a = 0;
        int b = v1s;
        for (int i = 0; i < initEcg; i++)
        {
            if (a >= dx.Length) break;
            int end = Math.Min(b, dx.Length);
            double[] sub = new double[end - a];
            Array.Copy(dx, a, sub, 0, sub.Length);

            var (peaks, values) = FindExtrema(sub, "max");
            if (values.Length > 0)
            {
                int maxIdx = 0;
                double maxVal = values[0];
                for (int j = 1; j < values.Length; j++)
                {
                    if (values[j] > maxVal)
                    {
                        maxVal = values[j];
                        maxIdx = j;
                    }
                }
                qrsPeakBuffer[i] = maxVal;
            }
            a += v1s;
            b += v1s;
        }

        double ANP = Median(noisePeakBuffer);
        double AQRSP = Median(qrsPeakBuffer);
        double TH = 0.475;
        double DT = ANP + TH * (AQRSP - ANP);

        int lim = (int)Math.Ceiling(0.2 * samplingRate);
        int diffNr = (int)Math.Ceiling(0.045 * samplingRate);

        var (allPeaks, _) = FindExtrema(dx, "max");
        List<int> beats = new List<int>();
        int indexQrs = 0;
        int indexNoise = 0;
        int indexRr = 0;
        int npeaks = 0;

        foreach (int f in allPeaks)
        {
            // 1. Is f-peak larger than any neighbor peak within 200 ms?
            bool skipPeak = false;
            foreach (int op in allPeaks)
            {
                if (op != f && op > f - lim && op < f + lim)
                {
                    if (dx[op] > dx[f])
                    {
                        skipPeak = true;
                        break;
                    }
                }
            }
            if (skipPeak) continue;

            double elapsed = 0;
            if (npeaks > 0)
            {
                elapsed = f - beats[npeaks - 1];
            }

            if (dx[f] > DT)
            {
                // Check slopes in raw signal
                int start = Math.Max(0, f - diffNr);
                int end = Math.Min(length, f + diffNr);
                double[] diffNow = new double[end - start - 1];
                int posCount = 0;
                for (int i = 0; i < diffNow.Length; i++)
                {
                    diffNow[i] = signal[start + i + 1] - signal[start + i];
                    if (diffNow[i] > 0) posCount++;
                }

                if (posCount == 0 || posCount == diffNow.Length) continue;

                if (npeaks > 0)
                {
                    if (elapsed < thElapsed)
                    {
                        int pStart = Math.Max(0, beats[npeaks - 1] - diffNr);
                        int pEnd = Math.Min(length, beats[npeaks - 1] + diffNr);
                        double[] diffPrev = new double[pEnd - pStart - 1];
                        for (int i = 0; i < diffPrev.Length; i++)
                        {
                            diffPrev[i] = signal[pStart + i + 1] - signal[pStart + i];
                        }

                        double slopeNow = diffNow.Max();
                        double slopePrev = diffPrev.Max();

                        if (slopeNow < 0.5 * slopePrev) continue; // T-wave
                    }

                    if (dx[f] < 3.0 * Median(qrsPeakBuffer))
                    {
                        beats.Add(f);
                        rrInterval[indexRr] = beats[npeaks] - beats[npeaks - 1];
                        indexRr = (indexRr + 1) % initEcg;
                    }
                    else
                    {
                        continue;
                    }
                }
                else
                {
                    if (dx[f] < 3.0 * Median(qrsPeakBuffer))
                    {
                        beats.Add(f);
                    }
                    else
                    {
                        continue;
                    }
                }

                npeaks++;
                qrsPeakBuffer[indexQrs] = dx[f];
                indexQrs = (indexQrs + 1) % initEcg;
            }
            else
            {
                double RRM = Median(rrInterval);
                if (npeaks >= 2 && elapsed >= 1.5 * RRM && elapsed > thElapsed)
                {
                    if (dx[f] > 0.5 * DT)
                    {
                        beats.Add(f);
                        rrInterval[indexRr] = beats[npeaks] - beats[npeaks - 1];
                        indexRr = (indexRr + 1) % initEcg;

                        npeaks++;
                        qrsPeakBuffer[indexQrs] = dx[f];
                        indexQrs = (indexQrs + 1) % initEcg;
                    }
                }
                else
                {
                    noisePeakBuffer[indexNoise] = dx[f];
                    indexNoise = (indexNoise + 1) % initEcg;
                }
            }

            ANP = Median(noisePeakBuffer);
            AQRSP = Median(qrsPeakBuffer);
            DT = ANP + 0.475 * (AQRSP - ANP);
        }

        List<int> rBeats = new List<int>();
        double adjacency = 0.05 * samplingRate;
        double thresCh = 0.85;

        foreach (int i in beats)
        {
            int start = Math.Max(0, i - lim);
            int end = Math.Min(length, i + lim);
            double[] window = new double[end - start];
            Array.Copy(signal, start, window, 0, window.Length);

            var (wPeaks, wVals) = FindExtrema(window, "max");
            var (wNegPeaks, wNegVals) = FindExtrema(window, "min");

            List<int> peakIndices = new List<int>(wPeaks);
            List<int> negPeakIndices = new List<int>(wNegPeaks);

            for (int k = 0; k < window.Length - 1; k++)
            {
                if (window[k + 1] - window[k] == 0)
                {
                    peakIndices.Add(k);
                    negPeakIndices.Add(k);
                }
            }

            var pospeaks = peakIndices.Select(idx => (val: window[idx], idx)).OrderByDescending(p => p.val).ToList();
            var negpeaks = negPeakIndices.Select(idx => (val: window[idx], idx)).OrderBy(p => p.val).ToList();

            bool errPos = pospeaks.Count == 0;
            bool errNeg = negpeaks.Count == 0;

            List<(double val, int idx)> twopeaks = new List<(double, int)>();
            if (!errPos)
            {
                twopeaks.Add(pospeaks[0]);
                for (int k = 1; k < pospeaks.Count; k++)
                {
                    if (Math.Abs(pospeaks[0].idx - pospeaks[k].idx) > adjacency)
                    {
                        twopeaks.Add(pospeaks[k]);
                        break;
                    }
                }
            }

            List<(double val, int idx)> twonegpeaks = new List<(double, int)>();
            if (!errNeg)
            {
                twonegpeaks.Add(negpeaks[0]);
                for (int k = 1; k < negpeaks.Count; k++)
                {
                    if (Math.Abs(negpeaks[0].idx - negpeaks[k].idx) > adjacency)
                    {
                        twonegpeaks.Add(negpeaks[k]);
                        break;
                    }
                }
            }

            double posdiv = 0.0;
            if (twopeaks.Count >= 2) posdiv = Math.Abs(twopeaks[0].val - twopeaks[1].val);
            else errPos = true;

            double negdiv = 0.0;
            if (twonegpeaks.Count >= 2) negdiv = Math.Abs(twonegpeaks[0].val - twonegpeaks[1].val);
            else errNeg = true;

            int finalOffset = 0;
            if (!errPos && !errNeg)
            {
                if (posdiv > thresCh * negdiv) finalOffset = twopeaks[0].idx;
                else finalOffset = twonegpeaks[0].idx;
            }
            else if (errPos && errNeg)
            {
                if (twopeaks.Count > 0 && twonegpeaks.Count > 0)
                {
                    if (Math.Abs(twopeaks[0].val) > Math.Abs(twonegpeaks[0].val)) finalOffset = twopeaks[0].idx;
                    else finalOffset = twonegpeaks[0].idx;
                }
                else if (twopeaks.Count > 0) finalOffset = twopeaks[0].idx;
                else if (twonegpeaks.Count > 0) finalOffset = twonegpeaks[0].idx;
            }
            else if (errPos)
            {
                if (twopeaks.Count > 0) finalOffset = twopeaks[0].idx;
            }
            else
            {
                if (twonegpeaks.Count > 0) finalOffset = twonegpeaks[0].idx;
            }

            rBeats.Add(finalOffset + start);
        }

        return rBeats.Distinct().OrderBy(x => x).ToArray();
    }

    public static int[] SsfSegmenter(double[] signal, double samplingRate = 1000.0, double threshold = 20, double before = 0.03, double after = 0.01)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        int length = signal.Length;
        int winB = (int)(before * samplingRate);
        int winA = (int)(after * samplingRate);

        double[] dx = new double[length - 1];
        for (int i = 0; i < length - 1; i++)
        {
            double diff = signal[i + 1] - signal[i];
            dx[i] = diff >= 0 ? 0.0 : diff * diff;
        }

        List<int> idxList = new List<int>();
        for (int i = 0; i < dx.Length; i++)
        {
            if (dx[i] > threshold) idxList.Add(i);
        }

        HashSet<int> Rset = new HashSet<int>();
        for (int k = 0; k < idxList.Count; k++)
        {
            int prev = k == 0 ? 0 : idxList[k - 1];
            if (k == 0 && idxList[0] <= 1) continue; // Matches didx[0] = idx[0] - 0 > 1, so idx[0] must be > 1
            if (k > 0 && idxList[k] - prev <= 1) continue;

            int item = idxList[k];
            int a = item - winB;
            if (a < 0) a = 0;
            int b = item + winA;
            if (b > length) continue;

            double maxVal = double.MinValue;
            int r = a;
            for (int i = a; i < b; i++)
            {
                if (signal[i] > maxVal)
                {
                    maxVal = signal[i];
                    r = i;
                }
            }
            Rset.Add(r);
        }

        return Rset.OrderBy(x => x).ToArray();
    }

    public static int[] ChristovSegmenter(double[] signal, double samplingRate = 1000.0)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        int length = signal.Length;

        int v100ms = (int)(0.1 * samplingRate);
        int v50ms = (int)(0.050 * samplingRate);
        int v300ms = (int)(0.300 * samplingRate);
        int v350ms = (int)(0.350 * samplingRate);
        int v200ms = (int)(0.2 * samplingRate);
        int v1200ms = (int)(1.2 * samplingRate);
        double M_th = 0.4;

        // Power-line interference suppression (Moving average of 20 ms)
        int sizePowerline = (int)(0.02 * samplingRate);
        double[] bPowerline = Enumerable.Repeat(1.0 / 50.0, sizePowerline).ToArray(); // Wait, in python it is np.ones(...) / 50.0. For 1000Hz, size is 20, sum is 20/50 = 0.4.
        double[] aPowerline = new double[] { 1.0 };
        double[] X = Filtering.FiltFilt(bPowerline, aPowerline, signal);

        // Electromyogram noise suppression (Moving average of 28.5 ms, 1000 / 35.0 = 28)
        int sizeEmyo = (int)(samplingRate / 35.0);
        double[] bEmyo = Enumerable.Repeat(1.0 / 35.0, sizeEmyo).ToArray();
        X = Filtering.FiltFilt(bEmyo, aPowerline, X);

        // Lowpass Butterworth order 7, 40 Hz
        var lpCoeff = Filtering.Butterworth(7, new double[] { 2.0 * 40.0 / samplingRate }, "lowpass");
        X = Filtering.FiltFilt(lpCoeff.b, lpCoeff.a, X);

        // Highpass Butterworth order 7, 9 Hz
        var hpCoeff = Filtering.Butterworth(7, new double[] { 2.0 * 9.0 / samplingRate }, "highpass");
        X = Filtering.FiltFilt(hpCoeff.b, hpCoeff.a, X);

        int k = 1;
        double[] Y = new double[X.Length - 2 * k];
        for (int n = k + 1; n < X.Length - k; n++)
        {
            double val = X[n] * X[n] - X[n - k] * X[n + k];
            Y[n - k - 1] = val < 0.0 ? 0.0 : val;
        }

        // Complex lead moving average (40 ms)
        int sizeLead = (int)(samplingRate / 25.0);
        double[] bLead = Enumerable.Repeat(1.0 / 25.0, sizeLead).ToArray();
        Y = Filtering.LFilter(bLead, aPowerline, Y, out _);

        // Init MM
        int initSamples = (int)(5 * samplingRate);
        double maxY = Y.Take(Math.Min(initSamples, Y.Length)).Max();
        double[] MM = Enumerable.Repeat(M_th * maxY, 5).ToArray();
        int MMidx = 0;
        double M = MM.Average();

        double[] slope = new double[(int)samplingRate];
        for (int i = 0; i < slope.Length; i++)
        {
            slope[i] = 1.0 - 0.4 * i / (slope.Length - 1);
        }

        double Rdec = 0.0;
        double R = 0.0;
        double[] RR = new double[5];
        int RRidx = 0;
        double Rm = 0.0;
        List<int> QRS = new List<int>();
        List<int> Rpeak = new List<int>();
        int current_sample = 0;
        bool skip = false;
        double F = Y.Take(Math.Min(v350ms, Y.Length)).Average();
        double Mtemp = M;

        while (current_sample < Y.Length)
        {
            if (QRS.Count > 0)
            {
                int lastQrs = QRS[^1];
                if (current_sample <= lastQrs + v200ms)
                {
                    int startIdx = lastQrs;
                    int endIdx = Math.Min(lastQrs + v200ms, Y.Length);
                    double subMax = 0.0;
                    for (int i = startIdx; i < endIdx; i++)
                    {
                        if (Y[i] > subMax) subMax = Y[i];
                    }
                    double Mnew = M_th * subMax;
                    int prevIdx = (MMidx - 1 + 5) % 5;
                    Mnew = Mnew <= 1.5 * MM[prevIdx] ? Mnew : 1.1 * MM[prevIdx];
                    MM[MMidx] = Mnew;
                    MMidx = (MMidx + 1) % 5;
                    Mtemp = MM.Average();
                    M = Mtemp;
                    skip = true;
                }
                else if (current_sample >= lastQrs + v200ms && current_sample < lastQrs + v1200ms)
                {
                    M = Mtemp * slope[current_sample - lastQrs - v200ms];
                }

                if (current_sample >= lastQrs && current_sample < lastQrs + (2.0 / 3.0) * Rm)
                {
                    R = 0;
                }
                else if (current_sample >= lastQrs + (2.0 / 3.0) * Rm && current_sample < lastQrs + Rm)
                {
                    R += Rdec;
                }
            }

            double MFR = M + F + R;
            if (!skip && Y[current_sample] >= MFR)
            {
                QRS.Add(current_sample);
                int startArg = current_sample;
                int endArg = Math.Min(current_sample + v300ms, Y.Length);
                double maxVal = double.MinValue;
                int argmax = startArg;
                for (int i = startArg; i < endArg; i++)
                {
                    if (Y[i] > maxVal)
                    {
                        maxVal = Y[i];
                        argmax = i;
                    }
                }
                Rpeak.Add(argmax);

                if (QRS.Count >= 2)
                {
                    RR[RRidx] = QRS[^1] - QRS[^2];
                    RRidx = (RRidx + 1) % 5;
                }
            }
            skip = false;

            if (current_sample >= v350ms)
            {
                double maxLatest = double.MinValue;
                for (int i = current_sample - v50ms; i < current_sample; i++)
                {
                    if (Y[i] > maxLatest) maxLatest = Y[i];
                }
                double maxEarliest = double.MinValue;
                for (int i = current_sample - v350ms; i < current_sample - v300ms; i++)
                {
                    if (Y[i] > maxEarliest) maxEarliest = Y[i];
                }
                F += (maxLatest - maxEarliest) / 1000.0;
            }

            Rm = RR.Average();
            current_sample++;
        }

        List<int> rpeaks = new List<int>();
        foreach (int i in Rpeak)
        {
            int start = Math.Max(0, i - v100ms);
            int end = Math.Min(length, i + v100ms);
            double maxVal = double.MinValue;
            int argmax = start;
            for (int m = start; m < end; m++)
            {
                if (signal[m] > maxVal)
                {
                    maxVal = signal[m];
                    argmax = m;
                }
            }
            rpeaks.Add(argmax);
        }

        return rpeaks.Distinct().OrderBy(x => x).ToArray();
    }

    public static int[] EngzeeSegmenter(double[] signal, double samplingRate = 1000.0, double threshold = 0.48)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        int length = signal.Length;

        int changeM = (int)(0.75 * samplingRate);
        int Miterate = (int)(1.75 * samplingRate);
        int v250ms = (int)(0.25 * samplingRate);
        int v1200ms = (int)(1.2 * samplingRate);
        int v1500ms = (int)(1.5 * samplingRate);
        int v180ms = (int)(0.18 * samplingRate);
        int p10ms = (int)Math.Ceiling(0.01 * samplingRate);
        int p20ms = (int)Math.Ceiling(0.02 * samplingRate);
        int err_kill = (int)(0.01 * samplingRate);
        int inc = 1;
        double mmth = threshold;
        double mmp = 0.2;

        // Differentiator: y1[i] = signal[i+4] - signal[i]
        double[] y1 = new double[length - 4];
        for (int i = 0; i < y1.Length; i++)
        {
            y1[i] = signal[i + 4] - signal[i];
        }

        // Lowpass: y2 = sum c * y1
        double[] c = new double[] { 1, 4, 6, 4, 1, -1, -4, -6, -4, -1 };
        double[] y2 = new double[y1.Length - 9];
        for (int n = 9; n < y1.Length; n++)
        {
            double dot = 0.0;
            for (int j = 0; j < 10; j++)
            {
                dot += c[j] * y1[n - 9 + j];
            }
            y2[n - 9] = dot;
        }

        int y2_len = y2.Length;
        if (y2_len < Miterate) return Array.Empty<int>();

        double maxY2Init = y2.Take(Miterate).Max();
        double minY2Init = y2.Take(Miterate).Min();

        double[] MM = Enumerable.Repeat(mmth * maxY2Init, 3).ToArray();
        int MMidx = 0;
        double Th = MM.Average();

        double[] NN = Enumerable.Repeat(mmp * minY2Init, 2).ToArray();
        int NNidx = 0;
        double ThNew = NN.Average();

        bool update = false;
        List<int> nthfpluss = new List<int>();
        List<int> rpeaks = new List<int>();

        while (true)
        {
            if (update)
            {
                double Mnew = 0.0;
                double Nnew = 0.0;
                if (inc * changeM + Miterate < y2_len)
                {
                    int a = (inc - 1) * changeM;
                    int b = inc * changeM + Miterate;
                    double maxSub = double.MinValue;
                    double minSub = double.MaxValue;
                    for (int j = a; j < b; j++)
                    {
                        if (y2[j] > maxSub) maxSub = y2[j];
                        if (y2[j] < minSub) minSub = y2[j];
                    }
                    Mnew = mmth * maxSub;
                    Nnew = mmp * minSub;
                }
                else if (y2_len - (inc - 1) * changeM > v1500ms)
                {
                    int a = (inc - 1) * changeM;
                    double maxSub = double.MinValue;
                    double minSub = double.MaxValue;
                    for (int j = a; j < y2_len; j++)
                    {
                        if (y2[j] > maxSub) maxSub = y2[j];
                        if (y2[j] < minSub) minSub = y2[j];
                    }
                    Mnew = mmth * maxSub;
                    Nnew = mmp * minSub;
                }

                if (y2_len - inc * changeM > Miterate)
                {
                    int prevMMIdx = (MMidx - 1 + 3) % 3;
                    MM[MMidx] = Mnew <= 1.5 * MM[prevMMIdx] ? Mnew : 1.1 * MM[prevMMIdx];
                    int prevNNIdx = (NNidx - 1 + 2) % 2;
                    NN[NNidx] = Math.Abs(Nnew) <= 1.5 * Math.Abs(NN[prevNNIdx]) ? Nnew : 1.1 * NN[prevNNIdx];
                }

                MMidx = (MMidx + 1) % 3;
                NNidx = (NNidx + 1) % 2;
                Th = MM.Average();
                ThNew = NN.Average();
                inc++;
                update = false;
            }

            int nthfplus = -1;
            if (nthfpluss.Count > 0)
            {
                int lastp = nthfpluss[^1] + 1;
                if (lastp < (inc - 1) * changeM)
                {
                    lastp = (inc - 1) * changeM;
                }
                int limit = inc * changeM + err_kill;
                if (limit > y2_len) limit = y2_len;

                // Find downward crossing of Th
                for (int j = lastp; j < limit - 1; j++)
                {
                    if (y2[j] > Th && y2[j + 1] < Th)
                    {
                        nthfplus = j;
                        break;
                    }
                }

                if (nthfplus == -1)
                {
                    if (inc * changeM > y2_len) break;
                    else
                    {
                        update = true;
                        continue;
                    }
                }

                if (rpeaks.Count > 0)
                {
                    int diff = nthfplus - rpeaks[^1];
                    if (diff > v250ms && diff < v1200ms)
                    {
                        // Pass
                    }
                    else if (diff < v250ms)
                    {
                        nthfpluss.Add(nthfplus);
                        continue;
                    }
                }
            }
            else
            {
                int start = (inc - 1) * changeM;
                int limit = inc * changeM + err_kill;
                if (limit > y2_len) limit = y2_len;

                for (int j = start; j < limit - 1; j++)
                {
                    if (y2[j] > Th && y2[j + 1] < Th)
                    {
                        nthfplus = j;
                        break;
                    }
                }

                if (nthfplus == -1)
                {
                    if (inc * changeM > y2_len) break;
                    else
                    {
                        update = true;
                        continue;
                    }
                }
            }

            nthfpluss.Add(nthfplus);

            // Define 180ms search region
            int iIdx = nthfplus;
            int fIdx = nthfplus + v180ms;
            if (fIdx > y2_len) fIdx = y2_len;

            // Check if signal remains below ThNew for p10ms samples
            int consecutivePoints = 0;
            for (int k = iIdx; k < fIdx; k++)
            {
                if (y2[k] < ThNew)
                {
                    consecutivePoints++;
                    if (consecutivePoints >= p10ms)
                    {
                        int maxShift = p20ms;
                        if (nthfplus > maxShift)
                        {
                            int startArg = iIdx - maxShift;
                            double maxVal = double.MinValue;
                            int argmax = startArg;
                            for (int m = startArg; m < fIdx; m++)
                            {
                                if (signal[m] > maxVal)
                                {
                                    maxVal = signal[m];
                                    argmax = m;
                                }
                            }
                            rpeaks.Add(argmax);
                        }
                        else
                        {
                            double maxVal = double.MinValue;
                            int argmax = iIdx;
                            for (int m = iIdx; m < fIdx; m++)
                            {
                                if (signal[m] > maxVal)
                                {
                                    maxVal = signal[m];
                                    argmax = m;
                                }
                            }
                            rpeaks.Add(argmax);
                        }
                        break;
                    }
                }
                else
                {
                    consecutivePoints = 0;
                }
            }
        }

        return rpeaks.Distinct().OrderBy(x => x).ToArray();
    }

    public static int[] GamboaSegmenter(double[] signal, double samplingRate = 1000.0, double tol = 0.002)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        int length = signal.Length;

        int v_100ms = (int)(0.1 * samplingRate);
        int v_300ms = (int)(0.3 * samplingRate);

        // Compute histogram counts and edges (100 bins)
        double minVal = signal.Min();
        double maxVal = signal.Max();
        double bw = (maxVal - minVal) / 100.0;
        double[] edges = new double[101];
        for (int j = 0; j <= 100; j++) edges[j] = minVal + j * bw;

        int[] counts = new int[100];
        foreach (double val in signal)
        {
            if (val < minVal || val > maxVal) continue;
            int bin = -1;
            if (val == maxVal)
            {
                bin = 99;
            }
            else
            {
                int low = 0;
                int high = 99;
                while (low <= high)
                {
                    int mid = (low + high) / 2;
                    if (val >= edges[mid] && val < edges[mid + 1])
                    {
                        bin = mid;
                        break;
                    }
                    else if (val < edges[mid])
                    {
                        high = mid - 1;
                    }
                    else
                    {
                        low = mid + 1;
                    }
                }
            }
            if (bin != -1)
            {
                counts[bin]++;
            }
        }

        double[] F = new double[100];
        double sum = 0.0;
        for (int j = 0; j < 100; j++)
        {
            sum += counts[j] / (double)(length * bw); // Matches density=True
            F[j] = sum;
        }

        double TH = 0.01;
        double v0 = minVal;
        for (int j = 0; j < 100; j++)
        {
            if (F[j] > TH)
            {
                v0 = edges[j];
                break;
            }
        }

        double v1 = maxVal;
        for (int j = 99; j >= 0; j--)
        {
            if (F[j] < (1.0 - TH))
            {
                v1 = edges[j]; // Note: index j is same as np.nonzero[0][-1]
                break;
            }
        }

        double nrm = Math.Max(Math.Abs(v0), Math.Abs(v1));
        double[] normSignal = signal.Select(x => x / nrm).ToArray();

        // 2nd difference: d2[i] = norm[i+2] - 2*norm[i+1] + norm[i]
        double[] d2 = new double[length - 2];
        for (int i = 0; i < length - 2; i++)
        {
            d2[i] = normSignal[i + 2] - 2 * normSignal[i + 1] + normSignal[i];
        }

        // Find local maxima of z = -d2 where value > tol
        // Python: b = np.nonzero((np.diff(np.sign(np.diff(-d2)))) == -2)[0] + 2
        // As analyzed, index in b is k + 1 where k is a peak in z = -d2
        // Check if -d2[k+1] > tol
        List<double> bList = new List<double>();
        double[] z = d2.Select(x => -x).ToArray();

        for (int k = 1; k < z.Length - 1; k++)
        {
            if (z[k] > z[k - 1] && z[k] > z[k + 1])
            {
                if (z[k + 1] > tol)
                {
                    bList.Add(k + 1);
                }
            }
        }

        List<int> rpeaks = new List<int>();
        if (bList.Count >= 3)
        {
            double previous = bList[0];
            for (int k = 1; k < bList.Count; k++)
            {
                double iVal = bList[k];
                if (iVal - previous > v_300ms)
                {
                    previous = iVal;
                    int startArg = (int)iVal;
                    int endArg = Math.Min(length, (int)(iVal + v_100ms));
                    double maxSub = double.MinValue;
                    int argmax = startArg;
                    for (int m = startArg; m < endArg; m++)
                    {
                        if (signal[m] > maxSub)
                        {
                            maxSub = signal[m];
                            argmax = m;
                        }
                    }
                    rpeaks.Add(argmax);
                }
            }
        }

        return rpeaks.Distinct().OrderBy(x => x).ToArray();
    }

    public static int[] AsiSegmenter(double[] signal, double samplingRate = 1000.0, double Pth = 5.0)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        int length = signal.Length;

        int N = (int)Math.Round(3 * samplingRate / 128);
        int Nd = N - 1;
        double Rmin = 0.26;

        double[] diffEcg = new double[length - Nd];
        for (int idx = 0; idx < diffEcg.Length; idx++)
        {
            diffEcg[idx] = signal[idx + Nd] - signal[idx];
        }

        double[] ddiffEcg = new double[diffEcg.Length - 1];
        for (int idx = 0; idx < ddiffEcg.Length; idx++)
        {
            ddiffEcg[idx] = diffEcg[idx + 1] - diffEcg[idx];
        }

        double[] squar = ddiffEcg.Select(x => x * x).ToArray();

        // Integrate moving window (lfilter b=ones(N), a=1)
        double[] b = Enumerable.Repeat(1.0, N).ToArray();
        double[] aCoeff = new double[] { 1.0 };
        double[] processedEcg = Filtering.LFilter(b, aCoeff, squar, out _);

        List<int> rpeaks = new List<int>();
        int i = 1;
        double Ramptotal = 0.0;

        while (i < length - samplingRate)
        {
            int tf1 = (int)Math.Round(i + Rmin * samplingRate);
            double Rpeakamp = 0.0;
            int rpeakpos = 0;

            while (i < tf1 && i < processedEcg.Length)
            {
                if (processedEcg[i] > Rpeakamp)
                {
                    Rpeakamp = processedEcg[i];
                    rpeakpos = i + 1;
                }
                i++;
            }

            Ramptotal = (19.0 / 20.0) * Ramptotal + (1.0 / 20.0) * Rpeakamp;
            rpeaks.Add(rpeakpos);

            int d = tf1 - rpeakpos;
            int tf2 = i + (int)Math.Round(0.2 * 250 - d);

            while (i <= tf2)
            {
                i++;
            }

            double Thr = Ramptotal;
            while (i < processedEcg.Length && processedEcg[i] < Thr)
            {
                Thr *= Math.Exp(-Pth / samplingRate);
                i++;
            }
        }

        return rpeaks.Distinct().OrderBy(x => x).ToArray();
    }
}
