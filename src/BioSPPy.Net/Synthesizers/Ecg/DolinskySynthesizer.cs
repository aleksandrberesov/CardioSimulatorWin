using System;
using System.Collections.Generic;
using System.Linq;
using BioSPPy.Net.Signals.Tools;

namespace BioSPPy.Net.Synthesizers.Ecg;

public static class DolinskySynthesizer
{
    private static readonly Random _random = new Random();

    private static double NextNormal(double mean, double std)
    {
        double u1 = 1.0 - _random.NextDouble();
        double u2 = 1.0 - _random.NextDouble();
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return mean + std * randStdNormal;
    }

    private static double Clip(double val, double min, double max)
    {
        if (val < min) return min;
        if (val > max) return max;
        return val;
    }

    private static List<double> RangeList(double start, double end, double step)
    {
        List<double> list = new List<double>();
        for (double val = start; val < end - 1e-9; val += step)
        {
            list.Add(val);
        }
        return list;
    }

    public static double[] B(double l, int Kb)
    {
        if (Kb > 130)
        {
            throw new ArgumentException("Warning! Kb is out of boundaries.");
        }
        int size = (int)(Kb * l);
        return new double[size];
    }

    public static double[] P(double i, double Ap, int Kp)
    {
        if (Ap < -0.2 || Ap > 0.5)
        {
            throw new ArgumentException("Warning! Ap is out of boundaries.");
        }
        if (Kp < 10 || Kp > 100)
        {
            throw new ArgumentException("Warning! Kp is out of boundaries.");
        }

        List<double> k = RangeList(0, Kp, i);
        double[] pWave = new double[k.Count];
        for (int idx = 0; idx < k.Count; idx++)
        {
            pWave[idx] = -(Ap / 2.0) * Math.Cos((2.0 * Math.PI * k[idx] + 15.0) / Kp) + Ap / 2.0;
        }
        return pWave;
    }

    public static double[] Pq(double l, int Kpq)
    {
        if (Kpq < 0 || Kpq > 60)
        {
            throw new ArgumentException("Warning! Kpq is out of boundaries.");
        }
        int size = (int)(Kpq * l);
        return new double[size];
    }

    public static double[] Q1(double i, double Aq, int Kq1)
    {
        if (Aq < 0 || Aq > 0.5)
        {
            throw new ArgumentException("Warning! Aq is out of boundaries.");
        }
        if (Kq1 < 0 || Kq1 > 70)
        {
            throw new ArgumentException("Warning! Kq1 is out of boundaries.");
        }

        List<double> k = RangeList(0, Kq1, i);
        double[] q1Wave = new double[k.Count];
        for (int idx = 0; idx < k.Count; idx++)
        {
            q1Wave[idx] = -Aq * (k[idx] / Kq1);
        }
        return q1Wave;
    }

    public static double[] Q2(double i, double Aq, int Kq2)
    {
        if (Aq < 0 || Aq > 0.5)
        {
            throw new ArgumentException("Warning! Aq is out of boundaries.");
        }
        if (Kq2 < 0 || Kq2 > 50)
        {
            throw new ArgumentException("Warning! Kq2 is out of boundaries.");
        }

        List<double> k = RangeList(0, Kq2, i);
        double[] q2Wave = new double[k.Count];
        for (int idx = 0; idx < k.Count; idx++)
        {
            q2Wave[idx] = Aq * (k[idx] / Kq2) - Aq;
        }
        return q2Wave;
    }

    public static double[] R(double i, double Ar, int Kr)
    {
        if (Ar < 0.5 || Ar > 2.0)
        {
            throw new ArgumentException("Warning! Ar is out of boundaries.");
        }
        if (Kr < 10 || Kr > 150)
        {
            throw new ArgumentException("Warning! Kr is out of boundaries.");
        }

        List<double> k = RangeList(0, Kr, i);
        double[] rWave = new double[k.Count];
        for (int idx = 0; idx < k.Count; idx++)
        {
            rWave[idx] = Ar * Math.Sin((Math.PI * k[idx]) / Kr);
        }
        return rWave;
    }

    public static double S_Scalar(double i, double As, int Ks, int Kcs, double k)
    {
        if (As < 0 || As > 1.0)
        {
            throw new ArgumentException("Warning! As is out of boundaries.");
        }
        if (Ks < 10 || Ks > 200)
        {
            throw new ArgumentException("Warning! Ks is out of boundaries.");
        }
        if (Kcs < -5 || Kcs > 150)
        {
            throw new ArgumentException("Warning! Kcs is out of boundaries.");
        }

        double val = -As * i * k * (19.78 * Math.PI) / Ks * Math.Exp(-2.0 * Math.Pow(((6.0 * Math.PI) / Ks) * i * k, 2.0));
        return val;
    }

