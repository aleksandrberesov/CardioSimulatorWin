using System.Numerics;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// A welded, indexed triangle surface — the connected graph the eikonal wavefront solver
/// (<c>EikonalSolver</c>, added in a later milestone) marches over. <see cref="Positions"/> holds the
/// unique vertices; <see cref="Triangles"/> holds three indices per face into <see cref="Positions"/>;
/// per-vertex incidence (vertex → the triangles that touch it) is precomputed once into a compact CSR
/// layout so neighbour iteration during the march allocates nothing.
///
/// The point of this type is <b>welding</b>. Models imported from OBJ/glTF/FBX are almost always
/// "triangle soup": each face carries its own private copies of the vertices it shares with its
/// neighbours, so a graph built straight from the imported indices is fully disconnected and no wave
/// can propagate across it. <see cref="Weld"/> merges spatially-coincident vertices into one connected
/// graph and hands back a <c>rawToWelded</c> map so per-vertex results (activation times) can be
/// scattered back onto the original render vertices.
///
/// Deliberately platform-neutral (only <see cref="System.Numerics"/>) so it is unit-tested here and
/// mirrored in the Android renderer — the same convention as <see cref="InfarctTextureBlender"/>.
/// </summary>
public sealed class SurfaceMesh
{
    /// <summary>Unique vertex positions (coincident duplicates from the import were merged).</summary>
    public Vector3[] Positions { get; }

    /// <summary>Three welded-vertex indices per triangle; length is always a multiple of three.</summary>
    public int[] Triangles { get; }

    // CSR (compressed sparse row) adjacency: the triangles incident to vertex v are
    // _adjTriangles[_adjStart[v] .. _adjStart[v + 1]]. _adjStart has VertexCount + 1 entries.
    private readonly int[] _adjStart;
    private readonly int[] _adjTriangles;

    public int VertexCount => Positions.Length;

    public int TriangleCount => Triangles.Length / 3;

    /// <summary>The triangles that touch <paramref name="vertex"/>, as a zero-allocation span.</summary>
    public ReadOnlySpan<int> IncidentTriangles(int vertex)
        => _adjTriangles.AsSpan(_adjStart[vertex], _adjStart[vertex + 1] - _adjStart[vertex]);

    private SurfaceMesh(Vector3[] positions, int[] triangles)
    {
        Positions = positions;
        Triangles = triangles;

        int vertexCount = positions.Length;
        int triCount = triangles.Length / 3;

        // First pass: count how many triangles touch each vertex, then prefix-sum into start offsets.
        _adjStart = new int[vertexCount + 1];
        for (int i = 0; i < triangles.Length; i++)
        {
            _adjStart[triangles[i] + 1]++;
        }
        for (int v = 0; v < vertexCount; v++)
        {
            _adjStart[v + 1] += _adjStart[v];
        }

        // Second pass: scatter each triangle id into the slot of each of its three vertices. A per-vertex
        // write cursor starts at that vertex's offset and advances as entries are placed.
        _adjTriangles = new int[triangles.Length]; // == 3 * triCount, three incidences per triangle
        var cursor = new int[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            cursor[v] = _adjStart[v];
        }
        for (int t = 0; t < triCount; t++)
        {
            int b = t * 3;
            _adjTriangles[cursor[triangles[b]]++] = t;
            _adjTriangles[cursor[triangles[b + 1]]++] = t;
            _adjTriangles[cursor[triangles[b + 2]]++] = t;
        }
    }

