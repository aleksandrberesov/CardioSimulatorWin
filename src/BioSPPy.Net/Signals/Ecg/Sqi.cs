using System;
using System.Collections.Generic;
using System.Linq;
using BioSPPy.Net.Signals.Tools;

namespace BioSPPy.Net.Signals.Ecg;

public static class Sqi
{
    public static double BSQI(int[] detector1, int[] detector2, double fs = 1000.0, string mode = "simple", double searchWindowMs = 150.0)
    {
        if (detector1 == null || detector2 == null) throw new ArgumentNullException("Input Error, check detectors outputs");
        if (detector1.Length == 0 || detector2.Length == 0) return 0.0;

        int searchWindow = (int)(searchWindowMs / 1000.0 * fs);
        int both = 0;
        HashSet<int> det2Set = new HashSet<int>(detector2);

        foreach (int i in detector1)
        {
            int start = Math.Max(0, i - searchWindow);
            int end = i + searchWindow;
            for (int j = start; j < end; j++)
            {
                if (det2Set.Contains(j))
                {
                    both++;
                    break;
                }
            }
        }

        if (mode.Equals("simple", StringComparison.OrdinalIgnoreCase))
        {
            return ((double)both / detector1.Length) * 100.0;
        }
        else if (mode.Equals("matching", StringComparison.OrdinalIgnoreCase))
        {
            return (2.0 * both) / (detector1.Length + detector2.Length);
        }
        else if (mode.Equals("n_double", StringComparison.OrdinalIgnoreCase))
        {
            return (double)both / (detector1.Length + detector2.Length - both);
        }

        return 0.0;
    }

    public static double SSQI(double[] signal)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        if (signal.Length == 0) return 0.0;

        double mean = signal.Average();
        double m2 = 0.0;
        double m3 = 0.0;

        for (int i = 0; i < signal.Length; i++)
        {
            double diff = signal[i] - mean;
            m2 += diff * diff;
            m3 += diff * diff * diff;
        }

        m2 /= signal.Length;
        m3 /= signal.Length;

