using CardioSimulator.Core.Data;
using CardioSimulator.Core.Domain;
using Microsoft.Graphics.Canvas;
using Windows.UI;

namespace CardioSimulator.App.Rendering;

/// <summary>
/// Auto-detects a printed ECG trace in a loaded reference image and returns ADC samples
/// suitable for <c>ConstructorViewModel.TraceSamples</c>. Port of the Android
/// <c>TraceExtractor</c> (pure pixel processing, no CV libraries). The algorithm:
/// for each sample column on the canvas, map the column back through the inverse photo
/// transform to image space, scan all canvas-Y rows mapped to image pixels, skip pink
/// grid pixels and very bright pixels, and pick the darkest remaining pixel as the
/// trace position. Convert canvas-Y back to ADC via the inverse of
/// <c>EcgRenderer</c>'s trace mapping.
/// </summary>
public static class TraceExtractor
{
    /// <summary>
    /// Returns an ADC array of length <paramref name="sampleCount"/>, or null if the image
    /// is invalid. Any column with no detected pixel falls back to <paramref name="baseline"/>.
    /// </summary>
    public static int[]? Extract(
        CanvasBitmap image,
        int sampleCount,
        int baseline,
        float stepX,
        float stepY,
        PhotoTransform transform,
        float viewWidth,
        float viewHeight)
    {
        if (image is null || sampleCount <= 0 || viewWidth <= 0 || viewHeight <= 0) return null;

        var imgW = (int)image.SizeInPixels.Width;
        var imgH = (int)image.SizeInPixels.Height;
        if (imgW <= 0 || imgH <= 0) return null;

        Color[] pixels;
        try { pixels = image.GetPixelColors(); }
        catch { return null; }

        var baselineY = viewHeight / 2f;
        var traceLeft = EcgRenderer.CalAreaWidth;

        // Image is drawn with this composed matrix in canvas space (see EcgRenderer.RenderEditableLead):
        //   M = T(-w/2, -h/2) * S(scale) * R(rotDeg) * T(viewW/2 + offX, viewH/2 + offY)
        // Inverse (canvas → image):
        //   (image - w/2, image - h/2) = R(-rotDeg) * (canvas - center) / scale
        //   center = (viewW/2 + offX, viewH/2 + offY)
        var cx = viewWidth / 2f + transform.OffsetX;
        var cy = viewHeight / 2f + transform.OffsetY;
        var invScale = 1f / MathF.Max(0.001f, transform.Scale);
        var thetaRad = -transform.RotationDeg * MathF.PI / 180f;
        var cos = MathF.Cos(thetaRad);
        var sin = MathF.Sin(thetaRad);

        var result = new int[sampleCount];
        var heightI = (int)MathF.Round(viewHeight);
        var prev = baseline; // continuity: hold the last detected value across blank columns

        for (var i = 0; i < sampleCount; i++)
        {
            var canvasX = traceLeft + i * stepX;
            var bestCanvasY = -1;
            var bestBrightness = int.MaxValue;

            for (var y = 0; y < heightI; y++)
            {
                var dx = canvasX - cx;
                var dy = y - cy;
                var rx = dx * cos - dy * sin;
                var ry = dx * sin + dy * cos;
                var imgX = (int)(rx * invScale + imgW / 2f);
                var imgY = (int)(ry * invScale + imgH / 2f);
                if (imgX < 0 || imgX >= imgW || imgY < 0 || imgY >= imgH) continue;

                var c = pixels[imgY * imgW + imgX];
                if (IsLikelyGrid(c) || IsTooBright(c)) continue;

                var brightness = c.R + c.G + c.B;
                if (brightness < bestBrightness)
                {
                    bestBrightness = brightness;
                    bestCanvasY = y;
                }
            }

            if (bestCanvasY < 0)
            {
                // No trace pixel in this column — hold the previous value (Android continuity)
                // rather than snapping to baseline, so gaps don't create vertical spikes.
                result[i] = prev;
                continue;
            }

            prev = baseline + (int)MathF.Round((baselineY - bestCanvasY) / MathF.Max(0.001f, stepY));
            result[i] = prev;
        }

        return result;
    }

    /// <summary>Pink-ish (red-dominated, low blue/green) → likely grid line.</summary>
    private static bool IsLikelyGrid(Color c) =>
        c.R > 200 && c.G + 30 < c.R && c.B + 30 < c.R;

    /// <summary>Near-white → likely paper, not ink.</summary>
    private static bool IsTooBright(Color c) => c.R + c.G + c.B > 600;
}
