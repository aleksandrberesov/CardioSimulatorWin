using System;
using System.Collections.Generic;
using System.Linq;

namespace BioSPPy.Net.Signals.Ecg;

public static class FiducialPoints
{
    public static int[] ArgRelExtrema(double[] array, bool findMin)
    {
        if (array == null || array.Length < 3) return Array.Empty<int>();
        var list = new List<int>();
        for (int i = 1; i < array.Length - 1; i++)
        {
            if (findMin)
            {
                if (array[i] < array[i - 1] && array[i] < array[i + 1])
                    list.Add(i);
            }
            else
            {
                if (array[i] > array[i - 1] && array[i] > array[i + 1])
                    list.Add(i);
            }
        }
        return list.ToArray();
    }

    private static int ArgMin(double[] array)
    {
        if (array == null || array.Length == 0) return 0;
        double minVal = array[0];
        int minIdx = 0;
        for (int i = 1; i < array.Length; i++)
        {
            if (array[i] < minVal)
            {
                minVal = array[i];
                minIdx = i;
            }
        }
        return minIdx;
    }

    private static int ArgMax(double[] array)
    {
        if (array == null || array.Length == 0) return 0;
        double maxVal = array[0];
        int maxIdx = 0;
        for (int i = 1; i < array.Length; i++)
        {
            if (array[i] > maxVal)
            {
                maxVal = array[i];
                maxIdx = i;
            }
        }
        return maxIdx;
    }

    private static double[] Slice(double[] array, int start, int end)
    {
        int length = end - start;
        if (length < 0) length = 0;
        double[] result = new double[length];
        Array.Copy(array, start, result, 0, length);
        return result;
    }

    public static (int[] Q_positions, int[] Q_start_positions) GetQPositions(double[,] templates, int[] rpeaks)
    {
        if (templates == null) throw new ArgumentNullException(nameof(templates));
        if (rpeaks == null) throw new ArgumentNullException(nameof(rpeaks));

        int nTemplates = templates.GetLength(0);
        int templateSize = templates.GetLength(1);

        int[] Q_positions = new int[nTemplates];
        int[] Q_start_positions = new int[nTemplates];

        int templateRPosition = 100;

        for (int n = 0; n < nTemplates; n++)
        {
            double[] each = new double[templateSize];
            for (int j = 0; j < templateSize; j++) each[j] = templates[n, j];

            // Get Q Position
            double[] templateLeft = Slice(each, 0, templateRPosition + 1);
            int[] minIndices = ArgRelExtrema(templateLeft, findMin: true);
            int qIdx = minIndices.Length > 0 ? minIndices[^1] : ArgMin(templateLeft);

            Q_positions[n] = rpeaks[n] - (templateRPosition - qIdx);

            // Get Q start position
            double[] templateQLeft = Slice(each, 0, qIdx + 1);
            int[] maxIndices = ArgRelExtrema(templateQLeft, findMin: false);
            int qStartIdx = maxIndices.Length > 0 ? maxIndices[^1] : ArgMax(templateQLeft);

            Q_start_positions[n] = rpeaks[n] - templateRPosition + qStartIdx;
        }

        return (Q_positions, Q_start_positions);
    }

    public static (int[] S_positions, int[] S_end_positions) GetSPositions(double[,] templates, int[] rpeaks)
    {
        if (templates == null) throw new ArgumentNullException(nameof(templates));
        if (rpeaks == null) throw new ArgumentNullException(nameof(rpeaks));

        int nTemplates = templates.GetLength(0);
        int templateSize = templates.GetLength(1);

        int[] S_positions = new int[nTemplates];
        int[] S_end_positions = new int[nTemplates];

        int templateRPosition = 100;

        for (int n = 0; n < nTemplates; n++)
        {
            double[] each = new double[templateSize];
            for (int j = 0; j < templateSize; j++) each[j] = templates[n, j];

            // Get S Position
            double[] templateRight = Slice(each, templateRPosition, templateSize);
            int[] minIndices = ArgRelExtrema(templateRight, findMin: true);
            int sIdxRel = minIndices.Length > 0 ? minIndices[0] : ArgMin(templateRight);

            S_positions[n] = rpeaks[n] + sIdxRel;

            // Get S end position
            int[] maxIndices = ArgRelExtrema(templateRight, findMin: false);
            int sEndIdxRel = maxIndices.Length > 0 ? maxIndices[0] : ArgMax(templateRight);

            S_end_positions[n] = rpeaks[n] + sEndIdxRel;
        }

        return (S_positions, S_end_positions);
    }

