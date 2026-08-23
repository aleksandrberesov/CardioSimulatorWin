# Implementation Plan: Eikonal Wavefront Solver (Core)

## Motivation

The 3D heart dialog already has a "Wavefront view" (`Heart3DDialog.ToggleWavefront` / `AdvanceWavefront`), but the activation time for each vertex is computed with a **straight-line Euclidean distance** from the conduction nodes:

```csharp
// Heart3DDialog.PrecomputeWavefront (current)
float dist = Vector3.Distance(worldPos, node.Position);
float t = node.ArrivalMs + dist / speed;      // min over all nodes
```

This is not a wavefront across *tissue*: the wave "teleports" across chamber cavities, ignores mesh connectivity, cannot be blocked by an infarct, and cannot model regional conduction-velocity differences. It also produces isotropic spherical isochrones that look wrong on a real heart.

This plan replaces the Euclidean distance with a **geodesic arrival time computed by solving the eikonal equation on the myocardial surface mesh** — the standard model for cardiac activation-time (isochrone) maps.

We solve

> |∇T(x)| = 1 / F(x),  with T = seedOffset at the source vertices

where `T` is activation time (ms), `F(x)` is local conduction speed (model-units/ms). Sources are the conduction-system nodes (SA → AV → His → Purkinje), each seeded at its clinical `ArrivalMs`. The solver walks the wave **along the mesh graph**, so it respects tissue topology, can slow/stop in scar, and yields smoothly curved isochrones.

The solver is a **pure, platform-neutral algorithm in `CardioSimulator.Core.Domain`** — no HelixToolkit, no DirectX, only `System.Numerics`. This mirrors the `InfarctTextureBlender` precedent (Core owns the math so it is unit-tested here and hand-ported to Android).

---

## User Review Required

> [!IMPORTANT]
> **Scope decision (defaulted, please confirm):** This plan delivers the *solver in Core plus its wiring into the existing wavefront animation*. It does **not** change the rendering path (still per-vertex Gouraud colors via `geom.Colors`) or move color mapping to a GPU shader — those remain separate follow-ups. The solver makes the wavefront *physiologically correct*; the shader work makes it *fast on dense meshes*. They are independent.

> [!IMPORTANT]
> **Android parity:** The Core solver is C#; Android has its own Kotlin renderer. An Android Parity Sync Plan will be produced at the end so the same eikonal math is mirrored in the Android 3D heart view (same convention as the infarct-texture parity plan).

---

## Open Questions (defaulted with a recommendation — confirm or override)

1. **Keep the old Euclidean path as a fallback?**
   *Recommendation:* No. Replace it outright; the eikonal result is a strict superset (Euclidean is just eikonal on a fully-connected isotropic graph). Keeps one code path.
2. **Single combined graph across all heart meshes, or solve per-mesh?**
   *Recommendation:* Start **per anatomical mesh** (matches the current `_activationTimes[mesh]` structure and is simplest), with each mesh seeded from its nearest conduction node. Cross-mesh propagation (atria→ventricle across separate meshes) is a known limitation handled by seeding, upgradeable later via explicit AV-junction bridge links (see Risks).
3. **Is infarct conduction-block in scope now?**
   *Recommendation:* Build the speed-field/barrier API now (cheap), but wire the infarct-region → blocked-vertices coupling as the *last* milestone (M4), since it depends on mapping the infarct texture mask to vertices.

---

## Proposed Changes

### CardioSimulator.Core

#### [NEW] `src/CardioSimulator.Core/Domain/SurfaceMesh.cs`

A platform-neutral triangle mesh with the vertex-welding and adjacency the solver needs. **Welding is critical**: imported OBJ/glTF/FBX meshes are usually "triangle soup" — every triangle carries its own copies of shared vertices, so a naive graph is fully disconnected and no wave can propagate. `SurfaceMesh.Weld` merges spatially-coincident vertices (spatial hash on quantized position) into one connected graph, and returns the `rawToWelded` map so the App can scatter results back onto its render vertices.