    /// <summary>
    /// Welds vertices closer than <paramref name="weldEpsilon"/> (in model units) into one, producing a
    /// connected mesh, and fills <paramref name="rawToWelded"/> with the welded index of every input
    /// vertex (same length as <paramref name="rawPositions"/>) so callers can map results back to their
    /// render geometry. Triangles are remapped through the weld; any that collapse (two corners merged)
    /// or reference an out-of-range index are dropped. Every input vertex survives as a welded vertex
    /// even if no surviving triangle references it, so <paramref name="rawToWelded"/> is always total.
    /// </summary>
    /// <param name="rawPositions">Imported vertex positions (typically already in world space).</param>
    /// <param name="rawIndices">Imported triangle indices; a trailing partial triangle is ignored.</param>
    /// <param name="weldEpsilon">Merge radius. Values &lt;= 0 weld only exactly-equal positions.</param>
    /// <param name="rawToWelded">Output: input-vertex index → welded-vertex index.</param>
    public static SurfaceMesh Weld(
        ReadOnlySpan<Vector3> rawPositions,
        ReadOnlySpan<int> rawIndices,
        float weldEpsilon,
        out int[] rawToWelded)
    {
        int rawCount = rawPositions.Length;
        rawToWelded = new int[rawCount];
        var welded = new List<Vector3>(rawCount);

        if (weldEpsilon > 0f)
        {
            // Spatial hash with cell size == epsilon. Two points within epsilon of each other differ by
            // at most one cell per axis, so searching the 3x3x3 neighbourhood is guaranteed to find an
            // existing representative if one is in range; the squared-distance test does the final gating.
            float epsSq = weldEpsilon * weldEpsilon;
            var grid = new Dictionary<(long X, long Y, long Z), List<int>>();

            for (int i = 0; i < rawCount; i++)
            {
                var p = rawPositions[i];
                long cx = (long)MathF.Floor(p.X / weldEpsilon);
                long cy = (long)MathF.Floor(p.Y / weldEpsilon);
                long cz = (long)MathF.Floor(p.Z / weldEpsilon);

                int found = -1;
                for (long dx = -1; dx <= 1 && found < 0; dx++)
                {
                    for (long dy = -1; dy <= 1 && found < 0; dy++)
                    {
                        for (long dz = -1; dz <= 1 && found < 0; dz++)
                        {
                            if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var bucket))
                            {
                                continue;
                            }
                            foreach (int wi in bucket)
                            {
                                if (Vector3.DistanceSquared(welded[wi], p) <= epsSq)
                                {
                                    found = wi;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (found < 0)
                {
                    found = welded.Count;
                    welded.Add(p);
                    var key = (cx, cy, cz);
                    if (!grid.TryGetValue(key, out var ownBucket))
                    {
                        ownBucket = new List<int>(1);
                        grid[key] = ownBucket;
                    }
                    ownBucket.Add(found);
                }
                rawToWelded[i] = found;
            }
        }
        else
        {
            // Exact-coincidence weld: Vector3 has value equality, so a plain dictionary suffices.
            var exact = new Dictionary<Vector3, int>();
            for (int i = 0; i < rawCount; i++)
            {
                var p = rawPositions[i];
                if (!exact.TryGetValue(p, out int wi))
                {
                    wi = welded.Count;
                    welded.Add(p);
                    exact[p] = wi;
                }
                rawToWelded[i] = wi;
            }
        }

        // Remap triangles through the weld, dropping collapsed / out-of-range faces.
        int inputTriCount = rawIndices.Length / 3;
        var triangles = new List<int>(inputTriCount * 3);
        for (int t = 0; t < inputTriCount; t++)
        {
            int r0 = rawIndices[t * 3];
            int r1 = rawIndices[t * 3 + 1];
            int r2 = rawIndices[t * 3 + 2];
            if ((uint)r0 >= (uint)rawCount || (uint)r1 >= (uint)rawCount || (uint)r2 >= (uint)rawCount)
            {
                continue; // defensive: a stray index outside the vertex range
            }
            int a = rawToWelded[r0];
            int b = rawToWelded[r1];
            int c = rawToWelded[r2];
            if (a == b || b == c || a == c)
            {
                continue; // two corners welded together → zero-area, no surface to conduct across
            }
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }

        return new SurfaceMesh(welded.ToArray(), triangles.ToArray());
    }

    /// <summary>
    /// Index of the vertex nearest <paramref name="worldPoint"/>, or -1 if the mesh is empty. Brute
    /// force O(V) — used to snap a handful of conduction-node seeds onto the mesh, so this is not hot.
    /// </summary>
    public int NearestVertex(Vector3 worldPoint)
    {
        if (Positions.Length == 0)
        {
            return -1;
        }
        int best = 0;
        float bestSq = Vector3.DistanceSquared(Positions[0], worldPoint);
        for (int i = 1; i < Positions.Length; i++)
        {
            float d = Vector3.DistanceSquared(Positions[i], worldPoint);
            if (d < bestSq)
            {
                bestSq = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Number of connected components over the vertex-adjacency graph (two vertices are adjacent when a
    /// triangle joins them). A well-formed single heart mesh returns 1; the solver uses this to warn
    /// when a wave cannot reach part of the surface. A vertex touched by no surviving triangle is its
    /// own isolated component.
    /// </summary>
    public int CountConnectedComponents()
    {
        int vertexCount = Positions.Length;
        if (vertexCount == 0)
        {
            return 0;
        }

        var visited = new bool[vertexCount];
        var stack = new Stack<int>();
        int components = 0;

        for (int start = 0; start < vertexCount; start++)
        {
            if (visited[start])
            {
                continue;
            }
            components++;
            visited[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                foreach (int t in IncidentTriangles(u))
                {
                    int b = t * 3;
                    for (int k = 0; k < 3; k++)
                    {
                        int w = Triangles[b + k];
                        if (!visited[w])
                        {
                            visited[w] = true;
                            stack.Push(w);
                        }
                    }
                }
            }
        }
        return components;
    }
}
