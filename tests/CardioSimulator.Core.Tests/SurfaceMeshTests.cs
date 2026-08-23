using System.Collections.Generic;
using System.Numerics;
using CardioSimulator.Core.Domain;

namespace CardioSimulator.Core.Tests;

/// <summary>
/// Tests for <see cref="SurfaceMesh"/> — the welding + adjacency layer the eikonal solver builds on.
/// The load-bearing case is welding "triangle soup" (imported meshes duplicate shared vertices per
/// face) back into a single connected graph; without it no wavefront can propagate.
/// </summary>
public class SurfaceMeshTests
{
    private const float Eps = 1e-4f;

    // The 8 corners of a unit cube.
    private static readonly Vector3[] CubeCorners =
    {
        new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), // z = 0
        new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1), // z = 1
    };

    // Six quad faces (corner indices), each split into two triangles below. Every corner is used.
    private static readonly int[][] CubeQuads =
    {
        new[] { 0, 1, 2, 3 }, // bottom
        new[] { 4, 5, 6, 7 }, // top
        new[] { 0, 1, 5, 4 }, // front
        new[] { 2, 3, 7, 6 }, // back
        new[] { 1, 2, 6, 5 }, // right
        new[] { 3, 0, 4, 7 }, // left
    };

    /// <summary>Expands the cube into triangle soup: 12 triangles, each carrying its own 3 vertices.</summary>
    private static (Vector3[] positions, int[] indices) BuildCubeSoup()
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();
        foreach (var quad in CubeQuads)
        {
            // quad {a,b,c,d} -> triangles (a,b,c) and (a,c,d)
            AddSoupTriangle(positions, indices, CubeCorners[quad[0]], CubeCorners[quad[1]], CubeCorners[quad[2]]);
            AddSoupTriangle(positions, indices, CubeCorners[quad[0]], CubeCorners[quad[2]], CubeCorners[quad[3]]);
        }
        return (positions.ToArray(), indices.ToArray());
    }

    private static void AddSoupTriangle(List<Vector3> positions, List<int> indices, Vector3 a, Vector3 b, Vector3 c)
    {
        int start = positions.Count;
        positions.Add(a);
        positions.Add(b);
        positions.Add(c);
        indices.Add(start);
        indices.Add(start + 1);
        indices.Add(start + 2);
    }

    [Fact]
    public void Weld_TriangleSoupCube_MergesTo8ConnectedVertices()
    {
        var (positions, indices) = BuildCubeSoup();
        Assert.Equal(36, positions.Length); // soup: 12 triangles x 3 private vertices

        var mesh = SurfaceMesh.Weld(positions, indices, Eps, out _);

        Assert.Equal(8, mesh.VertexCount);              // merged back to the cube's 8 corners
        Assert.Equal(12, mesh.TriangleCount);           // all faces survive
        Assert.Equal(1, mesh.CountConnectedComponents()); // one connected surface
    }

    [Fact]
    public void Weld_RawToWelded_IsTotalAndSurjectiveAndScattersConsistently()
    {
        var (positions, indices) = BuildCubeSoup();
        var mesh = SurfaceMesh.Weld(positions, indices, Eps, out var rawToWelded);

        Assert.Equal(positions.Length, rawToWelded.Length); // one entry per raw vertex

        // Every welded index is reachable through the map (surjective onto 0..VertexCount).
        var seen = new HashSet<int>(rawToWelded);
        Assert.Equal(mesh.VertexCount, seen.Count);
        Assert.All(rawToWelded, wi => Assert.InRange(wi, 0, mesh.VertexCount - 1));

        // Scattering a per-welded value back to raw vertices: two raw vertices at the same position must
        // receive the same value (this is exactly how activation times land on render geometry).
        var perWelded = new float[mesh.VertexCount];
        for (int i = 0; i < perWelded.Length; i++)
        {
            perWelded[i] = i * 10f;
        }
        for (int i = 0; i < positions.Length; i++)
        {
            for (int j = i + 1; j < positions.Length; j++)
            {
                if (positions[i] == positions[j])
                {
                    Assert.Equal(perWelded[rawToWelded[i]], perWelded[rawToWelded[j]]);
                }
            }
        }
    }

    [Fact]
    public void Weld_MergesWithinEpsilon()
    {
        var positions = new[] { new Vector3(0, 0, 0), new Vector3(Eps * 0.5f, 0, 0) };
        var mesh = SurfaceMesh.Weld(positions, System.Array.Empty<int>(), Eps, out var map);

        Assert.Equal(1, mesh.VertexCount);
        Assert.Equal(map[0], map[1]);
    }

    [Fact]
    public void Weld_KeepsSeparateBeyondEpsilon()
    {
        var positions = new[] { new Vector3(0, 0, 0), new Vector3(Eps * 2f, 0, 0) };
        var mesh = SurfaceMesh.Weld(positions, System.Array.Empty<int>(), Eps, out var map);

        Assert.Equal(2, mesh.VertexCount);
        Assert.NotEqual(map[0], map[1]);
    }

    [Fact]
    public void Weld_DropsTrianglesThatCollapseAfterWelding()
    {
        // One valid triangle, plus a triangle whose first two vertices are coincident (collapses to a
        // degenerate edge once welded) and must be dropped.
        var positions = new List<Vector3>();
        var indices = new List<int>();
        AddSoupTriangle(positions, indices, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        AddSoupTriangle(positions, indices, new Vector3(5, 0, 0), new Vector3(5, 0, 0), new Vector3(6, 0, 0));

        var mesh = SurfaceMesh.Weld(positions.ToArray(), indices.ToArray(), Eps, out _);

        Assert.Equal(1, mesh.TriangleCount);
    }

    [Fact]
    public void Weld_DropsTrianglesWithOutOfRangeIndices()
    {
        var positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
        var indices = new[] { 0, 1, 2, /* bogus: */ 0, 1, 99 };

        var mesh = SurfaceMesh.Weld(positions, indices, Eps, out _);

        Assert.Equal(1, mesh.TriangleCount);
    }

    [Fact]
    public void CountConnectedComponents_CountsSeparatePieces()
    {
        // Two triangles far apart -> two disconnected surfaces.
        var positions = new List<Vector3>();
        var indices = new List<int>();
        AddSoupTriangle(positions, indices, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0));
        AddSoupTriangle(positions, indices, new Vector3(50, 0, 0), new Vector3(51, 0, 0), new Vector3(50, 1, 0));

        var mesh = SurfaceMesh.Weld(positions.ToArray(), indices.ToArray(), Eps, out _);

        Assert.Equal(6, mesh.VertexCount);
        Assert.Equal(2, mesh.TriangleCount);
        Assert.Equal(2, mesh.CountConnectedComponents());
    }

    [Fact]
    public void IncidentTriangles_ReportsEveryFaceTouchingAVertex()
    {
        var (positions, indices) = BuildCubeSoup();
        var mesh = SurfaceMesh.Weld(positions, indices, Eps, out _);

        // Each cube corner belongs to 3 faces, triangulated into either 4, 5, or 6 triangles depending
        // on how the shared diagonal falls. Cross-check the CSR adjacency against a brute-force scan.
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            int bruteForce = 0;
            for (int t = 0; t < mesh.TriangleCount; t++)
            {
                if (mesh.Triangles[t * 3] == v || mesh.Triangles[t * 3 + 1] == v || mesh.Triangles[t * 3 + 2] == v)
                {
                    bruteForce++;
                }
            }
            Assert.Equal(bruteForce, mesh.IncidentTriangles(v).Length);
            Assert.True(mesh.IncidentTriangles(v).Length > 0, $"corner {v} should touch at least one face");
        }
    }

    [Fact]
    public void NearestVertex_ReturnsClosestByEuclideanDistance()
    {
        // No triangles: welded order follows input order, so indices are predictable.
        var positions = new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0) };
        var mesh = SurfaceMesh.Weld(positions, System.Array.Empty<int>(), Eps, out _);

        Assert.Equal(1, mesh.NearestVertex(new Vector3(9, 1, 0)));
        Assert.Equal(0, mesh.NearestVertex(new Vector3(1, 1, 0)));
        Assert.Equal(2, mesh.NearestVertex(new Vector3(0, 8, 0)));
    }

    /// <summary>Builds an n x n grid on z=0 (spacing 1); vertex (i,j) is index j*n+i at (i,j,0).</summary>
    private static SurfaceMesh BuildGrid(int n)
    {
        var positions = new List<Vector3>();
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                positions.Add(new Vector3(i, j, 0));
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
        return SurfaceMesh.Weld(positions.ToArray(), indices.ToArray(), Eps, out _);
    }

    [Fact]
    public void ComputeVertexNormals_PlanarGrid_AllPlusZ()
    {
        var mesh = BuildGrid(6);
        foreach (var nrm in mesh.ComputeVertexNormals())
        {
            Assert.True(Vector3.Dot(nrm, new Vector3(0, 0, 1)) > 0.99f, $"expected +Z normal, got {nrm}");
        }
    }

    [Fact]
    public void ComputeVertexGradient_LinearField_IsConstant()
    {
        const int n = 6;
        var mesh = BuildGrid(n);
        // f = x  ->  gradient must be (1,0,0) everywhere.
        var values = new float[mesh.VertexCount];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            values[v] = mesh.Positions[v].X;
        }
        var grad = mesh.ComputeVertexGradient(values);
        foreach (var g in grad)
        {
            Assert.True((g - new Vector3(1, 0, 0)).Length() < 1e-3f, $"expected (1,0,0), got {g}");
        }
    }

    [Fact]
    public void ComputeVertexGradient_RadialField_PointsOutwardUnitMagnitude()
    {
        const int n = 21;
        var mesh = BuildGrid(n);
        var origin = new Vector3(0, 0, 0);
        // f = distance from a corner -> gradient points radially outward with magnitude ~1.
        var values = new float[mesh.VertexCount];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            values[v] = Vector3.Distance(mesh.Positions[v], origin);
        }
        var grad = mesh.ComputeVertexGradient(values);
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var radial = mesh.Positions[v] - origin;
            if (radial.Length() < 3f)
            {
                continue; // skip near the singularity at the source
            }
            var g = grad[v];
            Assert.InRange(g.Length(), 0.9f, 1.1f);
            var expected = Vector3.Normalize(radial);
            Assert.True(Vector3.Dot(Vector3.Normalize(g), expected) > 0.9f, $"grad {g} should align with {expected}");
        }
    }

    [Fact]
    public void SolveLaplace_PinnedEnds_GivesMonotonicFieldWithAxisGradient()
    {
        const int n = 15;
        var mesh = BuildGrid(n);
        // Pin the left edge (x=0) to 0 and the right edge (x=n-1) to 1. The uniform-weight harmonic
        // field is smooth and monotonic in x; its GRADIENT direction (what the fibre model uses) tracks
        // the +x long axis. (Exact linearity would need cotangent weights; not needed here.)
        var mask = new bool[mesh.VertexCount];
        var vals = new float[mesh.VertexCount];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            float x = mesh.Positions[v].X;
            if (x <= 0.5f) { mask[v] = true; vals[v] = 0f; }
            else if (x >= n - 1.5f) { mask[v] = true; vals[v] = 1f; }
        }
        var phi = mesh.SolveLaplace(mask, vals, 500);

        // Field stays within the pinned range and rises monotonically along a middle row.
        foreach (var p in phi)
        {
            Assert.InRange(p, -1e-3f, 1f + 1e-3f);
        }
        int mid = n / 2;
        float prev = -1f;
        for (int i = 0; i < n; i++)
        {
            float cur = phi[mid * n + i];
            Assert.True(cur >= prev - 1e-4f, $"phi should be monotonic along x; {cur} < {prev} at i={i}");
            prev = cur;
        }

        // Gradient of the field points along +x for interior vertices.
        var grad = mesh.ComputeVertexGradient(phi);
        int interior = 0;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            var p = mesh.Positions[v];
            if (p.X < 3 || p.X > n - 4 || p.Y < 3 || p.Y > n - 4) continue;
            interior++;
            Assert.True(Vector3.Dot(Vector3.Normalize(grad[v]), new Vector3(1, 0, 0)) > 0.9f,
                $"gradient should point +x, got {grad[v]}");
        }
        Assert.True(interior > 0);
    }

    [Fact]
    public void ComputeVertexGradient_NonFiniteValues_ProduceNoNaN()
    {
        var mesh = BuildGrid(6);
        var values = new float[mesh.VertexCount];
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            values[v] = v == 10 ? float.PositiveInfinity : mesh.Positions[v].X;
        }
        var grad = mesh.ComputeVertexGradient(values);
        foreach (var g in grad)
        {
            Assert.False(float.IsNaN(g.X) || float.IsNaN(g.Y) || float.IsNaN(g.Z), "gradient must not contain NaN");
        }
    }

    [Fact]
    public void Weld_EmptyInput_IsGraceful()
    {
        var mesh = SurfaceMesh.Weld(System.Array.Empty<Vector3>(), System.Array.Empty<int>(), Eps, out var map);

        Assert.Equal(0, mesh.VertexCount);
        Assert.Equal(0, mesh.TriangleCount);
        Assert.Equal(0, mesh.CountConnectedComponents());
        Assert.Equal(-1, mesh.NearestVertex(Vector3.Zero));
        Assert.Empty(map);
    }
}
