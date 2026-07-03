using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using CardioSimulator.App.Data;
using HelixToolkit.Geometry;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;

namespace CardioSimulator.App.Controls;

/// <summary>
/// One waypoint of the cardiac conduction pathway (SA node → AV node → His → bundle branches →
/// Purkinje → ventricular myocardium). Each node carries its anchor in model space and the
/// cumulative time (ms from the start of the cardiac cycle) at which the depolarisation wave
/// reaches it — the gap between the AV node and the His bundle is the AV nodal delay (the PR
/// segment), which is what makes the animation read as a real conduction sequence.
/// </summary>
public sealed class ConductionNode
{
    public string Key { get; set; } = string.Empty;
    public string LabelEn { get; set; } = string.Empty;
    public string LabelRu { get; set; } = string.Empty;
    public float[] Anchor { get; set; } = Array.Empty<float>();
    public float ArrivalMs { get; set; }

    public Vector3 Position =>
        Anchor is { Length: >= 3 } ? new Vector3(Anchor[0], Anchor[1], Anchor[2]) : Vector3.Zero;
}

/// <summary>The canonical stages of the conduction system, with clinically-ordered arrival times.</summary>
public readonly record struct ConductionStage(
    string Key, string En, string Ru, float ArrivalMs, string PhaseEn, string PhaseRu);

/// <summary>
/// The ordered conduction pathway for a model. Persisted next to the model file as a
/// <c>*.conduction.json</c> sidecar (same scheme as the hotspot sidecars). If none is authored,
/// <see cref="CreateDefault"/> lays a plausible top-to-apex path down the model's vertical axis so
/// there is something to animate immediately; the instructor can then refine the anchors by hand.
/// </summary>
public sealed class ConductionPath
{
    public List<ConductionNode> Nodes { get; set; } = new();

    /// <summary>The 7-stage template used both to seed a default path and to drive authoring order.</summary>
    public static readonly IReadOnlyList<ConductionStage> Template = new[]
    {
        new ConductionStage("sa",       "SA node",                "СА-узел",                   0f,
            "P wave — atrial depolarization",        "Зубец P — деполяризация предсердий"),
        new ConductionStage("atria",    "Atrial conduction",      "Проведение по предсердиям", 60f,
            "P wave — atrial depolarization",        "Зубец P — деполяризация предсердий"),
        new ConductionStage("av",       "AV node",                "АВ-узел",                   100f,
            "PR segment — AV nodal delay",           "Сегмент PR — задержка в АВ-узле"),
        new ConductionStage("his",      "Bundle of His",          "Пучок Гиса",                170f,
            "QRS — His–Purkinje conduction",         "QRS — проведение по системе Гиса–Пуркинье"),
        new ConductionStage("bundles",  "Bundle branches",        "Ножки пучка Гиса",          190f,
            "QRS — bundle branches",                 "QRS — ножки пучка Гиса"),
        new ConductionStage("purkinje", "Purkinje fibers",        "Волокна Пуркинье",          210f,
            "QRS — Purkinje network",                "QRS — сеть Пуркинье"),
        new ConductionStage("apex",     "Ventricular myocardium", "Миокард желудочков",        245f,
            "QRS complete — ventricles depolarized", "QRS завершён — желудочки деполяризованы"),
    };

    /// <summary>Total active-depolarisation time (ms); the rest of each beat is electrical diastole.</summary>
    public float TotalMs => Nodes.Count > 0 ? Nodes[^1].ArrivalMs : 0f;

    /// <summary>At least two placed nodes are needed before there is a path to draw/animate.</summary>
    public bool IsComplete => Nodes.Count >= 2;

    private static string PrimarySidecar(string modelPath) =>
        Path.ChangeExtension(modelPath, ".conduction.json");

    private static string FallbackSidecar(string modelPath) =>
        Path.Combine(AppPaths.ModelsDir, Path.GetFileNameWithoutExtension(modelPath) + ".conduction.json");

