using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BioSPPy.Net.Signals.Tools;

public static class Dsp
{
    public static double[] GetWindow(string kernel, int size)
    {
        double[] w = new double[size];
        if (size <= 0) return w;
        if (size == 1)
        {
            w[0] = 1.0;
            return w;
        }

        switch (kernel.ToLowerInvariant())
        {
            case "boxcar":
            case "box":
            case "ones":
            case "rect":
            case "rectangular":
                for (int i = 0; i < size; i++) w[i] = 1.0;
                break;

            case "hamming":
            case "hamm":
            case "ham":
                for (int i = 0; i < size; i++)
                {
                    w[i] = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / (size - 1));
                }
                break;

            case "hanning":
            case "hann":
            case "han":
                for (int i = 0; i < size; i++)
                {
                    w[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (size - 1));
                }
                break;

            case "parzen":
            case "parz":
            case "par":
                for (int i = 0; i < size; i++)
                {
                    double t = i - (size - 1) / 2.0;
                    double abs_t = Math.Abs(t);
                    if (abs_t <= size / 4.0)
                    {
                        double ratio = abs_t / (size / 2.0);
                        w[i] = 1.0 - 6.0 * ratio * ratio * (1.0 - ratio);
                    }
                    else if (abs_t <= size / 2.0)
                    {
                        double ratio = abs_t / (size / 2.0);
                        w[i] = 2.0 * Math.Pow(1.0 - ratio, 3.0);
                    }
                    else
                    {
                        w[i] = 0.0;
                    }
                }
                break;

            default:
                throw new ArgumentException($"Unsupported window kernel: {kernel}");
        }