    public static double[] S(double i, double As, int Ks, int Kcs)
    {
        List<double> kRange = RangeList(0, Ks - Kcs, i);
        double[] sWave = new double[kRange.Count];
        for (int idx = 0; idx < kRange.Count; idx++)
        {
            sWave[idx] = S_Scalar(i, As, Ks, Kcs, kRange[idx]);
        }
        return sWave;
    }

    public static double St_Scalar(double i, double As, int Ks, int Kcs, int sm, int Kst, double k)
    {
        if (sm < 1 || sm > 150)
        {
            throw new ArgumentException("Warning! sm is out of boundaries.");
        }
        if (Kst < 0 || Kst > 110)
        {
            throw new ArgumentException("Warning! Kst is out of boundaries.");
        }

        double sAtEnd = S_Scalar(i, As, Ks, Kcs, Ks - Kcs);
        double val = -sAtEnd * (k / sm) + sAtEnd;
        return val;
    }

    public static double[] St(double i, double As, int Ks, int Kcs, int sm, int Kst)
    {
        List<double> kRange = RangeList(0, Kst, i);
        double[] stSegment = new double[kRange.Count];
        for (int idx = 0; idx < kRange.Count; idx++)
        {
            stSegment[idx] = St_Scalar(i, As, Ks, Kcs, sm, Kst, kRange[idx]);
        }
        return stSegment;
    }

    public static double T_Scalar(double i, double As, int Ks, int Kcs, int sm, int Kst, double At, int Kt, double k)
    {
        if (At < -0.5 || At > 1.0)
        {
            throw new ArgumentException("Warning! At is out of boundaries.");
        }
        if (Kt < 50 || Kt > 300)
        {
            throw new ArgumentException("Warning! Kt is out of boundaries.");
        }

        double stAtEnd = St_Scalar(i, As, Ks, Kcs, sm, Kst, Kst);
        double val = -At * Math.Cos((1.48 * Math.PI * k + 15.0) / Kt) + At + stAtEnd;
        return val;
    }

    public static double[] T(double i, double As, int Ks, int Kcs, int sm, int Kst, double At, int Kt)
    {
        List<double> kRange = RangeList(0, Kt, i);
        double[] tWave = new double[kRange.Count];
        for (int idx = 0; idx < kRange.Count; idx++)
        {
            tWave[idx] = T_Scalar(i, As, Ks, Kcs, sm, Kst, At, Kt, kRange[idx]);
        }
        return tWave;
    }

    public static double[] I(double i, double As, int Ks, int Kcs, int sm, int Kst, double At, int Kt, int si, int Ki)
    {
        if (si < 0 || si > 50)
        {
            throw new ArgumentException("Warning! si is out of boundaries.");
        }

        double tAtEnd = T_Scalar(i, As, Ks, Kcs, sm, Kst, At, Kt, Kt);
        List<double> kRange = RangeList(0, Ki, i);
        double[] iSegment = new double[kRange.Count];
        for (int idx = 0; idx < kRange.Count; idx++)
        {
            iSegment[idx] = tAtEnd * (si / (kRange[idx] + 10.0));
        }
        return iSegment;
    }