        if (m2 < 1e-15) return 0.0;
        return m3 / Math.Pow(m2, 1.5);
    }

    public static double KSQI(double[] signal, bool fisher = true)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        if (signal.Length == 0) return 0.0;

        double mean = signal.Average();
        double m2 = 0.0;
        double m4 = 0.0;

        for (int i = 0; i < signal.Length; i++)
        {
            double diff = signal[i] - mean;
            double diffSq = diff * diff;
            m2 += diffSq;
            m4 += diffSq * diffSq;
        }

        m2 /= signal.Length;
        m4 /= signal.Length;

        if (m2 < 1e-15) return 0.0;
        double kurt = m4 / (m2 * m2);
        return fisher ? kurt - 3.0 : kurt;
    }

    public static double PSQI(double[] signal, double f_thr = 0.01)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        if (signal.Length < 2) return 0.0;

        int flatlineCount = 0;
        int diffLength = signal.Length - 1;

        for (int i = 0; i < diffLength; i++)
        {
            if (Math.Abs(signal[i + 1] - signal[i]) < f_thr)
            {
                flatlineCount++;
            }
        }

        return ((double)flatlineCount / diffLength) * 100.0;
    }

    public static double FSQI(double[] ecgSignal, double fs = 1000.0, int nseg = 1024, double[]? numSpectrum = null, double[]? demSpectrum = null, string mode = "simple")
    {
        if (ecgSignal == null) throw new ArgumentNullException(nameof(ecgSignal));
        if (numSpectrum == null) numSpectrum = new double[] { 5.0, 20.0 };

        var (f, Pxx_den) = Dsp.Welch(ecgSignal, fs, nseg);
        if (f.Length == 0) return 0.0;

        double PowerInRange(double[] range)
        {
            List<double> ySub = new List<double>();
            List<double> xSub = new List<double>();
            for (int i = 0; i < f.Length; i++)
            {
                if (f[i] >= range[0] && f[i] <= range[1])
                {
                    ySub.Add(Pxx_den[i]);
                    xSub.Add(f[i]);
                }
            }
            return Dsp.IntegrateTrapz(ySub.ToArray(), xSub.ToArray());
        }

        double numPower = PowerInRange(numSpectrum);
        double demPower = demSpectrum == null 
            ? PowerInRange(new double[] { 0.0, fs / 2.0 }) 
            : PowerInRange(demSpectrum);

        if (Math.Abs(demPower) < 1e-15) return 0.0;

        double ratio = numPower / demPower;
        return mode.Equals("bas", StringComparison.OrdinalIgnoreCase) ? 1.0 - ratio : ratio;
    }

    public static string ZZ2018(double[] signal, int[] detector1, int[] detector2, double fs = 1000.0, double searchWindowMs = 100.0, int nseg = 1024, string mode = "simple")
    {
        if (detector1 == null || detector2 == null || detector1.Length == 0 || detector2.Length == 0)
        {
            return "Unacceptable";
        }

        double qsqi = BSQI(detector1, detector2, fs, "matching", searchWindowMs);
        double psqi = FSQI(signal, fs, nseg, new double[] { 5.0, 15.0 }, new double[] { 5.0, 40.0 });
        double ksqi = KSQI(signal);
        double bassqi = FSQI(signal, fs, nseg, new double[] { 0.0, 1.0 }, new double[] { 0.0, 40.0 }, "bas");

        if (mode.Equals("simple", StringComparison.OrdinalIgnoreCase))
        {
            int qsqiClass = 0;
            if (qsqi > 0.90) qsqiClass = 2;
            else if (qsqi < 0.60) qsqiClass = 0;
            else qsqiClass = 1;

            double RRMax = 1.0;
            if (detector1.Length > 1)
            {
                double minDiff = double.MaxValue;
                for (int i = 0; i < detector1.Length - 1; i++)
                {
                    double diff = detector1[i + 1] - detector1[i];
                    if (diff < minDiff) minDiff = diff;
                }
                RRMax = 60000.0 / (1000.0 / fs * minDiff);
            }

            double l1, l2, l3;
            if (RRMax < 130.0)
            {
                l1 = 0.5; l2 = 0.8; l3 = 0.4;
            }
            else
            {
                l1 = 0.4; l2 = 0.7; l3 = 0.3;
            }

            int pSQIClass = 0;
            if (psqi > l1 && psqi < l2) pSQIClass = 2;
            else if (psqi > l3 && psqi < l1) pSQIClass = 1;
            else pSQIClass = 0;

            int kSQIClass = ksqi > 5.0 ? 2 : 0;

            int basSQIClass = 0;
            if (bassqi >= 0.95) basSQIClass = 2;
            else if (bassqi < 0.9) basSQIClass = 0;
            else basSQIClass = 1;

            int[] classMatrix = new int[] { qsqiClass, pSQIClass, kSQIClass, basSQIClass };
            int nOptimal = classMatrix.Count(c => c == 2);
            int nSuspics = classMatrix.Count(c => c == 1);
            int nUnqualy = classMatrix.Count(c => c == 0);

            if (nUnqualy >= 3 || (nUnqualy == 2 && nSuspics >= 1) || (nUnqualy == 1 && nSuspics == 3))
            {
                return "Unacceptable";
            }
            else if (nOptimal >= 3 && nUnqualy == 0)
            {
                return "Excellent";
            }
            else
            {
                return "Barely acceptable";
            }
        }
        else if (mode.Equals("fuzzy", StringComparison.OrdinalIgnoreCase))
        {
            double qsqiScaled = qsqi * 100.0;
            double UqH = 0.0;
            if (qsqiScaled <= 80.0) UqH = 0.0;
            else if (qsqiScaled >= 90.0) UqH = qsqiScaled / 100.0;
            else UqH = 1.0 / (1.0 + (1.0 / Math.Pow(0.3 * (qsqiScaled - 80.0), 2.0)));

            double UqI = 1.0 / (1.0 + Math.Pow((qsqiScaled - 75.0) / 7.5, 2.0));

            double UqJ = 0.0;
            if (qsqiScaled <= 55.0) UqJ = 1.0;
            else UqJ = 1.0 / (1.0 + Math.Pow((qsqiScaled - 55.0) / 5.0, 2.0));

            double[] R1 = new double[] { UqH, UqI, UqJ };

            // pSQI fuzzy
            double UpH = 0.0;
            if (psqi <= 0.25) UpH = 0.0;
            else if (psqi >= 0.35) UpH = 1.0;
            else UpH = 0.1 * (psqi - 0.25); // typo from python matched exactly

            double UpI = 0.0;
            if (psqi < 0.18) UpI = 0.0;
            else if (psqi >= 0.32) UpI = 0.0;
            else if (psqi >= 0.18 && psqi < 0.22) UpI = 25.0 * (psqi - 0.18);
            else if (psqi >= 0.22 && psqi < 0.28) UpI = 1.0;
            else UpI = 25.0 * (0.32 - psqi);

            double UpJ = 0.0;
            if (psqi < 0.15) UpJ = 1.0;
            else if (psqi > 0.25) UpJ = 0.0;
            else UpJ = 0.1 * (0.25 - psqi); // typo from python matched exactly

            double[] R2 = new double[] { UpH, UpI, UpJ };

            // kSQI fuzzy
            double[] R3 = ksqi > 5.0 ? new double[] { 1.0, 0.0, 0.0 } : new double[] { 0.0, 0.0, 1.0 };

            // basSQI fuzzy
            double UbH = 0.0;
            if (bassqi <= 90.0) UbH = 0.0;
            else if (bassqi >= 95.0) UbH = bassqi / 100.0;
            else UbH = 1.0 / (1.0 + (1.0 / Math.Pow(0.8718 * (bassqi - 90.0), 2.0)));

            double UbI = 0.0;
            if (bassqi <= 85.0) UbI = 1.0;
            else UbI = 1.0 / (1.0 + Math.Pow((bassqi - 85.0) / 5.0, 2.0));

            double UbJ = 1.0 / (1.0 + Math.Pow((bassqi - 95.0) / 2.5, 2.0));

            double[] R4 = new double[] { UbH, UbI, UbJ };

            double[] W = new double[] { 0.4, 0.4, 0.1, 0.1 };
            double[] S = new double[3];
            for (int col = 0; col < 3; col++)
            {
                S[col] = R1[col] * W[0] + R2[col] * W[1] + R3[col] * W[2] + R4[col] * W[3];
            }

            double sumSq = S[0] * S[0] + S[1] * S[1] + S[2] * S[2];
            double V = 1.0;
            if (sumSq > 1e-15)
            {
                V = (S[0] * S[0] * 1.0 + S[1] * S[1] * 2.0 + S[2] * S[2] * 3.0) / sumSq;
            }

            if (V < 1.5) return "Excellent";
            else if (V >= 2.40) return "Unnacceptable"; // Replicated spelling error from python
            else return "Barely acceptable";
        }

        return "Unacceptable";
    }
}
