using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BioSPPy.Net.Signals.Tools;

public static class Filtering
{
    public static double[] Poly(Complex[] roots)
    {
        int n = roots.Length;
        Complex[] coeffs = new Complex[n + 1];
        coeffs[0] = 1.0;
        for (int i = 0; i < n; i++)
        {
            Complex r = roots[i];
            for (int j = i + 1; j > 0; j--)
            {
                coeffs[j] = coeffs[j] - r * coeffs[j - 1];
            }
        }
        double[] realCoeffs = new double[n + 1];
        for (int i = 0; i <= n; i++)
        {
            realCoeffs[i] = coeffs[i].Real;
        }
        return realCoeffs;
    }

    public static (double[] b, double[] a) Butterworth(int order, double[] Wn, string band = "lowpass")
    {
        // 1. Get analog prototype poles
        Complex[] poles = new Complex[order];
        for (int k = 0; k < order; k++)
        {
            double theta = Math.PI * (2 * k + 1) / (2.0 * order);
            poles[k] = new Complex(-Math.Sin(theta), Math.Cos(theta));
        }

        // 2. Pre-warp cutoff frequencies
        double[] omega = new double[Wn.Length];
        for (int i = 0; i < Wn.Length; i++)
        {
            omega[i] = Math.Tan(Math.PI * Wn[i] / 2.0);
        }

        List<Complex> analogPoles = new List<Complex>();
        List<Complex> analogZeros = new List<Complex>();
        double analogGain = 1.0;

        if (band.Equals("lowpass", StringComparison.OrdinalIgnoreCase))
        {
            double w = omega[0];
            for (int i = 0; i < order; i++)
            {
                analogPoles.Add(poles[i] * w);
            }
            analogGain = Math.Pow(w, order);
        }
        else if (band.Equals("highpass", StringComparison.OrdinalIgnoreCase))
        {
            double w = omega[0];
            for (int i = 0; i < order; i++)
            {
                analogPoles.Add(w / poles[i]);
                analogZeros.Add(0.0);
            }
            analogGain = 1.0; // Normalized highpass analog gain at infinity is 1.0
        }
        else if (band.Equals("bandpass", StringComparison.OrdinalIgnoreCase))
        {
            double w1 = omega[0];
            double w2 = omega[1];
            double bw = w2 - w1;
            double w0Sq = w1 * w2;

            for (int i = 0; i < order; i++)
            {
                Complex p = poles[i];
                // s^2 - bw * p * s + w0Sq = 0
                Complex term1 = bw * p / 2.0;
                Complex term2 = Complex.Sqrt(bw * bw * p * p - 4.0 * w0Sq) / 2.0;
                analogPoles.Add(term1 + term2);
                analogPoles.Add(term1 - term2);
                analogZeros.Add(0.0);
            }
            analogGain = Math.Pow(bw, order);
        }
        else if (band.Equals("bandstop", StringComparison.OrdinalIgnoreCase))
        {
            double w1 = omega[0];
            double w2 = omega[1];
            double bw = w2 - w1;
            double w0Sq = w1 * w2;

            for (int i = 0; i < order; i++)
            {
                Complex p = poles[i];
                // s^2 - (bw / p) * s + w0Sq = 0
                Complex term1 = (bw / p) / 2.0;
                Complex term2 = Complex.Sqrt((bw / p) * (bw / p) - 4.0 * w0Sq) / 2.0;
                analogPoles.Add(term1 + term2);
                analogPoles.Add(term1 - term2);
                analogZeros.Add(new Complex(0.0, Math.Sqrt(w0Sq)));
                analogZeros.Add(new Complex(0.0, -Math.Sqrt(w0Sq)));
            }

            // Calculate gain to make response at s=0 equal to 1.0
            Complex poleProd = 1.0;
            foreach (var ap in analogPoles) poleProd *= -ap;
            analogGain = poleProd.Real / Math.Pow(w0Sq, order);
        }

        // 3. Bilinear transform to digital plane
        Complex[] digitalPoles = new Complex[analogPoles.Count];
        for (int i = 0; i < analogPoles.Count; i++)
        {
            digitalPoles[i] = (1.0 + analogPoles[i]) / (1.0 - analogPoles[i]);
        }

        Complex[] digitalZeros = new Complex[analogPoles.Count];
        int idx = 0;
        for (int i = 0; i < analogZeros.Count; i++)
        {
            digitalZeros[idx++] = (1.0 + analogZeros[i]) / (1.0 - analogZeros[i]);
        }
        while (idx < digitalPoles.Length)
        {
            digitalZeros[idx++] = -1.0;
        }

        // Compute digital gain
        Complex poleProduct = 1.0;
        foreach (var p in analogPoles) poleProduct *= (1.0 - p);

        Complex zeroProduct = 1.0;
        foreach (var z in analogZeros) zeroProduct *= (1.0 - z);

        double digitalGain = (analogGain * zeroProduct / poleProduct).Real;

        double[] b = Poly(digitalZeros);
        double[] a = Poly(digitalPoles);

        for (int i = 0; i < b.Length; i++)
        {
            b[i] *= digitalGain;
        }

        return (b, a);
    }