    public static (double[] ecg, double[] t, Dictionary<string, double> parameters) Generate(
        int Kb = 130,
        double Ap = 0.2,
        int Kp = 100,
        int Kpq = 40,
        double Aq = 0.1,
        int Kq1 = 25,
        int Kq2 = 5,
        double Ar = 0.7,
        int Kr = 40,
        double As = 0.2,
        int Ks = 30,
        int Kcs = 5,
        int sm = 96,
        int Kst = 100,
        double At = 0.15,
        int Kt = 220,
        int si = 2,
        int Ki = 200,
        double var = 0.01,
        double samplingRate = 10000.0)
    {
        // 1. Boundary checks and warnings
        if (Kp > 120 && Ap >= 0.25)
        {
            Console.WriteLine("Warning: P wave isn't within physiological values.");
        }
        if (Kq1 + Kq2 > 30 || Aq > 0.25 * Ar)
        {
            Console.WriteLine("Warning: Q wave isn't within physiological values.");
        }
        if (120 > Kp + Kpq || Kp + Kpq > 220)
        {
            Console.WriteLine("Warning: PR interval isn't within physiological limits.");
        }
        if (Kq1 + Kq2 + Kr + Ks - Kcs > 120)
        {
            Console.WriteLine("Warning: QRS complex duration isn't within physiological limits.");
        }
        if (Kq1 + Kq2 + Kr + Ks - Kcs + Kst + Kt > 450)
        {
            Console.WriteLine("Warning: QT segment duration isn't within physiological limits for men.");
        }
        if (Kq1 + Kq2 + Kr + Ks - Kcs + Kst + Kt > 470)
        {
            Console.WriteLine("Warning: QT segment duration isn't within physiological limits for women.");
        }

        if (var < 0.0 || var > 1.0)
        {
            throw new ArgumentException("Variability value should be between 0.0 and 1.0");
        }

        // 2. Variability adjustments
        if (var > 0.0)
        {
            Kb = (int)Math.Round(Clip(NextNormal(Kb, Kb * var), 0, 130));
            Ap = Clip(NextNormal(Ap, Ap * var), -0.2, 0.5);
            Kp = (int)Math.Round(Clip(NextNormal(Kp, Kp * var), 10, 100));
            Kpq = (int)Math.Round(Clip(NextNormal(Kpq, Kpq * var), 0, 60));
            Aq = Clip(NextNormal(Aq, Aq * var), 0, 0.5);
            Kq1 = (int)Math.Round(Clip(NextNormal(Kq1, Kq1 * var), 0, 70));
            Kq2 = (int)Math.Round(Clip(NextNormal(Kq2, Kq2 * var), 0, 50));
            Ar = Clip(NextNormal(Ar, Ar * var), 0.5, 2.0);
            Kr = (int)Math.Round(Clip(NextNormal(Kr, Kr * var), 10, 150));
            As = Clip(NextNormal(As, As * var), 0, 1.0);
            Ks = (int)Math.Round(Clip(NextNormal(Ks, Ks * var), 10, 200));
            Kcs = (int)Math.Round(Clip(NextNormal(Kcs, Kcs * var), -5, 150));
            sm = (int)Math.Round(Clip(NextNormal(sm, sm * var), 1, 150));
            Kst = (int)Math.Round(Clip(NextNormal(Kst, Kst * var), 0, 110));
            At = Clip(NextNormal(At, At * var), -0.5, 1.0);
            Kt = (int)Math.Round(Clip(NextNormal(Kt, Kt * var), 50, 300));
            si = (int)Math.Round(Clip(NextNormal(si, si * var), 0, 50));
        }

        double i = 1000.0 / samplingRate;
        double l = 1.0 / i;

        // Concatenate waveforms
        List<double> bToSList = new List<double>();
        bToSList.AddRange(B(l, Kb));
        bToSList.AddRange(P(i, Ap, Kp));
        bToSList.AddRange(Pq(l, Kpq));
        bToSList.AddRange(Q1(i, Aq, Kq1));
        bToSList.AddRange(Q2(i, Aq, Kq2));
        bToSList.AddRange(R(i, Ar, Kr));
        bToSList.AddRange(S(i, As, Ks, Kcs));

        List<double> stToIList = new List<double>();
        stToIList.AddRange(St(i, As, Ks, Kcs, sm, Kst));
        stToIList.AddRange(T(i, As, Ks, Kcs, sm, Kst, At, Kt));
        stToIList.AddRange(I(i, As, Ks, Kcs, sm, Kst, At, Kt, si, Ki));

        double[] bToS = bToSList.ToArray();
        double[] stToI = stToIList.ToArray();

        // 3. Apply smoothing filters using the hybrid "boxzen" kernel
        double[] ecg1Filtered = Dsp.Smoother(bToS, "boxzen", 50, true);
        double[] ecg2Filtered = Dsp.Smoother(stToI, "boxzen", 500, true);

        // Concatenate results
        double[] ecgwave = new double[ecg1Filtered.Length + ecg2Filtered.Length];
        Array.Copy(ecg1Filtered, 0, ecgwave, 0, ecg1Filtered.Length);
        Array.Copy(ecg2Filtered, 0, ecgwave, ecg1Filtered.Length, ecg2Filtered.Length);

        double[] t = new double[ecgwave.Length];
        for (int idx = 0; idx < t.Length; idx++)
        {
            t[idx] = idx / samplingRate;
        }

        var parameters = new Dictionary<string, double>
        {
            { "Kb", Kb }, { "Ap", Ap }, { "Kp", Kp }, { "Kpq", Kpq },
            { "Aq", Aq }, { "Kq1", Kq1 }, { "Kq2", Kq2 }, { "Ar", Ar },
            { "Kr", Kr }, { "As", As }, { "Ks", Ks }, { "Kcs", Kcs },
            { "sm", sm }, { "Kst", Kst }, { "At", At }, { "Kt", Kt },
            { "si", si }, { "Ki", Ki }, { "var", var }, { "sampling_rate", samplingRate }
        };

        return (ecgwave, t, parameters);
    }
}
