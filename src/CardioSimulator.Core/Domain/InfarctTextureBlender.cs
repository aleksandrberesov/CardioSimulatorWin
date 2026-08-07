namespace CardioSimulator.Core.Domain;

/// <summary>
/// The per-pixel blend that turns a healthy myocardium albedo into an infarcted one, gated by a
/// grayscale mask and driven by a single 0..1 <c>progress</c> value. This is the "alpha channel"
/// mechanism the model was authored around:
///
/// <code>final = lerp(healthy, infarct, mask * progress)</code>
///
/// The mask marks <em>where</em> necrosis can appear (white = infarct region, black = untouched
/// tissue); <c>progress</c> marks <em>how far</em> it has developed. At <c>progress = 0</c> the
/// output equals the healthy texture; at <c>progress = 1</c> the infarct texture shows fully wherever
/// the mask is white, with the mask's soft edges giving a feathered necrosis border for free.
///
/// The math is deliberately platform-neutral (raw byte buffers, no imaging types) so it can be unit
/// tested here and mirrored verbatim in the Android renderer. Colour channels operate on interleaved
/// BGRA8 buffers (the format Windows' <c>BitmapDecoder</c> and Direct3D's
/// <c>B8G8R8A8_UNorm</c> agree on); the mask is a separate one-byte-per-pixel buffer.
/// </summary>
public static class InfarctTextureBlender
{
    /// <summary>Bytes per pixel in the colour buffers (B, G, R, A).</summary>
    public const int BytesPerPixel = 4;

    /// <summary>
    /// Blends <paramref name="healthyBgra"/> toward <paramref name="infarctBgra"/> using
    /// <paramref name="maskGray"/> and <paramref name="progress"/>, writing BGRA8 into
    /// <paramref name="destBgra"/>. Alpha is forced opaque so the result is a plain albedo map (the
    /// X-ray translucency is applied separately, via the material's colour, not the texture).
    /// </summary>
    /// <param name="healthyBgra">Healthy albedo, interleaved BGRA8, length = <paramref name="pixelCount"/> * 4.</param>
    /// <param name="infarctBgra">Infarct albedo, same layout/length as the healthy buffer.</param>
    /// <param name="maskGray">Blend mask, one byte per pixel, length = <paramref name="pixelCount"/>.</param>
    /// <param name="pixelCount">Number of pixels (width * height).</param>
    /// <param name="progress">Infarct development, clamped to [0, 1].</param>
    /// <param name="destBgra">Destination, same layout/length as the colour buffers. May reuse an existing buffer.</param>
    public static void BlendBgra(
        ReadOnlySpan<byte> healthyBgra,
        ReadOnlySpan<byte> infarctBgra,
        ReadOnlySpan<byte> maskGray,
        int pixelCount,
        float progress,
        Span<byte> destBgra)
    {
        int colorLen = pixelCount * BytesPerPixel;
        if (healthyBgra.Length < colorLen || infarctBgra.Length < colorLen || destBgra.Length < colorLen)
        {
            throw new ArgumentException("Colour buffers must hold at least pixelCount * 4 bytes.");
        }
        if (maskGray.Length < pixelCount)
        {
            throw new ArgumentException("Mask buffer must hold at least pixelCount bytes.", nameof(maskGray));
        }

        progress = Math.Clamp(progress, 0f, 1f);

        for (int p = 0; p < pixelCount; p++)
        {
            // Blend weight for this pixel: how much of the infarct texture shows through.
            // maskGray/255 in [0,1] scaled by progress; a 0 mask leaves the pixel fully healthy.
            float w = (maskGray[p] / 255f) * progress;
            int i = p * BytesPerPixel;

            destBgra[i]     = Lerp(healthyBgra[i], infarctBgra[i], w);         // B
            destBgra[i + 1] = Lerp(healthyBgra[i + 1], infarctBgra[i + 1], w); // G
            destBgra[i + 2] = Lerp(healthyBgra[i + 2], infarctBgra[i + 2], w); // R
            destBgra[i + 3] = 255;                                             // A (opaque)
        }
    }

    /// <summary>Allocating convenience overload; returns a fresh BGRA8 buffer.</summary>
    public static byte[] BlendBgra(
        byte[] healthyBgra,
        byte[] infarctBgra,
        byte[] maskGray,
        int pixelCount,
        float progress)
    {
        var dest = new byte[pixelCount * BytesPerPixel];
        BlendBgra(healthyBgra, infarctBgra, maskGray, pixelCount, progress, dest);
        return dest;
    }

    private static byte Lerp(byte a, byte b, float t)
    {
        // Rounded linear interpolation kept inside [0,255]; inputs are already valid bytes so the
        // result cannot exceed the range, but clamp defensively against float error at the ends.
        float v = a + (b - a) * t;
        int r = (int)MathF.Round(v);
        return (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
    }
}