```csharp
namespace CardioSimulator.Core.Domain;

using System.Numerics;

/// <summary>
/// A welded, indexed triangle surface used by <see cref="EikonalSolver"/>. Positions are unique
/// (coincident duplicates from triangle-soup imports are merged); <see cref="Triangles"/> holds three
/// indices per face into <see cref="Positions"/>. Adjacency (vertex → incident triangles) is built once.
/// Platform-neutral: only System.Numerics, so it unit-tests here and ports verbatim to Android.
/// </summary>
public sealed class SurfaceMesh
{
    public Vector3[] Positions { get; }
    public int[] Triangles { get; }          // length % 3 == 0

    // vertex index -> incident triangle ids (CSR: Adjacency[AdjStart[v]..AdjStart[v+1]])
    private readonly int[] _adjTriangles;
    private readonly int[] _adjStart;

    public int VertexCount => Positions.Length;
    public int TriangleCount => Triangles.Length / 3;
    public ReadOnlySpan<int> IncidentTriangles(int vertex) =>
        _adjTriangles.AsSpan(_adjStart[vertex], _adjStart[vertex + 1] - _adjStart[vertex]);

    private SurfaceMesh(Vector3[] positions, int[] triangles) { /* build CSR adjacency */ }

    /// <summary>
    /// Welds coincident vertices within <paramref name="weldEpsilon"/> (model units) and returns the
    /// connected mesh plus a map from each raw input vertex to its welded index (for scattering
    /// per-vertex results back onto render geometry). Degenerate triangles are dropped.
    /// </summary>
    public static SurfaceMesh Weld(
        ReadOnlySpan<Vector3> rawPositions,
        ReadOnlySpan<int> rawIndices,
        float weldEpsilon,
        out int[] rawToWelded);

    /// <summary>Nearest welded vertex to <paramref name="worldPoint"/> (brute force; used for a
    /// handful of conduction-node seeds, so O(V) per seed is fine).</summary>
    public int NearestVertex(Vector3 worldPoint);
}
```

#### [NEW] `src/CardioSimulator.Core/Domain/EikonalSolver.cs`

The solver plus its input/output types. Built once per mesh (adjacency reuse), then `Solve` is called per conduction configuration.

```csharp
namespace CardioSimulator.Core.Domain;

using System.Numerics;

/// <summary>A wave source: a mesh vertex that ignites at <see cref="TimeOffsetMs"/>.</summary>
public readonly record struct EikonalSeed(int VertexIndex, float TimeOffsetMs);

/// <summary>Speed field + barriers for one solve. Speed is model-units per millisecond.</summary>
public sealed class EikonalOptions
{
    public float DefaultSpeed { get; set; } = 1f;
    public float[]? VertexSpeed { get; set; }   // per-vertex speed (null => DefaultSpeed everywhere)
    public bool[]? Blocked { get; set; }        // true => non-conducting (infarct/scar); stays +inf
    public float WeldEpsilon { get; set; }      // informational; welding happens in SurfaceMesh
}

/// <summary>
/// Solves the eikonal equation |grad T| = 1/F on a triangulated surface via the Fast Marching Method
/// (Sethian; triangle-based update per Kimmel and Sethian, "Computing geodesic paths on manifolds").
/// Returns per-vertex activation time in ms; unreachable/blocked vertices are float.PositiveInfinity.
/// Deterministic (no clocks/RNG), so results are cacheable and unit-testable.
/// </summary>
public sealed class EikonalSolver
{
    public EikonalSolver(SurfaceMesh mesh);

    public float[] Solve(IReadOnlyList<EikonalSeed> seeds, EikonalOptions options);
}
```

