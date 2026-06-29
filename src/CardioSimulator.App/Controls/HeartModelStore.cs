using System;
using System.IO;
using System.Linq;
using CardioSimulator.App.Data;

namespace CardioSimulator.App.Controls;

/// <summary>
/// Resolves which 3D heart model the viewer loads and manages the user override. Priority:
/// a user-chosen file under <see cref="AppPaths.ModelsDir"/> (set from Settings) wins, else the
/// bundled <c>Assets/Models/heart.*</c> shipped with the app, else none (placeholder primitive).
/// The override is stored as <c>heart.&lt;ext&gt;</c> so the viewer can probe it the same way as the
/// bundled file. Shared by <see cref="Heart3DDialog"/> (load) and the Settings dialog (change/reset).
/// </summary>
public static class HeartModelStore
{
    /// <summary>Supported model extensions, in the order they are probed.</summary>
    public static readonly string[] Extensions =
        { ".glb", ".gltf", ".fbx", ".obj", ".dae", ".stl", ".3ds", ".ply" };

    private static string BundledDir => Path.Combine(AppContext.BaseDirectory, "Assets", "Models");

    /// <summary>The model file the viewer should load, or <c>null</c> to fall back to the placeholder.</summary>
    public static string? ResolveActiveModelPath()
    {
        foreach (var dir in new[] { AppPaths.ModelsDir, BundledDir })
        {
            var match = Probe(dir);
            if (match is not null)
            {
                return match;
            }
        }
        return null;
    }

    /// <summary>True when a user override is in effect (vs the bundled default).</summary>
    public static bool HasUserModel() => Probe(AppPaths.ModelsDir) is not null;

    /// <summary>True if the path's extension is one the importer supports.</summary>
    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Copies the chosen file into the user models dir as the new default, replacing any prior
    /// override (so the probe order can't pick a stale file of a different extension).
    /// </summary>
    public static void SaveUserModel(string sourcePath)
    {
        Directory.CreateDirectory(AppPaths.ModelsDir);
        ClearUserModels();
        var dest = Path.Combine(AppPaths.ModelsDir, "heart" + Path.GetExtension(sourcePath).ToLowerInvariant());
        File.Copy(sourcePath, dest, overwrite: true);
    }

    /// <summary>Removes any user override, reverting to the bundled default.</summary>
    public static void ResetToBundled() => ClearUserModels();

    private static void ClearUserModels()
    {
        if (!Directory.Exists(AppPaths.ModelsDir))
        {
            return;
        }
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(AppPaths.ModelsDir, "heart" + ext);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string? Probe(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }
        foreach (var ext in Extensions)
        {
            var candidate = Path.Combine(dir, "heart" + ext);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
