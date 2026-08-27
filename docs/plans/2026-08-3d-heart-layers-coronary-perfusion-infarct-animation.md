# Implementation Plan: 3D Heart — Pure Viewer over a Packaged Content Set (Layers, Coronaries, Perfusion Territories, Territory-Driven Infarct, Beating Animation)

Source requirement: **`ТЗ 3д модели сердца 1.docx`** (customer, 2026-08-26). Authored-content contract: **[docs/asset-spec-3d-heart.md](../asset-spec-3d-heart.md)** (the file set the customer + 3D artist deliver). This plan is the Win-first implementation; an Android parity sync plan is produced at the end.

> [!IMPORTANT]
> **Reframe (customer direction, 2026-08):** the 3D heart view becomes a **pure viewer**. **All in-app editing/authoring is removed** — "Edit Hotspots", "Clear All", "Edit pathway", and the Admin/User authoring gating for this dialog all go away. Every feature and the whole appearance are driven **entirely by pre-authored files**. The final delivery form is **one encrypted `.pak`** (reusing the app's existing `ContentCrypto`/CSP2 content-pack mechanism) so students can't extract the model/content. See [[heart3d-pure-viewer-content-package]].

---

## 1. Motivation

The ТЗ re-frames the 3D heart as a **stack of selectable anatomical layers** with several new capabilities. The seven requested layers/behaviours:

1. **Educational exterior** — a whole heart skin shown **semi-transparent** so the conduction/nerve fibres ("yellow branches") read through the myocardium.
2. **Realistic exterior** — a second, photoreal textured skin.
3. **Coronary-artery layer** — split into **named branches**.
4. **Conduction system + heart cut in half**.
5. **Leads system** + **perfusion territories tinted by supplying coronary artery** (explicitly "needed for the infarct textures").
6. **Infarct development** — smooth healthy → **necrotic (blackened)** transition, localised to the occluded vessel's territory.
7. **Beating-heart animation**.

On top of the seven layers, the reframe adds two structural requirements: **(a)** all of the above is consumed from an **authored file set** (no runtime authoring), and **(b)** that set is loaded from **one encrypted package**.

### What already ships (`src/CardioSimulator.App/Controls/Heart3DDialog.cs`)

**Reuse (rendering/animation — keep):**
- Model import (`.glb/.gltf/.fbx/.obj/…`) via `HeartModelStore` + SharpAssimp, off-thread, with a placeholder fallback.
- `IsolateHeart()` — separates "heart + coronary" meshes from a "scaffold" (silhouette + ECG lead system) and frames each. **Seed of the layer system.**
- **X-ray / transparency** (`ToggleTransparency` / `ApplyTransparency`) — covers most of layer #1.
- **Conduction system** render + travelling pulse + phase captions + **eikonal wavefront** (`EikonalSolver`, Core) + fibre **streamlines**. Covers #4 (conduction), #7's "nicely-drawn conduction".
- **Cutaway / half-heart** (`BuildCutRepresentation` / `ToggleCutaway` / `UpdateCutPlane`). Covers #4.
- **Infarct texture blend** — `InfarctTextureSet` + `InfarctTextureBlender` (Core, CPU, unit-tested) + progress slider + "develop" animation + infarct→conduction-block coupling. Covers #6 — but with **one global mask**.
- **Leads scheme** (`InitLeadsScheme` / `ToggleLeadsScheme`). Covers the "leads" half of #5.

**Remove (authoring — the reframe deletes these):**
- `ToggleAuthoringMode` / `ShowAddHotspotPrompt` / `PromptClearAllHotspots` / `DeleteHotspot` / `SaveHotspots` and the "Edit Hotspots" / "Clear All" toolbar.
- `ToggleConductionEdit` / `PlaceNextConductionNode` / `UpdateEditHint` and the "Edit pathway" button.
- The `_isAdmin` authoring gating **inside this dialog** (nothing left to gate here; the app-wide role model is untouched elsewhere).
- The writable-sidecar `Save`/fallback paths in `Heart3DDialog`/`ConductionSystem` (content is read-only from the package now).

### Gaps this plan closes

| ID | Gap | Spec |
|----|-----|------|
| **G0** | **In-app authoring must be removed** (hotspots edit/clear, pathway edit, admin gating, writable sidecars). | reframe |
| **G7** | **No content-package loader.** The app probes loose `heart.*` files; it must load a **manifest + entries from an encrypted `.pak`** in-memory. | reframe |
| **G8** | **Conduction pathway is authored in-app.** It must instead be built from **`cond_node_*` locator empties** in the model (+ optional `heart.conduction.json` override), with built-in clinical timings. | reframe |
| **G1** | No explicit **layer manager** / mesh-name → layer classification. | #1–#5 |
| **G2** | No **educational ↔ realistic skin** switch. | #1, #2 |
| **G3** | No **named coronary branches** (click-to-identify, highlight, localized). | #3 |
| **G4** | No **perfusion-territory** overlay. | #5 |
| **G5** | Infarct is a **single global mask** (want per-vessel). The **"MI" button (`AppStrings.Monitor3DMi`, `Heart3DDialog.cs:387`) is a dead placeholder** reserved for this; also want the **affected-lead readout** (LAD → V1–V3). | #5, #6 |
| **G6** | No **contraction animation** playback. | #7 |

---

## 2. Architecture

**Single authored model + companion files, all bundled into one encrypted package.** This matches every existing precedent (`IsolateHeart` parses a multi-sub-mesh model; `InfarctTextureSet` keys sidecars off the model name; the app already ships AES-256-GCM `.pak` content packs read lazily in-memory via `EncryptedArchive`).

The **authoritative file-set contract is [docs/asset-spec-3d-heart.md](../asset-spec-3d-heart.md)** (delivered — see M0). In brief: `heart.manifest.json` (entry point) + `heart.glb` (layers by mesh-name prefix `heart_real_*`/`heart_edu_*`/`coro_<CODE>_*`/`cond_*`/`leads_*`/`scaffold_*`, plus `cond_node_*` locator empties and a `beat` animation clip) + `heart.healthy/infarct.png` + `heart.territories.png` + metadata JSON (`coronaries`, `territories`, optional `hotspots`/`conduction`/`layers`/`strings`). **Missing entry ⇒ feature auto-hides.**

**Packaging:** all `heart.*` entries → ZIP → AES-256-GCM `.pak` (e.g. `heart3d.pak`) via `ContentCrypto`; read entries lazily with `EncryptedArchive` (no disk extraction), exactly as the pathology/course packs work.

---

## 3. Open questions (defaulted — confirm or override)

1. **Package resolution & the existing `HeartModelStore`/Settings picker.** *Recommendation:* the viewer prefers a `heart3d.pak` (bundled under `Assets/Models`, override under `AppPaths.ModelsDir`); the Settings "3D model" picker accepts a `.pak` (and, for dev, a loose `heart.glb` folder). `HeartModelStore` grows a `ResolveActivePackage()` beside the existing loose-file resolver, which stays as the **authoring/dev path** only.
2. **Do we keep a loose-folder dev path at all?** *Recommendation:* **yes**, gated to non-shipping/dev builds, so content can be iterated before packing. Shipping builds read only the `.pak`.
3. **Beating animation source.** *Recommendation:* **embedded `beat` clip** via HelixToolkit's animation updater; **procedural fallback** (subtle BPM-paced anisotropic scale pulse) when no clip, so the control is never dead.
4. **Affected-lead readout.** *Recommendation:* a **small labelled pill list** beside the existing reference ECG strip, driven by `affectedLeads[]` from `CoronaryTaxonomy`. No live ECG recompute (the dialog holds only a BPM).
5. **Coupling infarct to the real rhythm/ECG engine.** *Recommendation:* **out of scope** — this dialog is a visualiser handed a BPM; affected-leads is educational annotation. A future plan can drive the monitor from a selected occlusion.

---

## 4. Milestones

Ordered so each milestone ships something usable. Every milestone **degrades gracefully** on a package lacking its layer.

### M0 — Asset contract & taxonomy  *(contract delivered)*

- **[DONE] [docs/asset-spec-3d-heart.md](../asset-spec-3d-heart.md)** — the full authored file-set contract (Russian-primary, ASCII tokens), covering mesh naming, `cond_node_*` locators, textures, JSON formats, the manifest, packaging, and the sketchfab references.
- **[NEW] `src/CardioSimulator.Core/Domain/CoronaryTaxonomy.cs`** — platform-neutral (Core, unit-tested, hand-portable to Android; mirrors `InfarctTextureBlender`/`EikonalSolver`). Canonical vessel codes, EN/RU names, territory code, `affectedLeads[]`. Seeds **LAD** (→ anteroseptal, V1–V4), **LCX** (→ lateral, I/aVL/V5–V6), **RCA** (→ inferior, II/III/aVF). Loadable/overridable from `heart.coronaries.json`.

### M1 — Pure-viewer conversion + content-package reader (G0, G7)

- **[NEW] `Heart3DPackage`** (App) — opens `heart3d.pak` via `ContentCrypto`/`EncryptedArchive`, reads `heart.manifest.json`, and exposes entry streams (`heart.glb`, textures, JSON) **in-memory**. `HeartModelStore.ResolveActivePackage()` resolves it (bundled ⇐ `Assets/Models`, override ⇐ `AppPaths.ModelsDir`); Settings picker accepts a `.pak`.
- **[CHANGE] `LoadModelAsync`** — import the model from the package stream (SharpAssimp can import from a byte buffer/temp) instead of a loose path; resolve every sidecar as a package entry, not a filesystem probe.
- **[REMOVE] all in-app authoring** (G0): the hotspot edit/clear toolbar + prompts + save, the "Edit pathway" button + node placement + edit hint, and the `_isAdmin` authoring gating within this dialog. Hotspots/conduction become **read-only** from package entries; the `Save`/writable-fallback code paths in `Heart3DDialog`/`ConductionSystem` are deleted.
- Manifest drives the dialog **title** (`titleRu`/`titleEn`) and default framing.

### M2 — Layer manager + educational/realistic skin switch (G1, G2)

- **[NEW]** After import, classify every `MeshNode` into a `HeartLayer` enum (`EducationalSkin`, `RealisticSkin`, `Coronaries`, `Conduction`, `Leads`, `Scaffold`, `Other`) via the mesh-name convention + `heart.layers.json` override. Generalises `_scaffoldMeshes` into `Dictionary<HeartLayer, List<MeshNode>>`.
- **[NEW] left-rail "Layers" group** — a toggle per present layer (hidden if empty). Absorbs the existing "Leads scheme" button (leads become one layer), keeping the reframe-on-toggle behaviour.
- **[CHANGE]** `IsolateHeart` / `InitLeadsScheme` / `ToggleLeadsScheme` refactored onto the layer map.
- **Skin switch:** mutually-exclusive `EducationalSkin` / `RealisticSkin` visibility. Default realistic; educational auto-suggests X-ray on. Infarct/wavefront/territory overlays target the **visible** skin (extend `SetupInfarct`'s mesh discovery).

### M3 — Named coronary branches + read-only annotations (G3)

- **[NEW]** Coronary meshes are hit-testable: clicking a `coro_<CODE>_*` mesh shows its **localized name** (from `CoronaryTaxonomy`) in the existing details-panel style and **highlights** the whole branch (emissive tint of all meshes sharing `<CODE>`), dimming others. Reuses `TraverseMeshes` + the `_originalDiffuse` cache.
- **[NEW]** A branch list / legend in the left rail — click a name to highlight/frame that vessel (reuses `FlyToHotspot`'s camera animation).
- **[CHANGE] hotspots become read-only annotations** loaded from the `heart.hotspots.json` package entry — the marker + details UI stays, the authoring is gone (M1).

### M4 — Perfusion territories overlay (G4)

- **[NEW] `TerritoryTextureSet`** (App) beside `InfarctTextureSet` — decodes the `heart.territories.png` **label atlas** once into a per-pixel territory id (§ asset-spec §6). A "Perfusion territories" toggle tints each myocardial region with its vessel colour (semi-transparent overlay on the visible skin); colours come from `CoronaryTaxonomy` so vessel highlight (M3) and territory tint agree. Legend maps colour → vessel.

### M5 — Territory-driven infarct + wire MI button + affected leads (G5)

- **[CHANGE] `InfarctTextureBlender` / `InfarctTextureSet`** — blend necrosis **only inside a selected territory** (`Blend(progress, territoryId)`, masked by the label atlas), keeping the whole-heart path as `territoryId = All`. Unit tests in `InfarctTextureBlenderTests`.
- **[NEW] wire `AppStrings.Monitor3DMi`** (`Heart3DDialog.cs:387`) → an **"MI / occlusion" panel**: pick a coronary branch → its territory becomes the infarct target → the existing 0–1 slider + "develop" animation blackens **that** territory. The wavefront infarct-block coupling (`MaybeResolveWavefrontForInfarct`) already keys off the mask, so conduction block follows for free.
- **[NEW] affected-lead readout** (§3 Q4) — the occluded vessel's `affectedLeads[]` beside the reference ECG strip.

### M6 — Conduction pathway from `cond_node_*` locators (G8)

- **[CHANGE] `ConductionPath`** — build node anchors from the model's **`cond_node_<KEY>` locator empties** (SA/atria/AV/His/bundles/Purkinje/apex), taking arrival-ms + RU/EN labels from the **built-in clinical `Template`**. Optional `heart.conduction.json` entry overrides timings/anchors. **Remove `CreateDefault(bounds)` as the primary path** (keep only as a last-ditch fallback if neither locators nor JSON exist), and remove the authoring writer.
- Wavefront/streamlines seed from the resulting pathway unchanged.

### M7 — Beating-heart animation (G6)

- **[NEW]** If the `.glb` carries the `beat` clip, drive it via HelixToolkit's animation updater from `OnCompositionRendering`, paced to `_bpm`, with a "Beat" play/pause; **sync** its phase with the conduction pulse. **Procedural fallback** (BPM-paced scale pulse) when no clip.
- Settle interactions: animation + cutaway (freeze or animate the cross-section), animation + infarct (don't fight the blend).

### M8 — Android parity sync plan

- Run **`create-prompt`** to emit `docs/plans/sync/2026-08-android-3d-heart-…-parity.md`, mirroring: the package/manifest reader, `CoronaryTaxonomy` (hand-ported), the layer/naming convention, `cond_node_*` pathway, territory-scoped infarct, and the beating animation into the Android Kotlin renderer. Same pattern as `2026-08-android-3d-heart-texture-infarct-parity.md`.

---

## 5. Cross-cutting concerns

- **Pure viewer:** no code path may write content or expose authoring. Content is read-only from the package; the only writable state is transient view state.
- **Graceful degradation** is mandatory: classify → absent layer/entry/clip ⇒ its control is `Collapsed` (the infarct feature is the template). A minimal package (manifest + one heart mesh) must still load and orbit.
- **Security / packaging:** reuse `ContentCrypto`/CSP2 + `EncryptedArchive` (lazy, in-memory); never extract package entries to disk. A separate packing tool builds `heart3d.pak` (mirrors the pathology/course pack build).
- **Localization:** every label via `AppStrings` (`Monitor3D*`) EN/RU; vessel/territory/lead names RU-first from the taxonomy / package JSON.
- **Performance:** keep decode/blend off the UI thread (existing `Task.Run` + coalescing in `BuildAndApplyAsync`); one-shot territory decode like `InfarctTextureSet` (`[[large-dataset-virtualize-lists]]`).
- **Rendering gotchas:** Assimp imports glTF as **Phong not PBR** (`[[heart3d-infarct-texture-pipeline]]`); `PhongMaterial.VertexColorBlendingFactor` must be 1 for per-vertex colour (`[[wavefront-vertex-color-rendering-broken]]`). Territory tint / branch highlight must respect these.
- **Theme:** dialog + sub-dialogs set `RequestedTheme` (`[[dialog-webview-theme-propagation]]`) and honour light-dismiss (`[[dialog-light-dismiss]]`).

## 6. Risk / dependency summary

- **Primary dependency is the authored package**, not code. M2–M7 are blocked on the customer + artist delivering a package honouring the contract; app work can proceed against a **stub package** built to the contract in parallel (M1 can land immediately with a stub).
- **Import-from-stream:** SharpAssimp import currently takes a path; importing from a package entry may need a temp-file shim or a stream import — validate early in M1.
- **Territory ↔ UV alignment:** territories must share the skin's UV atlas (`InfarctTextureSet` already rejects mismatched dimensions).
- **Animation retargeting:** skeletal clips can import with scale/orientation quirks through Assimp; the procedural fallback de-risks a "beating" control that must always work.
- **Removing authoring** loses the only in-app way to place hotspots/pathway — acceptable per the reframe, since all content now comes from the package. `cond_node_*` locators (M6) replace the pathway authoring's purpose.

---

## 7. Deliverables checklist

- [x] M0 asset-spec contract — `docs/asset-spec-3d-heart.md`
- [ ] M0 `CoronaryTaxonomy.cs` (+ tests)
- [ ] M1 Package/manifest reader + **removal of all in-app authoring**
- [ ] M2 Layer manager + educational/realistic skin switch
- [ ] M3 Named coronary branches + read-only annotations
- [ ] M4 Perfusion-territory overlay + legend
- [ ] M5 Territory-scoped infarct + wired MI button + affected-lead readout
- [ ] M6 `cond_node_*`-driven conduction pathway (authoring removed)
- [ ] M7 Beating animation (clip + procedural fallback)
- [ ] M8 Android parity sync plan