**Algorithm (Fast Marching Method):**
- Vertex states: `Far` (∞), `Considered` (in the narrow band), `Accepted` (finalized).
- Narrow band = `System.Collections.Generic.PriorityQueue<int, float>` (BCL, net8.0 — no custom heap needed). Handle stale entries with a "current best time vs. popped key" check (lazy deletion), since `PriorityQueue` has no decrease-key.
- Init: each seed vertex → `Accepted` at its `TimeOffsetMs`; push its neighbors as `Considered`.
- Main loop: pop the min-time `Considered` vertex, mark `Accepted`, and for each incident triangle update the triangle's still-non-accepted vertex with the **two-front local solver** (solve the quadratic from the two accepted vertices + local speed). Fall back to the **edge (Dijkstra) update** `T = min(T, T_neighbor + edgeLen/F)` when the triangle update is invalid (e.g. obtuse angle, one accepted neighbor). Skip `Blocked` vertices and triangles whose local speed is 0.
- Complexity O(V log V); memory O(V).

> Design note — accuracy vs. simplicity: the edge-update fallback is what makes this robust on messy anatomical meshes. Fully correct obtuse-triangle handling (virtual/unfolded updates) is a documented refinement, not required for a teaching-grade isochrone map. We ship the standard update + edge fallback in M3 and leave unfolding as an optional M3.5.

> Validation baseline: we also keep a trivial **Dijkstra-on-edges** solve behind an internal flag purely for tests — it has known metrication error (~paths snap to edges), so the FMM tests assert *lower* error than Dijkstra against analytic ground truth, proving the triangle solver earns its complexity.

#### [MODIFY] (optional) `src/CardioSimulator.Core/Domain/` conduction template

If we decide the clinical `ArrivalMs` and default conduction speed should live in Core (so Android shares them), lift the constants currently in `ConductionSystem.cs` (App) into a Core `ConductionModel`. *Deferred* — out of scope unless Open Question 2 escalates to a combined graph. Noted for the Android parity plan.

---

### CardioSimulator.App

#### [MODIFY] [Heart3DDialog.cs](file:///E:/VLN_Project/CardioSimulator/Win/src/CardioSimulator.App/Controls/Heart3DDialog.cs)

Replace the Euclidean loop in `PrecomputeWavefront` (~line 1928) with a call into the Core solver. Everything downstream (`ToggleWavefront`, `AdvanceWavefront`, the per-vertex `geom.Colors` animation) is unchanged — it still consumes `_activationTimes[mesh]`.

New flow inside `PrecomputeWavefront`, per heart mesh:
1. Read `geom.Positions` and `geom.Indices`; transform positions to world space by `mesh.TotalModelMatrix` (the current code already transforms per-vertex).
2. `var mesh = SurfaceMesh.Weld(worldPositions, indices, weldEps, out var rawToWelded);` with `weldEps = _modelMaxDim * 1e-4f`.
3. Build seeds: for each `ConductionNode`, `mesh.NearestVertex(node.Position)` → `EikonalSeed(v, node.ArrivalMs)`.
4. `var t = new EikonalSolver(mesh).Solve(seeds, options);` where `options.DefaultSpeed` is derived from model size (replaces the ad-hoc `_modelMaxDim/100f` so ~100 ms to cross the heart is preserved).
5. Scatter back: `activationPerRawVertex[i] = t[rawToWelded[i]];` store in `_activationTimes[mesh]` (same shape as today).
6. `float.PositiveInfinity` (unreachable) → treat as "never activates this cycle" in `AdvanceWavefront` (leave resting blue), so disconnected stray triangles don't flash.

**Threading & caching:**
- Run the whole precompute inside `Task.Run` (it is currently on the UI thread). Marshal the `_activationTimes` assignment back to the UI thread.
- Cache the result keyed by `(modelPath, conductionPathHash, speedFieldHash)`; invalidate on "Edit pathway" save and (M4) on infarct change. Avoids re-solving on every open/toggle.

#### [MODIFY] Infarct coupling (M4) — [Heart3DDialog.cs](file:///E:/VLN_Project/CardioSimulator/Win/src/CardioSimulator.App/Controls/Heart3DDialog.cs)

