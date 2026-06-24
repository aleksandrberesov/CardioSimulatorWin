using System;
using System.IO;

namespace CardioSimulator.App.Data;

/// <summary>
/// Stores and resolves the picture stimuli for image-based bank questions. Picked images are copied
/// into <see cref="AppPaths.TestImagesDir"/> as <c>&lt;questionId&gt;.&lt;ext&gt;</c> and referenced by
/// that relative filename in the question JSON, so the dataset is self-contained (survives the original
/// file being moved, and travels with an exported bank as long as the images folder is shipped too).
/// </summary>
public static class TestImageStore
{
    /// <summary>Copies a picked image into the images folder and returns its relative filename (the
    /// value stored in <c>TestQuestion.ImagePath</c>), or null on failure.</summary>
    public static string? Copy(string sourcePath, string questionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
            var ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(ext)) ext = ".png";
            Directory.CreateDirectory(AppPaths.TestImagesDir);
            var relative = questionId + ext.ToLowerInvariant();
            File.Copy(sourcePath, Path.Combine(AppPaths.TestImagesDir, relative), overwrite: true);
            return relative;
        }
        catch { return null; }
    }

    /// <summary>Absolute path for a stored relative filename.</summary>
    public static string FullPath(string relative) => Path.Combine(AppPaths.TestImagesDir, relative);

    public static bool Exists(string? relative) =>
        !string.IsNullOrEmpty(relative) && File.Exists(FullPath(relative));

    /// <summary>A <see cref="Uri"/> for binding to an <c>Image.Source</c>, or null when absent.</summary>
    public static Uri? UriFor(string? relative) =>
        Exists(relative) ? new Uri(FullPath(relative!)) : null;
}
