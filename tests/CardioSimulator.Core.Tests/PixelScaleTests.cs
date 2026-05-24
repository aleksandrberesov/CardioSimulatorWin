using CardioSimulator.Core.Data;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class PixelScaleTests
{
    // Anchor from the rendering doc's worked example: density 1.0, displayScale 0.4.
    // pxPerMm = 1.0 * (160 / 25.4) * 0.4 = 2.519685
    private static readonly PixelScale Scale = new(
        PxPerMm: 2.519685f,
        PaperSpeedMmPerSec: 25f,
        GainZoomY: 1f,
        Cal: new EcgCalibration());

    [Fact]
    public void DerivesPaperCoordinatesFromAnchor()
    {
        Assert.Equal(25.19685, Scale.PxPerMv, 3);
        Assert.Equal(62.992125, Scale.PxPerSec, 3);
        Assert.Equal(0.12598425, Scale.PxPerSample, 5);
        Assert.Equal(0.09842519, Scale.PxPerAdcCount, 5);
        Assert.Equal(2.519685, Scale.SmallGridStepPx, 4);
        Assert.Equal(12.598425, Scale.LargeGridStepPx, 4);
    }

    [Fact]
    public void GainZoom_ScalesVerticalAxisOnly()
    {
        var zoomed = Scale with { GainZoomY = 2f };
        Assert.Equal(2f * Scale.PxPerMv, zoomed.PxPerMv, 3);
        Assert.Equal(2f * Scale.PxPerAdcCount, zoomed.PxPerAdcCount, 5);
        // X axis is unaffected by gain zoom.
        Assert.Equal(Scale.PxPerSample, zoomed.PxPerSample, 5);
    }
}
