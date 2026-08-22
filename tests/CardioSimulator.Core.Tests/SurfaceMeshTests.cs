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