When the infarct set is present and progress > 0, mark welded vertices whose UV samples the infarct mask above a threshold as `EikonalOptions.Blocked = true` (or scale `VertexSpeed` down for the peri-infarct border zone). Re-solve when infarct progress crosses thresholds (debounced). This is what makes an infarct create a genuine conduction block / detour in the wavefront.

---

### Tests

#### [NEW] `tests/CardioSimulator.Core.Tests/SurfaceMeshTests.cs`
- Triangle-soup cube (36 raw indices, 24 raw vertices) welds to **8** unique vertices; the adjacency graph is a single connected component.
- `rawToWelded` covers every raw vertex; scattering a per-welded array back and reading through the map round-trips.
- Epsilon boundary: two vertices `weldEps*0.5` apart merge; `weldEps*2` apart do not.
- Degenerate (zero-area) triangles are dropped.

#### [NEW] `tests/CardioSimulator.Core.Tests/EikonalSolverTests.cs`
- **Planar accuracy:** regular NxN grid mesh on a plane, single corner seed, `DefaultSpeed=1`. Assert `max relative error` of `T` vs. Euclidean distance `< 5%` (FMM). Assert Dijkstra baseline error is *larger* on the diagonal (justifies FMM).
- **Two seeds / watershed:** two opposite-corner seeds → each vertex gets the min arrival; the midline equidistant set is correct.
- **Time offsets:** seed B offset `+50 ms` shifts its territory boundary by the expected geodesic amount.
- **Barrier / detour:** a wall of `Blocked` vertices forces the wave around it; arrival behind the wall exceeds the straight-line time; a fully enclosed blocked vertex stays `PositiveInfinity`.
- **Disconnected component** → `PositiveInfinity`.
- **Determinism:** identical inputs produce byte-identical output arrays across runs (guards the lazy-deletion / tie-breaking).

---

## Milestones

| # | Deliverable | Depends on | Status |
|---|-------------|-----------|--------|
| M1 | `SurfaceMesh` + welding + adjacency + `SurfaceMeshTests` | — | ✅ done (10 tests) |
| M2 | Dijkstra baseline solve (internal, for validation) | M1 | ✅ done (`EikonalSolver.SolveDijkstra`) |
| M3 | FMM triangle solver + `EikonalSolverTests` (accuracy) | M1, M2 | ✅ done (11 tests) |
| M3.5 | *(optional)* obtuse-triangle unfolding for tighter accuracy | M3 | not started |
| M4 | Speed field + barriers; infarct-block coupling in App | M3 | ✅ wired + verified numerically (visual subtle on this model — see note) |
| M5 | App integration: replace `PrecomputeWavefront`, off-thread + cache | M3 | ✅ integrated + builds; runtime smoke test pending |
| M6 | Android Parity Sync Plan | M5 | not started |

> **M1–M3 landed.** New Core files: `Domain/SurfaceMesh.cs`, `Domain/EikonalSolver.cs`. New tests: `SurfaceMeshTests.cs` (10), `EikonalSolverTests.cs` (11). The speed-field + block API (`EikonalOptions.VertexSpeed` / `Blocked`) shipped early with M3 and is unit-tested (barrier detour, blocked-vertex stays ∞, speed-scaling); only the infarct-mask→vertex wiring in the App remains for M4.

> **M5 landed.** `Heart3DDialog.PrecomputeWavefront` rewritten: snapshots each heart mesh's world-space geometry on the UI thread, then welds all heart meshes into one graph and runs the eikonal solve **off-thread** (`Task.Run`), scattering activation times back per mesh. Conduction nodes are seeded at their nearest welded vertex with the straight-line gap folded into the ignition offset. Results are cached by (model, pathway, speed) with a FIFO cap so reopening the dialog is instant, and the wavefront is re-solved when a pathway is authored. `AdvanceWavefront` / vertex-colour rendering unchanged (∞ arrival → stays resting-blue via the existing clamp). Fire-and-forget solve is guarded so a failure logs instead of crashing.

