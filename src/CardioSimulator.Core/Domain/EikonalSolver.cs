using System.Numerics;

namespace CardioSimulator.Core.Domain;

/// <summary>A wave source: a mesh vertex that ignites at <see cref="TimeOffsetMs"/> milliseconds.</summary>
public readonly record struct EikonalSeed(int VertexIndex, float TimeOffsetMs);

/// <summary>
/// Conduction properties for one solve. <see cref="DefaultSpeed"/> and <see cref="VertexSpeed"/> are in
/// model units per millisecond (so activation time comes out in ms). A vertex is non-conducting when it
/// is marked in <see cref="Blocked"/> or its speed is &lt;= 0 — that is how an infarct / scar creates a
/// conduction block the wavefront must route around.
/// </summary>
public sealed class EikonalOptions
{
    /// <summary>Speed used where <see cref="VertexSpeed"/> gives nothing. Must be &gt; 0.</summary>
    public float DefaultSpeed { get; set; } = 1f;

    /// <summary>Optional per-vertex speed (length must equal the mesh vertex count); null ⇒ uniform.</summary>
    public float[]? VertexSpeed { get; set; }

    /// <summary>Optional per-vertex conduction block (length must equal the mesh vertex count).</summary>
    public bool[]? Blocked { get; set; }
}

/// <summary>
/// Solves the eikonal equation |∇T| = 1/F on a triangulated surface, producing a per-vertex activation
/// (arrival) time in milliseconds — the isochrone map that drives the depolarisation-wavefront render.
///
/// The default <see cref="Solve"/> uses the Fast Marching Method (Sethian) with a triangle-based local
/// solver (Kimmel &amp; Sethian, "Computing geodesic paths on manifolds"): each triangle updates its
/// vertex from the wavefront passing through the face, which curves the isochrones correctly, with an
/// edge (one-dimensional) update as the always-valid fallback for obtuse triangles. Sources may be
/// multiple and carry independent ignition offsets, so <c>time[v] = min over seeds of (offset +
/// geodesicTime(seed → v))</c>. Unreachable or blocked vertices are <see cref="float.PositiveInfinity"/>.
///
/// <see cref="SolveDijkstra"/> is a deliberately naive edge-graph baseline kept for tests: it exhibits
/// the classic metrication error (paths snap to mesh edges), so the FMM accuracy tests assert a
/// <em>lower</em> error against analytic distance than Dijkstra, proving the triangle solver earns its
/// complexity.
///
/// Deterministic (no clocks / RNG) so results are cacheable and unit-testable. Platform-neutral so the
/// algorithm mirrors verbatim into the Android renderer, per <see cref="InfarctTextureBlender"/>.
/// </summary>
public sealed class EikonalSolver
{
    private const byte Far = 0;
    private const byte Considered = 1;
    private const byte Accepted = 2;

    private readonly SurfaceMesh _mesh;

