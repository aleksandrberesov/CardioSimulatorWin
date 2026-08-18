using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class EcgMeasurementsTests
{
    // 1000 Hz keeps sample→second conversion a clean divide-by-1000.
    private const double Fs = 1000.0;

    [Fact]
    public void Compute_EmptyOrNoSampleRate_ReturnsNothing()
    {
        Assert.False(EcgMeasurements.Compute(new List<SignificantPoint>(), Fs).HasAny);
        Assert.False(EcgMeasurements.Compute(
            new[] { new SignificantPoint(0, EcgPointType.R_PEAK) }, 0).HasAny);
    }

    [Fact]
    public void Compute_IntervalsAndSegments_UseBoundaryDeltas()
    {
        var points = new List<SignificantPoint>
        {
            new(100, EcgPointType.P_START),
            new(180, EcgPointType.P_END),      // P duration = 80 ms
            new(260, EcgPointType.QRS_START),  // PR interval = 160 ms (P_START→QRS_START)
            new(340, EcgPointType.QRS_END),    // QRS = 80 ms
            new(420, EcgPointType.T_START),    // ST = 80 ms (QRS_END→T_START)
            new(600, EcgPointType.T_END),      // T = 180 ms; QT = 340 ms (QRS_START→T_END)
        };

        var m = EcgMeasurements.Compute(points, Fs);

        Assert.Equal(0.080, m.PSeconds!.Value, 3);
        Assert.Equal(0.160, m.PrSeconds!.Value, 3);
        Assert.Equal(0.080, m.QrsSeconds!.Value, 3);
        Assert.Equal(0.080, m.StSeconds!.Value, 3);
        Assert.Equal(0.180, m.TSeconds!.Value, 3);
        Assert.Equal(0.340, m.QtSeconds!.Value, 3);
        Assert.Null(m.RrSeconds);
        Assert.Null(m.HeartRateBpm);
    }

    [Fact]
    public void Compute_MultipleRPeaks_AveragesRrAndDerivesHeartRate()
    {
        // R peaks at 0, 800, 1600 ms → mean R-R = 800 ms → 75 bpm.
        var points = new[]
        {
            new SignificantPoint(0, EcgPointType.R_PEAK),
            new SignificantPoint(800, EcgPointType.R_PEAK),
            new SignificantPoint(1600, EcgPointType.R_PEAK),
        };

        var m = EcgMeasurements.Compute(points, Fs);

        Assert.Equal(0.800, m.RrSeconds!.Value, 3);
        Assert.Equal(75.0, m.HeartRateBpm!.Value, 1);
    }

    [Fact]
    public void Compute_DuplicateBoundary_LastMarkerWins()
    {
        // A second QRS_END re-marks the complex; the later index should define the QRS width.
        var points = new[]
        {
            new SignificantPoint(100, EcgPointType.QRS_START),
            new SignificantPoint(150, EcgPointType.QRS_END),
            new SignificantPoint(200, EcgPointType.QRS_END),
        };

        var m = EcgMeasurements.Compute(points, Fs);

        Assert.Equal(0.100, m.QrsSeconds!.Value, 3);
    }

    [Fact]
    public void Compute_PqInterval_AndBazettQtc_AreDerived()
    {
        var points = new List<SignificantPoint>
        {
            new(100, EcgPointType.P_START),
            new(260, EcgPointType.QRS_START),
            new(280, EcgPointType.Q_PEAK),   // PQ = P_START→Q_PEAK = 180 ms
            new(600, EcgPointType.T_END),    // QT = QRS_START→T_END = 340 ms
            new(0, EcgPointType.R_PEAK),
            new(800, EcgPointType.R_PEAK),   // RR = 800 ms
        };

        var m = EcgMeasurements.Compute(points, Fs);

        Assert.Equal(0.180, m.PqSeconds!.Value, 3);
        // QTc (Bazett) = QT / sqrt(RR) = 0.340 / sqrt(0.800) ≈ 0.380 s.
        Assert.Equal(0.380, m.QtcSeconds!.Value, 3);
    }

    [Fact]
    public void Compute_QtcNull_WhenNoRr()
    {
        var points = new List<SignificantPoint>
        {
            new(260, EcgPointType.QRS_START),
            new(600, EcgPointType.T_END),   // QT present but only one R peak → no RR/QTc
            new(0, EcgPointType.R_PEAK),
        };

        var m = EcgMeasurements.Compute(points, Fs);

        Assert.Equal(0.340, m.QtSeconds!.Value, 3);
        Assert.Null(m.QtcSeconds);
    }

    [Fact]
    public void Compute_SixSecondHeartRate_CountsRPeaksInLeadingWindow()
    {
        // Six beats in the leading 6 s (0..5000 ms) plus one beyond it; the 6-second method sees only
        // the first six → 60 bpm, while the mean-R-R rate is dragged down by the late seventh beat.
        var points = new[] { 0, 1000, 2000, 3000, 4000, 5000, 7000 }
            .Select(i => new SignificantPoint(i, EcgPointType.R_PEAK)).ToList();
        var waveforms = new Dictionary<Lead, Points> { [Lead.II] = new Points(new float[8000]) };

        var m = EcgMeasurements.Compute(points, Fs, waveforms);

        Assert.Equal(60.0, m.HeartRate6SecBpm!.Value, 1);
        Assert.True(m.HeartRateBpm!.Value < 55.0); // mean R-R rate is lower than the 6-second count
    }

    [Fact]
    public void Compute_Amplitudes_ReadPerLeadFromBaselineZeroedSamples()
    {
        const int pIdx = 140, qIdx = 250;
        var leadII = new float[300];
        leadII[pIdx] = 150f;   // 0.15 mV at 1000 counts/mV
        leadII[qIdx] = -200f;  // -0.20 mV
        var leadV1 = new float[300];
        leadV1[pIdx] = 50f;    // 0.05 mV
        leadV1[qIdx] = -300f;  // -0.30 mV
        var waveforms = new Dictionary<Lead, Points>
        {
            [Lead.II] = new Points(leadII),
            [Lead.V1] = new Points(leadV1),
        };
        var points = new[]
        {
            new SignificantPoint(pIdx, EcgPointType.P_PEAK),
            new SignificantPoint(qIdx, EcgPointType.Q_PEAK),
        };

        var m = EcgMeasurements.Compute(points, Fs, waveforms, adcCountsPerMv: 1000.0);

        Assert.Equal(2, m.PAmplitudesMv!.Count);
        Assert.Equal(Lead.II, m.PAmplitudesMv[0].Lead); // canonical order: II precedes V1
        Assert.Equal(0.15, m.PAmplitudesMv.First(a => a.Lead == Lead.II).AmplitudeMv, 3);
        Assert.Equal(0.05, m.PAmplitudesMv.First(a => a.Lead == Lead.V1).AmplitudeMv, 3);
        Assert.Equal(-0.20, m.QAmplitudesMv!.First(a => a.Lead == Lead.II).AmplitudeMv, 3);
        Assert.Equal(-0.30, m.QAmplitudesMv.First(a => a.Lead == Lead.V1).AmplitudeMv, 3);
    }

    [Fact]
    public void Compute_Amplitudes_NullWithoutWaveforms()
    {
        var points = new[] { new SignificantPoint(10, EcgPointType.P_PEAK) };

        var m = EcgMeasurements.Compute(points, Fs);

        Assert.Null(m.PAmplitudesMv);
        Assert.Null(m.QAmplitudesMv);
    }
}