    public static double[] FirWin(int numtaps, double[] cutoff, string band = "lowpass")
    {
        // Enforce odd numtaps
        if (numtaps % 2 == 0) numtaps++;

        double[] h = new double[numtaps];
        int M = numtaps - 1;
        double halfM = M / 2.0;

        if (band.Equals("lowpass", StringComparison.OrdinalIgnoreCase))
        {
            double fc = cutoff[0];
            for (int n = 0; n < numtaps; n++)
            {
                double diff = n - halfM;
                if (Math.Abs(diff) < 1e-9)
                {
                    h[n] = fc;
                }
                else
                {
                    h[n] = Math.Sin(Math.PI * fc * diff) / (Math.PI * diff);
                }
            }
        }
        else if (band.Equals("highpass", StringComparison.OrdinalIgnoreCase))
        {
            double fc = cutoff[0];
            for (int n = 0; n < numtaps; n++)
            {
                double diff = n - halfM;
                double lp;
                if (Math.Abs(diff) < 1e-9)
                {
                    lp = fc;
                    h[n] = 1.0 - lp;
                }
                else
                {
                    lp = Math.Sin(Math.PI * fc * diff) / (Math.PI * diff);
                    h[n] = -lp;
                }
            }
        }
        else if (band.Equals("bandpass", StringComparison.OrdinalIgnoreCase))
        {
            double fc1 = cutoff[0];
            double fc2 = cutoff[1];
            for (int n = 0; n < numtaps; n++)
            {
                double diff = n - halfM;
                if (Math.Abs(diff) < 1e-9)
                {
                    h[n] = fc2 - fc1;
                }
                else
                {
                    h[n] = (Math.Sin(Math.PI * fc2 * diff) - Math.Sin(Math.PI * fc1 * diff)) / (Math.PI * diff);
                }
            }
        }
        else if (band.Equals("bandstop", StringComparison.OrdinalIgnoreCase))
        {
            double fc1 = cutoff[0];
            double fc2 = cutoff[1];
            for (int n = 0; n < numtaps; n++)
            {
                double diff = n - halfM;
                double bp;
                if (Math.Abs(diff) < 1e-9)
                {
                    bp = fc2 - fc1;
                    h[n] = 1.0 - bp;
                }
                else
                {
                    bp = (Math.Sin(Math.PI * fc2 * diff) - Math.Sin(Math.PI * fc1 * diff)) / (Math.PI * diff);
                    h[n] = -bp;
                }
            }
        }

        // Apply Hamming window
        for (int n = 0; n < numtaps; n++)
        {
            double win = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * n / M);
            h[n] *= win;
        }

        // Normalize
        if (band.Equals("lowpass", StringComparison.OrdinalIgnoreCase) || band.Equals("bandstop", StringComparison.OrdinalIgnoreCase))
        {
            double sum = h.Sum();
            for (int n = 0; n < numtaps; n++) h[n] /= sum;
        }
        else if (band.Equals("highpass", StringComparison.OrdinalIgnoreCase))
        {
            double sum = 0.0;
            for (int n = 0; n < numtaps; n++) sum += h[n] * Math.Pow(-1.0, n);
            for (int n = 0; n < numtaps; n++) h[n] /= sum;
        }
        else if (band.Equals("bandpass", StringComparison.OrdinalIgnoreCase))
        {
            double fCenter = (cutoff[0] + cutoff[1]) / 2.0;
            double sum = 0.0;
            for (int n = 0; n < numtaps; n++)
            {
                sum += h[n] * Math.Cos(Math.PI * fCenter * (n - halfM));
            }
            for (int n = 0; n < numtaps; n++) h[n] /= sum;
        }