> **✅ M4 done — infarct → conduction block (2026-08-21).** `PrecomputeWavefront` now also snapshots per-vertex UVs for the infarct-skin meshes (tracked by node identity via `_infarctMeshes`, since the wavefront view swaps their `Material` out — this was a real bug: detecting skin meshes by current material failed once wavefront was on). Off-thread, `SolveWavefront` samples the necrosis mask (`InfarctTextureSet.SampleMask`) per vertex; where `mask × progress ≥ 0.4` the welded vertex is passed to the solver as `Blocked` (non-conducting), so the wave routes around dead scar. Re-solves are triggered (and cached) per necrosis "bucket" (0..10) as the infarct develops/scrubs, and on wavefront-on. **Verified numerically** via a diagnostic log: 4 infarct meshes, 15,260 vertices sampled, mask max 1.0, blocked vertices scale with necrosis (0 → 7 → 112 → … → **211 at full infarct**). **Visual caveat:** on the bundled `heart.fbx` the authored necrosis mask maps to only ~200 sparse *posterior*-wall vertices, so Gouraud interpolation smears red over them and no distinct blue "hole" shows from the fixed anterior camera (and injected orbit-drag is swallowed by WinUI, so automated rotation to the posterior wall isn't possible). The coupling is correct; prominence is a model-data limitation. **Follow-up — M4.1 (optional):** a peri-infarct *slow* zone (reduced `VertexSpeed`, already supported) so the wave visibly lags/detours even when the hard-block core is small, and/or a larger authored mask.

> **✅ M5.1 done — wavefront colours render (2026-08-21).** Runtime smoke test (bundled `heart.fbx`): opened the 3D dialog, toggled "Волны деполяризации" + Play, burst-captured a full cardiac cycle. The blue→red action-potential wavefront now propagates across the myocardium — red ignites at the SA/atrial region on P-wave onset, then a clean geodesic gradient sweeps down the ventricles (the eikonal solve driving it). Confirmed the whole M5 pipeline end-to-end.
>
> **Root cause of the earlier white heart:** HelixToolkit's vertex-colour blending is **opt-in** — `PhongMaterialCore.VertexColorBlendingFactor` defaults to **0**, so the material ignored `geom.Colors` entirely regardless of buffer state (added in HelixToolkit 2.12.0). Fix was one line: set `VertexColorBlendingFactor = 1.0f` on `_wavefrontMaterial`. (The speculative `geom.UpdateVertices()` rebuild was unnecessary and was reverted; `AdvanceWavefront`'s existing `geom.Colors` + `UpdateColors()` per-frame path is correct once blending is enabled.)

M1–M3 are the core of the ask and are independently verifiable in Core with zero UI. M5 is the visible payoff.

---

## Customization follow-ups (requested 2026-08-21)

User asked for customizable depolarisation colours and a fiber/"sparkle-line" rendering (reference image supplied). Sequenced:

| # | Deliverable | Status |
|---|-------------|--------|
| C1 | **Custom colour schemes** — selectable ramps (Classic blue→red, Thermal, Viridis, Ice, Fire) via a «Цвета волны» dropdown | ✅ done + verified |
| C2 | **Propagation streamlines** — short line glyphs oriented by ∇(activation) (wave travel direction), coloured by activation; `LineGeometryModel3D` line set animated like the mesh. Reuses the solver; no fibre data | ✅ done + verified |
| C3 | **True fibre streamlines** — anatomically-oriented lines. Achievable code-only via a **rule-based (LDRB) fibre model** (fibre orientation from Laplace solves on the geometry), no external DTI dataset. Reuses the C2 line renderer | ✅ done + verified (epicardial approximation) |

