using CardioSimulator.Core.Domain;
using Xunit;

namespace CardioSimulator.Core.Tests;

public class InfarctTextureBlenderTests
{
    // One healthy pixel (white) and one infarct pixel (black), so a blend reads directly as
    // "how much necrosis": B/G/R fall from 255 toward 0 as the weight rises.
    private static (byte[] healthy, byte[] infarct) TwoPixels()
    {
        var healthy = new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 };
        var infarct = new byte[] { 0, 0, 0, 255, 0, 0, 0, 255 };
        return (healthy, infarct);
    }

    [Fact]
    public void Progress0_ReturnsHealthyUnchanged()
    {
        var (healthy, infarct) = TwoPixels();
        var mask = new byte[] { 255, 255 }; // fully masked, but progress 0 ⇒ no change

        var result = InfarctTextureBlender.BlendBgra(healthy, infarct, mask, pixelCount: 2, progress: 0f);

        Assert.Equal(healthy, result);
    }

    [Fact]
    public void Progress1_WhereMaskWhite_ShowsInfarctFully()
    {
        var (healthy, infarct) = TwoPixels();
        var mask = new byte[] { 255, 255 };

        var result = InfarctTextureBlender.BlendBgra(healthy, infarct, mask, pixelCount: 2, progress: 1f);

        // Colour channels fully replaced by the infarct texture; alpha forced opaque.
        Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255 }, result);
    }

    [Fact]
    public void MaskBlack_LeavesPixelHealthy_EvenAtFullProgress()
    {
        var (healthy, infarct) = TwoPixels();
        var mask = new byte[] { 0, 255 }; // pixel 0 outside the infarct region, pixel 1 inside

        var result = InfarctTextureBlender.BlendBgra(healthy, infarct, mask, pixelCount: 2, progress: 1f);

        // Pixel 0 (mask 0) stays healthy; pixel 1 (mask 255) becomes infarct.
        Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0, 0, 255 }, result);
    }

    [Fact]
    public void HalfMask_HalfProgress_BlendsQuarterOfTheWay()
    {
        var (healthy, infarct) = TwoPixels();
        var mask = new byte[] { 128, 128 }; // ~0.5 mask

        var result = InfarctTextureBlender.BlendBgra(healthy, infarct, mask, pixelCount: 2, progress: 0.5f);

        // weight = (128/255) * 0.5 ≈ 0.251; 255 → ~191.
        // Just assert it moved partway (strictly between healthy and infarct), not an exact byte.
        Assert.InRange(result[0], 180, 200);
        Assert.Equal(255, result[3]); // alpha opaque
    }

    [Fact]
    public void ProgressAboveOne_IsClampedToFullInfarct()
    {
        var (healthy, infarct) = TwoPixels();
        var mask = new byte[] { 255, 255 };

        var clamped = InfarctTextureBlender.BlendBgra(healthy, infarct, mask, pixelCount: 2, progress: 5f);
        var atOne = InfarctTextureBlender.BlendBgra(healthy, infarct, mask, pixelCount: 2, progress: 1f);

        Assert.Equal(atOne, clamped);
    }

    [Fact]
    public void TooSmallMask_Throws()
    {
        var (healthy, infarct) = TwoPixels();
        var mask = new byte[] { 255 }; // only one mask byte for two pixels

        Assert.Throws<ArgumentException>(() =>
            InfarctTextureBlender.BlendBgra(healthy, infarct, mask, pixelCount: 2, progress: 1f));
    }
}