    public EikonalSolver(SurfaceMesh mesh) => _mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));

    /// <summary>
    /// Fast Marching Method solve. Returns activation time (ms) per vertex; unreachable/blocked vertices
    /// are <see cref="float.PositiveInfinity"/>.
    /// </summary>
    public float[] Solve(IReadOnlyList<EikonalSeed> seeds, EikonalOptions options)
    {
        int n = _mesh.VertexCount;
        ValidateOptions(options, n);

        var time = new float[n];
        Array.Fill(time, float.PositiveInfinity);
        var state = new byte[n]; // all Far
        var band = new PriorityQueue<int, float>();

        SeedBand(seeds, time, state, band);

        while (band.TryDequeue(out int u, out float key))
        {
            if (state[u] == Accepted)
            {
                continue; // stale entry left over from a decrease-key (PriorityQueue has none)
            }
            state[u] = Accepted; // time[u] is final: it is the smallest value ever enqueued for u

            // Update the not-yet-frozen vertices of every triangle touching u, using u (and the third
            // vertex of the triangle when it too is frozen — that enables the two-point face update).
            foreach (int t in _mesh.IncidentTriangles(u))
            {
                int b = t * 3;
                int a0 = _mesh.Triangles[b];
                int a1 = _mesh.Triangles[b + 1];
                int a2 = _mesh.Triangles[b + 2];
                // The two triangle vertices other than u.
                int v1, v2;
                if (a0 == u) { v1 = a1; v2 = a2; }
                else if (a1 == u) { v1 = a0; v2 = a2; }
                else { v1 = a0; v2 = a1; } // a2 == u

                UpdateVertex(v1, u, v2, time, state, band, options);
                UpdateVertex(v2, u, v1, time, state, band, options);
            }
        }
        return time;
    }

    /// <summary>
    /// Baseline: multi-source Dijkstra over the triangle-edge graph (travel time = edgeLength / speed).
    /// Correct with respect to connectivity and blocks, but overestimates geodesic time (metrication
    /// error). Kept internal for the FMM accuracy tests to compare against.
    /// </summary>
    internal float[] SolveDijkstra(IReadOnlyList<EikonalSeed> seeds, EikonalOptions options)
    {
        int n = _mesh.VertexCount;
        ValidateOptions(options, n);

        var time = new float[n];
        Array.Fill(time, float.PositiveInfinity);
        var settled = new bool[n];
        var band = new PriorityQueue<int, float>();

        foreach (var seed in seeds)
        {
            if ((uint)seed.VertexIndex < (uint)n && seed.TimeOffsetMs < time[seed.VertexIndex])
            {
                time[seed.VertexIndex] = seed.TimeOffsetMs;
                band.Enqueue(seed.VertexIndex, seed.TimeOffsetMs);
            }
        }

        while (band.TryDequeue(out int u, out float key))
        {
            if (settled[u])
            {
                continue;
            }
            settled[u] = true;
            var pu = _mesh.Positions[u];

            foreach (int t in _mesh.IncidentTriangles(u))
            {
                int b = t * 3;
                for (int k = 0; k < 3; k++)
                {
                    int w = _mesh.Triangles[b + k];
                    if (w == u || settled[w])
                    {
                        continue;
                    }
                    float speed = SpeedAt(w, options);
                    if (speed <= 0f)
                    {
                        continue; // blocked: cannot enter
                    }
                    float cand = time[u] + Vector3.Distance(pu, _mesh.Positions[w]) / speed;
                    if (cand < time[w])
                    {
                        time[w] = cand;
                        band.Enqueue(w, cand);
                    }
                }
            }
        }
        return time;
    }

    private void SeedBand(IReadOnlyList<EikonalSeed> seeds, float[] time, byte[] state, PriorityQueue<int, float> band)
    {
        int n = time.Length;
        foreach (var seed in seeds)
        {
            if ((uint)seed.VertexIndex >= (uint)n)
            {
                continue;
            }
            // Seeds enter the band at their ignition offset; the loop freezes them (or an earlier route
            // to them) in time order, so multiple sources naturally yield the min arrival everywhere.
            if (seed.TimeOffsetMs < time[seed.VertexIndex])
            {
                time[seed.VertexIndex] = seed.TimeOffsetMs;
                state[seed.VertexIndex] = Considered;
                band.Enqueue(seed.VertexIndex, seed.TimeOffsetMs);
            }
        }
    }

    /// <summary>
    /// Tries to lower the arrival time at <paramref name="target"/> using the just-frozen
    /// <paramref name="from"/> vertex — a one-dimensional edge update, plus a two-point face update when
    /// <paramref name="third"/> is also frozen. The smallest valid candidate wins.
    /// </summary>
    private void UpdateVertex(int target, int from, int third, float[] time, byte[] state, PriorityQueue<int, float> band, EikonalOptions options)
    {
        if (state[target] == Accepted)
        {
            return;
        }
        float speed = SpeedAt(target, options);
        if (speed <= 0f)
        {
            return; // blocked tissue never activates via propagation
        }
        float slowness = 1f / speed;

        var pTarget = _mesh.Positions[target];
        var pFrom = _mesh.Positions[from];

        // Edge (1-D) update — always valid.
        double candidate = time[from] + Vector3.Distance(pTarget, pFrom) * slowness;

        // Two-point (2-D) face update, only when the third vertex is frozen and it improves on the edge.
        if (state[third] == Accepted)
        {
            double face = TwoPointUpdate(pTarget, pFrom, _mesh.Positions[third], time[from], time[third], slowness);
            if (face < candidate)
            {
                candidate = face;
            }
        }

        if (candidate < time[target])
        {
            time[target] = (float)candidate;
            if (state[target] == Far)
            {
                state[target] = Considered;
            }
            band.Enqueue(target, (float)candidate); // stale prior entries are filtered on dequeue
        }
    }

    /// <summary>
    /// The Bornemann–Rasch quadratic update for the arrival time at <paramref name="pTarget"/> given the
    /// two frozen triangle vertices and the local slowness. Returns <see cref="double.PositiveInfinity"/>
    /// when the update is invalid (degenerate triangle, no real root, causality violated, or the
    /// characteristic falls outside the triangle — the obtuse case), so the caller keeps the edge update.
    /// </summary>
    private static double TwoPointUpdate(Vector3 pTarget, Vector3 pA, Vector3 pB, double tA, double tB, double slowness)
    {
        // Edge vectors from the update vertex to the two frozen vertices.
        Vector3 eA = pA - pTarget;
        Vector3 eB = pB - pTarget;

        double m11 = Vector3.Dot(eA, eA);
        double m12 = Vector3.Dot(eA, eB);
        double m22 = Vector3.Dot(eB, eB);
        double det = m11 * m22 - m12 * m12;
        if (det <= 1e-18)
        {
            return double.PositiveInfinity; // colinear / degenerate: no face to march through
        }

        // Solve  a·T² − 2B·T + C = 0, where the coefficients are quadratic forms in M⁻¹.
        // M⁻¹ = (1/det)·[[m22, −m12], [−m12, m11]].
        double a = (m11 + m22 - 2 * m12) / det;                       // 1ᵀ M⁻¹ 1
        double B = (m22 * tA - m12 * tB - m12 * tA + m11 * tB) / det; // 1ᵀ M⁻¹ c
        double cQuad = (tA * (m22 * tA - m12 * tB) + tB * (m11 * tB - m12 * tA)) / det - slowness * slowness;

        double disc = B * B - a * cQuad;
        if (disc < 0 || a <= 0)
        {
            return double.PositiveInfinity;
        }
        double t0 = (B + Math.Sqrt(disc)) / a; // larger root

        // Causality: the front must reach the target after both frozen vertices.
        if (t0 < tA || t0 < tB)
        {
            return double.PositiveInfinity;
        }

        // Monotonicity: the characteristic direction must point into the triangle wedge, i.e. the
        // barycentric weights λ = M⁻¹ (t0·1 − c) are both non-negative. Otherwise the true minimiser is
        // on an edge and the (always-valid) edge update should govern.
        double dA = t0 - tA;
        double dB = t0 - tB;
        double lambdaA = (m22 * dA - m12 * dB) / det;
        double lambdaB = (m11 * dB - m12 * dA) / det;
        if (lambdaA < 0 || lambdaB < 0)
        {
            return double.PositiveInfinity;
        }
        return t0;
    }

    private static float SpeedAt(int vertex, EikonalOptions options)
    {
        if (options.Blocked is { } blocked && blocked[vertex])
        {
            return 0f;
        }
        float speed = options.VertexSpeed is { } vs ? vs[vertex] : options.DefaultSpeed;
        return speed > 0f ? speed : 0f;
    }

    private void ValidateOptions(EikonalOptions options, int n)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        if (options.DefaultSpeed <= 0f && options.VertexSpeed is null)
        {
            throw new ArgumentException("DefaultSpeed must be positive when no per-vertex speed is given.", nameof(options));
        }
        if (options.VertexSpeed is { } vs && vs.Length != n)
        {
            throw new ArgumentException($"VertexSpeed length ({vs.Length}) must equal the mesh vertex count ({n}).", nameof(options));
        }
        if (options.Blocked is { } blocked && blocked.Length != n)
        {
            throw new ArgumentException($"Blocked length ({blocked.Length}) must equal the mesh vertex count ({n}).", nameof(options));
        }
    }
}