        return h;
    }

    public static double[] LFilter(double[] b, double[] a, double[] x, out double[] zf, double[]? zi = null)
    {
        int n = x.Length;
        int nb = b.Length;
        int na = a.Length;
        int m = Math.Max(nb, na) - 1;

        double[] bPad = new double[m + 1];
        double[] aPad = new double[m + 1];
        Array.Copy(b, bPad, nb);
        Array.Copy(a, aPad, na);

        double a0 = aPad[0];
        if (Math.Abs(a0 - 1.0) > 1e-15)
        {
            for (int i = 0; i <= m; i++)
            {
                bPad[i] /= a0;
                aPad[i] /= a0;
            }
        }

        double[] z = new double[m];
        if (zi != null)
        {
            Array.Copy(zi, z, Math.Min(zi.Length, m));
        }

        double[] y = new double[n];
        for (int j = 0; j < n; j++)
        {
            double xj = x[j];
            double yj = bPad[0] * xj + (m > 0 ? z[0] : 0.0);
            y[j] = yj;

            for (int i = 0; i < m - 1; i++)
            {
                z[i] = bPad[i + 1] * xj - aPad[i + 1] * yj + z[i + 1];
            }
            if (m > 0)
            {
                z[m - 1] = bPad[m] * xj - aPad[m] * yj;
            }
        }

        zf = z;
        return y;
    }

    public static double[] LFilterZi(double[] b, double[] a)
    {
        int nb = b.Length;
        int na = a.Length;
        int r = Math.Max(nb, na) - 1;
        if (r <= 0) return Array.Empty<double>();

        double[] bPad = new double[r + 1];
        double[] aPad = new double[r + 1];
        Array.Copy(b, bPad, nb);
        Array.Copy(a, aPad, na);

        double a0 = aPad[0];
        if (Math.Abs(a0 - 1.0) > 1e-15)
        {
            for (int i = 0; i <= r; i++)
            {
                bPad[i] /= a0;
                aPad[i] /= a0;
            }
        }

        double[] v = new double[r];
        for (int i = 0; i < r; i++)
        {
            v[i] = bPad[i + 1] - aPad[i + 1] * bPad[0];
        }

        double[] c = new double[r];
        double[] d = new double[r];
        c[0] = 1.0;
        d[0] = 0.0;

        for (int i = 1; i < r; i++)
        {
            c[i] = aPad[i] + c[i - 1];
            d[i] = d[i - 1] - v[i - 1];
        }

        double denom = aPad[r] + c[r - 1];
        double z0 = 0.0;
        if (Math.Abs(denom) > 1e-15)
        {
            z0 = (v[r - 1] - d[r - 1]) / denom;
        }

        double[] zi = new double[r];
        zi[0] = z0;
        for (int i = 1; i < r; i++)
        {
            zi[i] = c[i] * z0 + d[i];
        }

        return zi;
    }

    public static double[] FiltFilt(double[] b, double[] a, double[] x)
    {
        int length = x.Length;
        int r = Math.Max(b.Length, a.Length) - 1;
        if (r <= 0) return (double[])x.Clone();

        int padlen = 3 * (r + 1);
        if (length <= padlen)
        {
            throw new ArgumentException($"Signal length ({length}) must be greater than padlen ({padlen}).");
        }

        // Padding by reflection
        double[] yPad = new double[length + 2 * padlen];
        for (int j = 1; j <= padlen; j++)
        {
            yPad[padlen - j] = 2.0 * x[0] - x[j];
            yPad[length + padlen - 1 + j] = 2.0 * x[length - 1] - x[length - 1 - j];
        }
        Array.Copy(x, 0, yPad, padlen, length);

        double[] zi = LFilterZi(b, a);

        // Forward filter
        double[] ziScaled = new double[zi.Length];
        for (int i = 0; i < zi.Length; i++) ziScaled[i] = zi[i] * yPad[0];
        double[] yFwd = LFilter(b, a, yPad, out _, ziScaled);

        // Reverse
        Array.Reverse(yFwd);

        // Backward filter
        for (int i = 0; i < zi.Length; i++) ziScaled[i] = zi[i] * yFwd[0];
        double[] yBwd = LFilter(b, a, yFwd, out _, ziScaled);

        // Reverse back
        Array.Reverse(yBwd);

        // Slice
        double[] output = new double[length];
        Array.Copy(yBwd, padlen, output, 0, length);

        return output;
    }

    public static (double[] signal, double fs, Dictionary<string, object> paramsDict) FilterSignal(
        double[] signal,
        string ftype,
        string band,
        int order,
        double[] frequency,
        double sampling_rate)
    {
        double[] b, a;
        double[] Wn = frequency.Select(f => 2.0 * f / sampling_rate).ToArray();

        if (ftype.Equals("FIR", StringComparison.OrdinalIgnoreCase))
        {
            b = FirWin(order, Wn, band);
            a = new double[] { 1.0 };
        }
        else if (ftype.Equals("butter", StringComparison.OrdinalIgnoreCase))
        {
            var coeff = Butterworth(order, Wn, band);
            b = coeff.b;
            a = coeff.a;
        }
        else
        {
            throw new ArgumentException($"Unsupported filter type: {ftype}");
        }

        double[] filtered = FiltFilt(b, a, signal);

        var paramsDict = new Dictionary<string, object>
        {
            { "ftype", ftype },
            { "order", order },
            { "frequency", frequency },
            { "band", band }
        };

        return (filtered, sampling_rate, paramsDict);
    }
}
