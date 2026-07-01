using System.Collections.Generic;
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
}