    /// <summary>Loads the authored pathway for a model, or <c>null</c> if none exists / fails to read.</summary>
    public static ConductionPath? Load(string modelPath)
    {
        foreach (var path in new[] { PrimarySidecar(modelPath), FallbackSidecar(modelPath) })
        {
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                var nodes = JsonSerializer.Deserialize<List<ConductionNode>>(File.ReadAllText(path));
                if (nodes is { Count: > 0 })
                {
                    return new ConductionPath { Nodes = nodes.OrderBy(n => n.ArrivalMs).ToList() };
                }
            }
            catch
            {
                // best-effort; fall through to the next candidate / default
            }
        }
        return null;
    }

    /// <summary>Writes the pathway next to the model, falling back to the writable models dir.</summary>
    public void Save(string modelPath)
    {
        var json = JsonSerializer.Serialize(Nodes, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            File.WriteAllText(PrimarySidecar(modelPath), json);
        }
        catch
        {
            try
            {
                Directory.CreateDirectory(AppPaths.ModelsDir);
                File.WriteAllText(FallbackSidecar(modelPath), json);
            }
            catch
            {
                // diagnostics only — never throw from a save
            }
        }
    }

    /// <summary>
    /// Seeds a plausible pathway down the model's vertical axis (base → apex) using the template
    /// stages, so a wave animates on first open. Anchors are rough — meant to be refined by the
    /// "Edit pathway" authoring mode, which snaps each node to a real surface point on the model.
    /// </summary>
    public static ConductionPath CreateDefault(BoundingBox bounds)
    {
        var min = bounds.Minimum;
        var max = bounds.Maximum;
        var cx = (min.X + max.X) * 0.5f;
        var cz = (min.Z + max.Z) * 0.5f;
        float top = max.Y, bottom = min.Y;
        float h = MathF.Max(max.Y - min.Y, 1e-3f);
        float w = MathF.Max(max.X - min.X, 1e-3f);

        // Fractions are (horizontal offset from centre, vertical position as height-from-bottom).
        (string key, float dx, float yFrac)[] layout =
        {
            ("sa",       0.18f, 0.90f), // high on the (patient's) right atrium
            ("atria",    0.05f, 0.75f),
            ("av",       0.00f, 0.55f), // AV node — centre, interatrial septum
            ("his",      0.00f, 0.45f),
            ("bundles",  0.00f, 0.30f),
            ("purkinje", 0.16f, 0.16f),
            ("apex",     0.00f, 0.08f), // apex
        };

        var path = new ConductionPath();
        foreach (var (key, dx, yFrac) in layout)
        {
            var stage = Template.First(s => s.Key == key);
            path.Nodes.Add(new ConductionNode
            {
                Key = stage.Key,
                LabelEn = stage.En,
                LabelRu = stage.Ru,
                ArrivalMs = stage.ArrivalMs,
                Anchor = new[] { cx + dx * w, bottom + yFrac * h, cz },
            });
        }
        return path;
    }

    /// <summary>
    /// Position of the depolarisation wave at <paramref name="tMs"/> into the cycle, or <c>null</c>
    /// once the wave has passed the last node (electrical diastole). <paramref name="stageIndex"/>
    /// receives the node the wave is currently entering, for the phase caption.
    /// </summary>
    public Vector3? Sample(float tMs, out int stageIndex)
    {
        stageIndex = -1;
        if (Nodes.Count == 0)
        {
            return null;
        }
        if (tMs <= Nodes[0].ArrivalMs)
        {
            stageIndex = 0;
            return Nodes[0].Position;
        }
        if (tMs >= TotalMs)
        {
            return null; // wave has completed — quiescent until the next beat
        }
        for (int i = 0; i < Nodes.Count - 1; i++)
        {
            float a = Nodes[i].ArrivalMs, b = Nodes[i + 1].ArrivalMs;
            if (tMs >= a && tMs < b)
            {
                stageIndex = i + 1;
                float f = b > a ? (tMs - a) / (b - a) : 0f;
                return Vector3.Lerp(Nodes[i].Position, Nodes[i + 1].Position, f);
            }
        }
        return null;
    }

    /// <summary>Cylinders along each segment plus a sphere at each node — the static pathway glyph.</summary>
    public static Geometry3D BuildPathGeometry(IReadOnlyList<ConductionNode> nodes, float tubeRadius, float nodeRadius)
    {
        var builder = new MeshBuilder(true, true, false);
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            var p1 = nodes[i].Position;
            var p2 = nodes[i + 1].Position;
            if (Vector3.Distance(p1, p2) > 1e-4f)
            {
                builder.AddCylinder(p1, p2, tubeRadius * 2f, 12);
            }
        }
        foreach (var node in nodes)
        {
            builder.AddSphere(node.Position, nodeRadius, 14, 14);
        }
        return builder.ToMeshGeometry3D();
    }

    /// <summary>A single sphere at <paramref name="center"/> — the travelling depolarisation pulse.</summary>
    public static Geometry3D BuildPulseGeometry(Vector3 center, float radius)
    {
        var builder = new MeshBuilder(true, true, false);
        builder.AddSphere(center, radius, 16, 16);
        return builder.ToMeshGeometry3D();
    }
}
