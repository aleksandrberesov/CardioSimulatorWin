using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Tests;

/// <summary>
/// Tests for <see cref="EikonalSolver"/>. The anchor is analytic: on a flat plane with unit speed the
/// geodesic arrival time equals the Euclidean distance, so the solver's output is checked against a
/// closed-form ground truth. FMM is required to beat the Dijkstra baseline's metrication error.
/// </summary>
public class EikonalSolverTests
{
    private const float Eps = 1e-4f;

    /// <summary>Builds an n x n regular grid on the z = 0 plane (spacing 1), split into two triangles
    /// per cell. Vertex (i, j) has index j * n + i and position (i, j, 0).</summary>
    private static SurfaceMesh BuildGrid(int n)
    {
        var positions = new Vector3[n * n];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                positions[j * n + i] = new Vector3(i, j, 0);
            }
        }
        var indices = new List<int>();
        int V(int i, int j) => j * n + i;
        for (int j = 0; j < n - 1; j++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                indices.Add(V(i, j)); indices.Add(V(i + 1, j)); indices.Add(V(i + 1, j + 1));
                indices.Add(V(i, j)); indices.Add(V(i + 1, j + 1)); indices.Add(V(i, j + 1));
            }
        }
        // Positions are already unique; welding is a no-op that also builds the adjacency we need.
        return SurfaceMesh.Weld(positions, indices.ToArray(), Eps, out _);
    }

    private static float MaxRelativeError(SurfaceMesh mesh, float[] time, Vector3 source)
    {
        float worst = 0f;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            float truth = Vector3.Distance(mesh.Positions[v], source);
            if (truth < 1e-3f)
            {
                continue; // skip the source itself
            }
            float rel = MathF.Abs(time[v] - truth) / truth;
            if (rel > worst)
            {
                worst = rel;
            }
        }
        return worst;
    }

    [Fact]
    public void Fmm_PlanarUnitSpeed_ApproximatesEuclideanDistance()
    {
        const int n = 41;
        var mesh = BuildGrid(n);
        var seeds = new[] { new EikonalSeed(0, 0f) }; // corner (0,0)
        var options = new EikonalOptions { DefaultSpeed = 1f };

        float[] fmm = new EikonalSolver(mesh).Solve(seeds, options);

        float error = MaxRelativeError(mesh, fmm, new Vector3(0, 0, 0));
        Assert.True(error < 0.04f, $"FMM max relative error {error:P2} should be under 4%");
    }

    [Fact]
    public void Fmm_IsMoreAccurateThanDijkstraBaseline()
    {
        const int n = 41;
        var mesh = BuildGrid(n);
        var seeds = new[] { new EikonalSeed(0, 0f) };
        var options = new EikonalOptions { DefaultSpeed = 1f };
        var solver = new EikonalSolver(mesh);

        float fmmError = MaxRelativeError(mesh, solver.Solve(seeds, options), Vector3.Zero);
        float dijkstraError = MaxRelativeError(mesh, solver.SolveDijkstra(seeds, options), Vector3.Zero);

        // Dijkstra snaps paths to edges (metrication error ~8% on a grid); FMM marches through faces.
        Assert.True(dijkstraError > fmmError,
            $"FMM error {fmmError:P2} should be below Dijkstra error {dijkstraError:P2}");
        Assert.True(dijkstraError > 0.05f, $"Dijkstra baseline error {dijkstraError:P2} should be visibly large");
    }

    [Fact]
    public void Solve_RespectsSpeed_ScalesArrivalTime()
    {
        const int n = 21;
        var mesh = BuildGrid(n);
        var seeds = new[] { new EikonalSeed(0, 0f) };

        float[] slow = new EikonalSolver(mesh).Solve(seeds, new EikonalOptions { DefaultSpeed = 0.5f });

        // Half the speed => double the arrival time everywhere (compare against unit-speed truth).
        int corner = (n - 1) * n + (n - 1);
        float truth = Vector3.Distance(mesh.Positions[corner], Vector3.Zero);
        Assert.Equal(truth / 0.5f, slow[corner], 1.0f);
    }

    [Fact]
    public void Solve_SeedOffset_ShiftsAllArrivalTimes()
    {
        const int n = 21;
        var mesh = BuildGrid(n);
        var seeds = new[] { new EikonalSeed(0, 100f) };
        var options = new EikonalOptions { DefaultSpeed = 1f };

        float[] time = new EikonalSolver(mesh).Solve(seeds, options);

        Assert.Equal(100f, time[0], 1e-3f); // the seed ignites at its offset
        int corner = (n - 1) * n + (n - 1);
        float truth = 100f + Vector3.Distance(mesh.Positions[corner], Vector3.Zero);
        Assert.Equal(truth, time[corner], 0.5f);
    }

    [Fact]
    public void Solve_TwoSeeds_TakesMinArrivalPerVertex()
    {
        const int n = 21;
        var mesh = BuildGrid(n);
        var options = new EikonalOptions { DefaultSpeed = 1f };
        var solver = new EikonalSolver(mesh);

        // Watershed semantics: a multi-source solve must equal the element-wise min of the individual
        // single-source solves. Comparing solver-to-solver (rather than to analytic distance) isolates
        // the min-arrival behaviour from the structured grid's directional (anisotropy) error.
        float[] fromA = solver.Solve(new[] { new EikonalSeed(0, 0f) }, options);
        float[] fromB = solver.Solve(new[] { new EikonalSeed(n - 1, 0f) }, options);
        float[] both = solver.Solve(new[] { new EikonalSeed(0, 0f), new EikonalSeed(n - 1, 0f) }, options);

        for (int v = 0; v < mesh.VertexCount; v++)
        {
            Assert.Equal(MathF.Min(fromA[v], fromB[v]), both[v], 1e-3f);
        }
    }

    [Fact]
    public void Solve_Barrier_ForcesDetour_AndBlockedVerticesStayInfinite()
    {
        const int n = 21;
        var mesh = BuildGrid(n);

        // Block the whole middle column (i == mid) EXCEPT the bottom row, leaving a single gap the wave
        // must funnel through. Seed top-left; target top-right is on the far side of the wall.
        int mid = n / 2;
        var blocked = new bool[mesh.VertexCount];
        for (int j = 1; j < n; j++)
        {
            blocked[j * n + mid] = true;
        }
        var options = new EikonalOptions { DefaultSpeed = 1f, Blocked = blocked };

        int seed = (n - 1) * n + 0;       // top-left
        int target = (n - 1) * n + (n - 1); // top-right
        float[] time = new EikonalSolver(mesh).Solve(new[] { new EikonalSeed(seed, 0f) }, options);

        // A blocked vertex (not a seed) never activates.
        Assert.True(float.IsPositiveInfinity(time[2 * n + mid]));

        // The detour down to the gap and back up is strictly longer than the straight-line distance.
        float straight = Vector3.Distance(mesh.Positions[target], mesh.Positions[seed]);
        Assert.True(time[target] > straight * 1.5f,
            $"barrier detour {time[target]:F1} should far exceed the straight-line {straight:F1}");
        Assert.False(float.IsPositiveInfinity(time[target]), "the target is still reachable through the gap");
    }

    [Fact]
    public void Solve_DisconnectedComponent_StaysInfinite()
    {
        // Two separate triangles; seed one, the other is unreachable.
        var positions = new List<Vector3>();
        var indices = new List<int>();
        void Tri(Vector3 a, Vector3 b, Vector3 c)
        {
            int s = positions.Count;
            positions.Add(a); positions.Add(b); positions.Add(c);
            indices.Add(s); indices.Add(s + 1); indices.Add(s + 2);
        }
        Tri(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        Tri(new Vector3(50, 0, 0), new Vector3(51, 0, 0), new Vector3(50, 1, 0));

        var mesh = SurfaceMesh.Weld(positions.ToArray(), indices.ToArray(), Eps, out _);
        float[] time = new EikonalSolver(mesh).Solve(new[] { new EikonalSeed(0, 0f) }, new EikonalOptions { DefaultSpeed = 1f });

        Assert.Equal(2, mesh.CountConnectedComponents());
        Assert.Contains(time, float.IsPositiveInfinity); // the far triangle never activates
    }

    [Fact]
    public void Solve_IsDeterministic()
    {
        const int n = 25;
        var mesh = BuildGrid(n);
        var seeds = new[] { new EikonalSeed(0, 0f), new EikonalSeed(n * n - 1, 30f) };
        var options = new EikonalOptions { DefaultSpeed = 1f };
        var solver = new EikonalSolver(mesh);

        Assert.Equal(solver.Solve(seeds, options), solver.Solve(seeds, options));
    }

    [Fact]
    public void Solve_NoSeeds_LeavesEverythingInfinite()
    {
        var mesh = BuildGrid(5);
        float[] time = new EikonalSolver(mesh).Solve(Array.Empty<EikonalSeed>(), new EikonalOptions { DefaultSpeed = 1f });
        Assert.All(time, t => Assert.True(float.IsPositiveInfinity(t)));
    }

    [Fact]
    public void Solve_OutOfRangeSeed_IsIgnored()
    {
        var mesh = BuildGrid(5);
        float[] time = new EikonalSolver(mesh).Solve(new[] { new EikonalSeed(9999, 0f) }, new EikonalOptions { DefaultSpeed = 1f });
        Assert.All(time, t => Assert.True(float.IsPositiveInfinity(t)));
    }

    [Fact]
    public void Solve_ValidatesOptionArrayLengths()
    {
        var mesh = BuildGrid(5);
        var solver = new EikonalSolver(mesh);
        var seeds = new[] { new EikonalSeed(0, 0f) };

        Assert.Throws<ArgumentException>(() =>
            solver.Solve(seeds, new EikonalOptions { VertexSpeed = new float[3] }));
        Assert.Throws<ArgumentException>(() =>
            solver.Solve(seeds, new EikonalOptions { Blocked = new bool[3] }));
    }
}