**C1 implementation:** `AdvanceWavefront` now maps each vertex's ms-since-activation to an AP "intensity" 0..1 (`WavefrontIntensity`: upstroke→plateau→repolarisation) and samples the selected scheme's colour-stop ramp (`SchemeStops`/`SampleScheme`). Classic reproduces the original blue→red exactly. Picker wired in `BuildConductionControls`. Verified: Thermal reproduces the reference image's blue→cyan→green→yellow look.

**C3 implementation:** Core gained `SurfaceMesh.SolveLaplace(fixedMask, fixedValues, iterations)` — a Gauss-Seidel harmonic solve over the mesh (1 new test: monotonic field + long-axis gradient direction). App `ComputeFiberDirections` builds a rule-based **epicardial** fibre field entirely from geometry: PCA long axis (power iteration) → pin base/apex bands → Laplace field → its gradient is the local long-axis direction → rotate by a −60° helix angle in the tangent plane (Rodrigues). `SolveWavefront` orients the streamline glyphs by fibre or by ∇activation per the new `StreamlineOrientation`; a «Ориентация линий» dropdown switches them (re-solves, cached per orientation). Verified: fibres wrap the ventricles helically, distinct from the propagation orientation. **Caveat:** single-layer epicardial approximation, not transmural (the model is a surface, not a volume) — a full LDRB with endo→epi helix variation would need a solid mesh.

**C2 implementation:** Core gained `SurfaceMesh.ComputeVertexNormals()` + `ComputeVertexGradient(values)` (P1 area-weighted gradient; 4 new tests). The off-thread `SolveWavefront` now also returns a `WavefrontSolution` with streamline glyph geometry (`BuildStreamlines`): at a ≤6000-vertex subsample of reachable surface vertices, a short segment centred on the vertex, oriented along ∇(activation) and lifted off the surface by the normal; each endpoint carries the seed's activation time. Rendered via a `LineGeometryModel3D` overlay coloured per-frame by `AdvanceStreamlines` using the same scheme ramp. New «Линии волны» toggle; independent of the solid wavefront view. Verified visually (matches the reference "sparkle line" look). Note: `Vector3Collection`/`IntCollection`/`Color4Collection` live in the root `HelixToolkit` namespace (not `HelixToolkit.SharpDX`).

## Risks & Mitigations

- **Triangle-soup imports disconnect the graph.** *Mitigation:* M1 welding, verified by the cube test. This is the single most likely cause of "the wave doesn't move."
- **Obtuse triangles reduce FMM accuracy.** *Mitigation:* edge-update fallback in M3 keeps results monotone and connected; unfolding (M3.5) only if the visual banding warrants it.
- **Separate anatomical meshes (atria vs. ventricles) don't share vertices, so a surface wave can't cross between them.** *Mitigation:* per-mesh solve with each mesh seeded from its nearest conduction node (Open Q 2). Future upgrade: add explicit "bridge" edges at the AV junction between the nearest atrial and His-region vertices before solving on a combined graph.
- **Precompute cost on dense meshes.** *Mitigation:* O(V log V) FMM, run off-thread, cached per configuration. If still heavy, the FMM structure ports directly to the parallel Fast Iterative Method / GPU later.
- **Speed units.** Model files are in arbitrary units; `DefaultSpeed` must be derived from `_modelMaxDim` so total activation (~250 ms for a normal beat, matching the conduction template) is preserved regardless of model scale. Covered in M5.
- **`PriorityQueue` has no decrease-key.** *Mitigation:* lazy deletion — on pop, discard entries whose stored key is worse than the vertex's current best time. Covered by the determinism test.

---

## Out of Scope (explicit)

- GPU/shader color mapping and per-pixel wavefront rendering (separate performance plan).
- Rhythm/pathology-driven conduction (bundle-branch block, ectopy, re-entry) — depends on this solver but is a downstream feature.
- ECG-strip cursor synchronization and the color legend.
- Fiber-anisotropic conduction (the `VertexSpeed` scalar field is isotropic; anisotropy needs a tensor speed and a modified local solver).