    public static (int[] P_positions, int[] P_start_positions, int[] P_end_positions) GetPPositions(double[,] templates, int[] rpeaks)
    {
        if (templates == null) throw new ArgumentNullException(nameof(templates));
        if (rpeaks == null) throw new ArgumentNullException(nameof(rpeaks));

        int nTemplates = templates.GetLength(0);
        int templateSize = templates.GetLength(1);

        int[] P_positions = new int[nTemplates];
        int[] P_start_positions = new int[nTemplates];
        int[] P_end_positions = new int[nTemplates];

        int templateRPosition = 100;
        int templatePPositionMax = 80;

        for (int n = 0; n < nTemplates; n++)
        {
            double[] each = new double[templateSize];
            for (int j = 0; j < templateSize; j++) each[j] = templates[n, j];

            // Get P Position
            double[] templateLeft = Slice(each, 0, templatePPositionMax + 1);
            int pIdx = ArgMax(templateLeft);

            P_positions[n] = rpeaks[n] - templateRPosition + pIdx;

            // Get P start position
            double[] templatePLeft = Slice(each, 0, pIdx + 1);
            int[] minIndicesLeft = ArgRelExtrema(templatePLeft, findMin: true);
            int pStartIdx = minIndicesLeft.Length > 0 ? minIndicesLeft[^1] : ArgMin(templatePLeft);

            P_start_positions[n] = rpeaks[n] - templateRPosition + pStartIdx;

            // Get P end position
            double[] templatePRight = Slice(each, pIdx, templatePPositionMax + 1);
            int[] minIndicesRight = ArgRelExtrema(templatePRight, findMin: true);
            int pEndIdxRel = minIndicesRight.Length > 0 ? minIndicesRight[0] : ArgMin(templatePRight);

            P_end_positions[n] = rpeaks[n] - templateRPosition + pIdx + pEndIdxRel;
        }

        return (P_positions, P_start_positions, P_end_positions);
    }

    public static (int[] T_positions, int[] T_start_positions, int[] T_end_positions) GetTPositions(double[,] templates, int[] rpeaks)
    {
        if (templates == null) throw new ArgumentNullException(nameof(templates));
        if (rpeaks == null) throw new ArgumentNullException(nameof(rpeaks));

        int nTemplates = templates.GetLength(0);
        int templateSize = templates.GetLength(1);

        int[] T_positions = new int[nTemplates];
        int[] T_start_positions = new int[nTemplates];
        int[] T_end_positions = new int[nTemplates];

        int templateRPosition = 100;
        int templateTPositionMin = 170;

        for (int n = 0; n < nTemplates; n++)
        {
            double[] each = new double[templateSize];
            for (int j = 0; j < templateSize; j++) each[j] = templates[n, j];

            // Get T Position
            double[] templateRight = Slice(each, templateTPositionMin, templateSize);
            int tIdxRel = ArgMax(templateRight);

            T_positions[n] = rpeaks[n] - templateRPosition + templateTPositionMin + tIdxRel;

            // Get T start position
            double[] templateTLeft = Slice(each, templateRPosition, templateTPositionMin + tIdxRel);
            int[] minIndicesLeft = ArgRelExtrema(templateTLeft, findMin: true);
            int tStartIdxRel = minIndicesLeft.Length > 0 ? minIndicesLeft[^1] : ArgMin(templateTLeft);

            T_start_positions[n] = rpeaks[n] + tStartIdxRel;

            // Get T end position
            double[] templateTRight = Slice(each, templateTPositionMin + tIdxRel, templateSize);
            int[] minIndicesRight = ArgRelExtrema(templateTRight, findMin: true);
            int tEndIdxRel = minIndicesRight.Length > 0 ? minIndicesRight[0] : ArgMin(templateTRight);

            T_end_positions[n] = rpeaks[n] - templateRPosition + templateTPositionMin + tIdxRel + tEndIdxRel;
        }

        return (T_positions, T_start_positions, T_end_positions);
    }

    public static List<EcgLandmarks> GetLandmarks(double[] signal, int[] rpeaks, double fs)
    {
        double before = 100.0 / fs;
        double after = 200.0 / fs;
        var (templates, validR) = EcgProcess.ExtractHeartbeats(signal, rpeaks, fs, before, after);

        var list = new List<EcgLandmarks>();
        if (validR.Length == 0) return list;

        var qPos = GetQPositions(templates, validR);
        var sPos = GetSPositions(templates, validR);
        var pPos = GetPPositions(templates, validR);
        var tPos = GetTPositions(templates, validR);

        for (int i = 0; i < validR.Length; i++)
        {
            list.Add(new EcgLandmarks(
                RPeak: validR[i],
                QPeak: qPos.Q_positions[i],
                QrsStart: qPos.Q_start_positions[i],
                SPeak: sPos.S_positions[i],
                QrsEnd: sPos.S_end_positions[i],
                PPeak: pPos.P_positions[i],
                PStart: pPos.P_start_positions[i],
                PEnd: pPos.P_end_positions[i],
                TPeak: tPos.T_positions[i],
                TStart: tPos.T_start_positions[i],
                TEnd: tPos.T_end_positions[i]
            ));
        }

        return list;
    }
}

public record EcgLandmarks(
    int RPeak,
    int QPeak,
    int QrsStart,
    int SPeak,
    int QrsEnd,
    int PPeak,
    int PStart,
    int PEnd,
    int TPeak,
    int TStart,
    int TEnd
);