        // Normalize sum to 1.0
        double sum = w.Sum();
        if (sum > 0)
        {
            for (int i = 0; i < size; i++) w[i] /= sum;
        }
        return w;
    }

    public static double[] ConvolveSame(double[] a, double[] v)
    {
        int na = a.Length;
        int nv = v.Length;
        double[] output = new double[na];
        int start = (nv - 1) / 2;

        for (int n = 0; n < na; n++)
        {
            double sum = 0.0;
            for (int k = 0; k < nv; k++)
            {
                int idx = n + start - k;
                if (idx >= 0 && idx < na)
                {
                    sum += a[idx] * v[k];
                }
            }
            output[n] = sum;
        }
        return output;
    }

    public static double[] Smoother(double[] signal, string kernel = "boxzen", int size = 10, bool mirror = true)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        if (signal.Length == 0) return Array.Empty<double>();

        int length = signal.Length;
        if (size > length) size = length - 1;
        if (size < 1) size = 1;

        if (kernel.Equals("boxzen", StringComparison.OrdinalIgnoreCase))
        {
            // 2-pass smoothing
            double[] aux = Smoother(signal, "boxcar", size, mirror);
            return Smoother(aux, "parzen", size, mirror);
        }

        double[] w = GetWindow(kernel, size);

        double[] smoothed;
        if (mirror)
        {
            double[] aux = new double[length + 2 * size];
            for (int i = 0; i < size; i++) aux[i] = signal[0];
            for (int i = 0; i < length; i++) aux[size + i] = signal[i];
            for (int i = 0; i < size; i++) aux[size + length + i] = signal[length - 1];

            double[] smoothedAux = ConvolveSame(aux, w);
            smoothed = new double[length];
            Array.Copy(smoothedAux, size, smoothed, 0, length);
        }
        else
        {
            smoothed = ConvolveSame(signal, w);
        }

        return smoothed;
    }

    public static void Radix2FFT(Complex[] a, bool inverse = false)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                var temp = a[i];
                a[i] = a[j];
                a[j] = temp;
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            Complex wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                Complex w = 1.0;
                for (int j = 0; j < len / 2; j++)
                {
                    Complex u = a[i + j];
                    Complex v = a[i + j + len / 2] * w;
                    a[i + j] = u + v;
                    a[i + j + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }

        if (inverse)
        {
            for (int i = 0; i < n; i++)
                a[i] /= n;
        }
    }

    public static int NextPowerOf2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    public static (double[] frequencies, double[] psd) Welch(double[] signal, double fs, int nperseg = 1024)
    {
        int n = signal.Length;
        if (n < nperseg) nperseg = n;
        nperseg = NextPowerOf2(nperseg); // Ensure power of 2 for Radix-2 FFT

        int step = nperseg / 2;
        double[] win = GetWindow("hann", nperseg);
        
        // Sum of squares of window coefficients for PSD scaling
        double winSumSq = 0.0;
        for (int i = 0; i < nperseg; i++)
        {
            // Note: GetWindow normalizes GetWindow sum to 1.0.
            // But scipy's hann window is not normalized by sum to 1.0!
            // Let's compute scipy-like hann window (not normalized by sum) to match scipy.signal.welch.
            // In scipy: win[i] = 0.5 - 0.5 * cos(2 * pi * i / (nperseg - 1))
            double val = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (nperseg - 1));
            win[i] = val;
            winSumSq += val * val;
        }

        List<double[]> segmentPsds = new List<double[]>();
        for (int start = 0; start + nperseg <= n; start += step)
        {
            double[] segment = new double[nperseg];
            double mean = 0.0;
            for (int i = 0; i < nperseg; i++)
            {
                segment[i] = signal[start + i];
                mean += segment[i];
            }
            mean /= nperseg;

            Complex[] fftInput = new Complex[nperseg];
            for (int i = 0; i < nperseg; i++)
            {
                // Detrend constant (subtract mean) and apply window
                double val = (segment[i] - mean) * win[i];
                fftInput[i] = new Complex(val, 0.0);
            }

            Radix2FFT(fftInput);

            int half = nperseg / 2;
            double[] segmentPsd = new double[half + 1];

            // Scaling factor: one-sided PSD scaling
            double scale = 2.0 / (fs * winSumSq);
            double scaleDcNyq = 1.0 / (fs * winSumSq);

            segmentPsd[0] = fftInput[0].Magnitude * fftInput[0].Magnitude * scaleDcNyq;
            for (int i = 1; i < half; i++)
            {
                segmentPsd[i] = fftInput[i].Magnitude * fftInput[i].Magnitude * scale;
            }
            segmentPsd[half] = fftInput[half].Magnitude * fftInput[half].Magnitude * scaleDcNyq;

            segmentPsds.Add(segmentPsd);
        }

        if (segmentPsds.Count == 0)
        {
            return (Array.Empty<double>(), Array.Empty<double>());
        }

        int psdLen = nperseg / 2 + 1;
        double[] psd = new double[psdLen];
        for (int i = 0; i < psdLen; i++)
        {
            double sum = 0.0;
            for (int j = 0; j < segmentPsds.Count; j++)
            {
                sum += segmentPsds[j][i];
            }
            psd[i] = sum / segmentPsds.Count;
        }

        double[] freqs = new double[psdLen];
        double df = fs / nperseg;
        for (int i = 0; i < psdLen; i++)
        {
            freqs[i] = i * df;
        }

        return (freqs, psd);
    }

    public static double IntegrateTrapz(double[] y, double[] x)
    {
        if (y.Length < 2) return 0.0;
        double sum = 0.0;
        for (int i = 0; i < y.Length - 1; i++)
        {
            sum += 0.5 * (y[i] + y[i + 1]) * (x[i + 1] - x[i]);
        }
        return sum;
    }

    public static (double[] index, double[] heartRate) GetHeartRate(int[] beats, double samplingRate = 1000.0, bool smooth = false, int size = 3)
    {
        if (beats == null) throw new ArgumentNullException(nameof(beats));
        if (beats.Length < 2) throw new ArgumentException("Not enough beats to compute heart rate.");

        int n = beats.Length;
        double[] ts = new double[n - 1];
        double[] hr = new double[n - 1];

        for (int i = 0; i < n - 1; i++)
        {
            ts[i] = beats[i + 1];
            double diff = beats[i + 1] - beats[i];
            hr[i] = samplingRate * (60.0 / diff);
        }

        // physiological limits: 40 <= hr <= 200
        List<double> validTs = new List<double>();
        List<double> validHr = new List<double>();
        for (int i = 0; i < hr.Length; i++)
        {
            if (hr[i] >= 40.0 && hr[i] <= 200.0)
            {
                validTs.Add(ts[i]);
                validHr.Add(hr[i]);
            }
        }

        double[] finalTs = validTs.ToArray();
        double[] finalHr = validHr.ToArray();

        // smooth with moving average
        if (smooth && finalHr.Length > 1)
        {
            finalHr = Smoother(finalHr, "boxcar", size, true);
        }

        return (finalTs, finalHr);
    }
}

