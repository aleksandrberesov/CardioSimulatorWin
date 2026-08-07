using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using CardioSimulator.Core.Domain;
using HelixToolkit.SharpDX;
using SharpDX.DXGI;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace CardioSimulator.App.Controls;

/// <summary>
/// The three source textures behind the infarct transition, decoded once into raw pixel buffers and
/// then blended on demand for a given 0..1 progress. See <see cref="InfarctTextureBlender"/> for the
/// blend itself; this type is the Windows-imaging + Direct3D glue around it.
///
/// The maps live as sidecar files next to the 3D model, keyed off the model's file name (mirroring
/// the <c>*.hotspots.json</c> convention): for <c>heart.glb</c> the loader looks for
/// <c>heart.healthy.*</c>, <c>heart.infarct.*</c> and <c>heart.mask.*</c> (jpg/jpeg/png). All three
/// must share the model's UV atlas resolution; the healthy map is the same atlas as the base-colour
/// texture already embedded in the model, so at progress 0 the heart looks exactly as authored.
/// </summary>
public sealed class InfarctTextureSet
{
    private readonly byte[] _healthyBgra; // interleaved BGRA8
    private readonly byte[] _infarctBgra; // interleaved BGRA8
    private readonly byte[] _maskGray;    // one byte per pixel

    public int Width { get; }
    public int Height { get; }
    private int PixelCount => Width * Height;

    private InfarctTextureSet(byte[] healthy, byte[] infarct, byte[] mask, int width, int height)
    {
        _healthyBgra = healthy;
        _infarctBgra = infarct;
        _maskGray = mask;
        Width = width;
        Height = height;
    }

    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png" };

    /// <summary>
    /// Resolves the three sidecar texture paths next to <paramref name="modelPath"/>, or <c>null</c>
    /// if any of the three is missing (⇒ the infarct feature is unavailable for this model).
    /// </summary>
    public static (string healthy, string infarct, string mask)? Resolve(string modelPath)
    {
        var dir = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }
        var baseName = Path.GetFileNameWithoutExtension(modelPath);
        var healthy = Probe(dir, baseName, "healthy");
        var infarct = Probe(dir, baseName, "infarct");
        var mask = Probe(dir, baseName, "mask");
        if (healthy is null || infarct is null || mask is null)
        {
            return null;
        }
        return (healthy, infarct, mask);
    }

    private static string? Probe(string dir, string baseName, string suffix)
    {
        foreach (var ext in ImageExtensions)
        {
            var candidate = Path.Combine(dir, $"{baseName}.{suffix}{ext}");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Decodes the three maps. Returns <c>null</c> if the images cannot be read or their dimensions
    /// disagree (they must share the model's UV atlas for the blend to line up).
    /// </summary>
    public static async Task<InfarctTextureSet?> LoadAsync(string healthyPath, string infarctPath, string maskPath)
    {
        var (healthy, hw, hh) = await DecodeBgraAsync(healthyPath);
        var (infarct, iw, ih) = await DecodeBgraAsync(infarctPath);
        var (mask, mw, mh) = await DecodeMaskAsync(maskPath);

        if (hw != iw || hh != ih || hw != mw || hh != mh || hw <= 0 || hh <= 0)
        {
            return null;
        }
        int pixels = hw * hh;
        if (healthy.Length < pixels * 4 || infarct.Length < pixels * 4 || mask.Length < pixels)
        {
            return null;
        }
        return new InfarctTextureSet(healthy, infarct, mask, hw, hh);
    }

    /// <summary>Blends the maps for <paramref name="progress"/> into a fresh BGRA8 buffer (CPU, thread-safe).</summary>
    public byte[] Blend(float progress) =>
        InfarctTextureBlender.BlendBgra(_healthyBgra, _infarctBgra, _maskGray, PixelCount, progress);

    /// <summary>Wraps a blended buffer as a Direct3D texture. Call on the UI thread with the render pipeline.</summary>
    public TextureModel Wrap(byte[] bgra) =>
        new(bgra, Format.B8G8R8A8_UNorm, Width, Height);

    private static async Task<(byte[] pixels, int width, int height)> DecodeBgraAsync(string path)
    {
        var decoder = await CreateDecoderAsync(path);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        return (data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    /// <summary>
    /// Decodes the mask to one byte per pixel. The mask is grayscale, so we decode to BGRA (the most
    /// broadly supported output) and take a single channel — robust across decoders that don't offer
    /// a native Gray8 path for JPEG.
    /// </summary>
    private static async Task<(byte[] gray, int width, int height)> DecodeMaskAsync(string path)
    {
        var (bgra, w, h) = await DecodeBgraAsync(path);
        var gray = new byte[w * h];
        for (int p = 0; p < gray.Length; p++)
        {
            gray[p] = bgra[p * 4 + 2]; // R channel (== G == B for a grayscale mask)
        }
        return (gray, w, h);
    }

    private static async Task<BitmapDecoder> CreateDecoderAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        return await BitmapDecoder.CreateAsync(stream);
    }
}
