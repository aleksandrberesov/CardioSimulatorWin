# Implementation Plan: 3D Heart — Anatomical Layers, Named Coronaries, Perfusion Territories, Territory-Driven Infarct & Beating Animation

Source requirement: **`ТЗ 3д модели сердца 1.docx`** (customer, 2026-08-26). This plan is the Win-first implementation; an Android parity sync plan is produced at the end (same convention as the infarct-texture and eikonal parities).

---

## 1. Motivation

The customer's ТЗ re-frames the 3D heart as a **stack of selectable anatomical layers**, with several new capabilities on top of the current single-model viewer. Translated, the seven requested layers/behaviours are:

1. **Educational exterior** — one whole heart mesh with "internal cameras", shown **semi-transparent** so the conduction/nerve fibres (the "yellow branches") read through the myocardium.
2. **Realistic exterior** — a second, photoreal textured skin of the same heart.
3. **Coronary-artery layer** — split into **named branches** (each branch is individually identifiable).
4. **Conduction system + heart cut in half** — the conduction tree shown on a cutaway heart.
5. **Leads system** — the ECG-lead scaffold, **plus perfusion territories tinted by supplying coronary artery** (explicitly "needed for the infarct textures").
6. **Infarct development** — a smooth transition from healthy colour → **necrotic (blackened)** tissue, localised to the territory of the occluded vessel.
7. **Beating-heart animation** — myocardial contraction playback (with the conduction system "effectively drawn").

### What already ships (`src/CardioSimulator.App/Controls/Heart3DDialog.cs`)

The dialog is already a rich 3D teaching surface. The following are **done** and should be **reused, not rebuilt**:

- Model import (`.glb/.gltf/.fbx/.obj/.dae/.stl/.3ds/.ply`) via `HeartModelStore` + SharpAssimp, off-thread, with a placeholder fallback.
- `IsolateHeart()` already separates "heart + coronary" meshes from a "scaffold" (human silhouette + ECG lead system) and frames the camera on each. **This is the seed of the layer system.**
- **X-ray / transparency** (`ToggleTransparency` / `ApplyTransparency`) — covers most of layer #1's "semi-transparent to see the fibres".
- **Conduction system** — `ConductionPath` (SA→AV→His→Purkinje), travelling pulse, phase captions, editable pathway, the **eikonal wavefront** (`EikonalSolver` in Core) and fibre **streamlines**. Covers #4 (conduction) and #7's "nicely-drawn conduction".
- **Cutaway / half-heart** — `BuildCutRepresentation` / `ToggleCutaway` / `UpdateCutPlane`. Covers #4 (cut in half).
- **Infarct texture blend** — `InfarctTextureSet` (healthy/infarct/mask sidecars) + `InfarctTextureBlender` (Core, CPU, unit-tested) + 0–1 progress slider + "develop" animation + infarct→conduction-block coupling in the wavefront. Covers #6 — but with **one global mask**, not per-vessel.
- **Leads scheme** — `InitLeadsScheme` / `ToggleLeadsScheme` toggles the scaffold meshes and reframes. Covers the "leads" half of #5.

### Gaps this plan closes

| ID | Gap | Spec |
|----|-----|------|
| **G1** | No explicit **layer manager**; layer visibility is ad-hoc (`_scaffoldMeshes` hide). Needs a sub-mesh **naming convention** to classify meshes into layers. | #1–#5 |
| **G2** | No **educational ↔ realistic skin** switch. | #1, #2 |
| **G3** | No **named coronary branches** (click-to-identify, highlight, localized names). Hotspots are generic, not a structured vessel layer. | #3 |
| **G4** | No **perfusion-territory** overlay (myocardium tinted by supplying artery). | #5 |
| **G5** | Infarct is a **single global mask**; the spec wants **occlude-a-vessel → that territory necroses**. The **"MI" button (`AppStrings.Monitor3DMi`, `Heart3DDialog.cs:387`) is a dead placeholder** (no click handler) reserved for this. Also wants the **affected-lead readout** (LAD → anteroseptal → V1–V3). | #5, #6 |
| **G6** | No **contraction animation** playback (only camera fly-to exists). | #7 |

---

## 2. Architecture decision (defaulted — please confirm)

> [!IMPORTANT]
> **Single rich model + naming conventions + sidecars** (recommended) **vs. a stack of separate model files.**
>
> **Recommendation: single model.** It matches every existing precedent in this codebase — `IsolateHeart` already parses a model that bundles heart + coronary + leads sub-meshes; `InfarctTextureSet` already keys sidecars off the model filename; hotspots and the conduction path are already sidecar JSON next to the model. Extending that (mesh-name prefixes for layers, a territory-encoding sidecar, an embedded animation clip) keeps one load path, one camera-framing pass, and one `HeartModelStore`.
>
> The alternative (one `.glb` per layer, layered at runtime) is only worth it if the 3D artist cannot deliver a single authored scene, or if layers must be independently versioned/streamed. It multiplies load/attach/align/dispose cost and re-opens the alignment problem the single UV atlas solves for free.
>
> **The rest of this plan assumes the single-model contract.** If the artist prefers multiple files, only §3 (the contract) and `HeartModelStore` change; the App feature code is nearly identical.

### The asset contract (what the 3D artist must deliver)

Because the sketchfab links are *briefs*, the app can only light up a feature when the model honours a contract. All of this degrades gracefully — **a model that omits a layer simply hides that feature's controls** (exactly how infarct hides today when sidecars are absent).

**Mesh naming convention** (case-insensitive prefix on each mesh/node name in the exported model):

| Prefix | Layer | Example names |
|--------|-------|---------------|
| `heart_edu_*` | Educational skin (translucent-capable) | `heart_edu_myocardium` |
| `heart_real_*` | Realistic skin | `heart_real_myocardium` |
| `coro_<CODE>_*` | Coronary branch, `<CODE>` from the vessel taxonomy | `coro_LAD_prox`, `coro_LCX_om1`, `coro_RCA_pda` |
| `terr_<CODE>_*` | Perfusion territory owned by vessel `<CODE>` | `terr_LAD`, `terr_RCA` |
| `cond_*` | Conduction system geometry (if authored as mesh, not the JSON path) | `cond_purkinje` |
| `leads_*` / `scaffold_*` | ECG-lead scaffold / silhouette | `leads_v1`, `scaffold_torso` |

**Sidecars next to `heart.glb`** (mirroring the existing `heart.hotspots.json` / `heart.healthy.*` conventions):

- `heart.layers.json` — optional overrides/labels for the mesh-name classification (so non-conforming exports can still be mapped without re-export).
- `heart.coronaries.json` — vessel taxonomy: `code → { displayName_en, displayName_ru, territoryCode, affectedLeads[] }`.
- `heart.territories.json` **or** per-territory grayscale masks `heart.terr.<CODE>.mask.png` — see §5 (G4/G5) for the two options and the recommended one.
- Embedded **animation clip** named `beat` (or the first clip) in the `.glb` for G6.

A **`docs/asset-spec-3d-heart.md`** deliverable (M0) writes this contract up for the artist with the sketchfab references inline.

---

## 3. Open questions (defaulted with a recommendation — confirm or override)

1. **Territory encoding — per-territory masks vs. a single label map?**
   *Recommendation:* a **single BGRA "territory label" atlas** (`heart.territories.png`) where each vessel owns a flat RGB key colour, decoded once into a per-pixel `byte territoryId`. One decode, trivial "is this pixel in territory X" test, and it composes with the existing `InfarctTextureBlender` by treating the label map as a *selectable* mask. Separate per-territory masks also work (and reuse `InfarctTextureSet.SampleMask` verbatim) but multiply file count and decode time. Ship the label-map path; keep `SampleMask` for the fibre/wavefront block test.
2. **Educational vs realistic — two skins in one model, or the realistic skin + a shader "educational" preset?**
   *Recommendation:* **two skins in one model** (`heart_edu_*` / `heart_real_*`), toggled by visibility. It's what the artist is already being asked to build (two exteriors), needs no shader work, and the "educational" look is then just *realistic-hidden + edu-shown + X-ray on*.
3. **Beating animation source — embedded skeletal/morph clip, or a procedural scale pulse?**
   *Recommendation:* **embedded clip** if the artist delivers one (the ТЗ's reference model is animated), played via HelixToolkit's animation updater. Provide a **procedural fallback** (subtle anisotropic scale pulse of the myocardium meshes on the R-wave, paced to BPM) so the "beating" control still does *something* on models without a clip. Gate the real clip behind its presence.
4. **Affected-lead readout — reuse the bottom ECG strip, or a new callout?**
   *Recommendation:* highlight the affected leads (e.g. V1–V3 for LAD) as a **small labelled pill list** beside the existing reference ECG strip, driven by `affectedLeads[]` from the coronary taxonomy. No live ECG recompute — the dialog only has a heart rate, not sample data (same constraint the current strip already documents).
5. **Coupling infarct to the actual rhythm/ECG engine?**
   *Recommendation:* **out of scope here.** This dialog is a *visualiser*; it is handed a BPM, not the rhythm model. The affected-lead readout is educational annotation from the taxonomy, not a simulated ECG. A future plan can drive the monitor's ECG from a selected occlusion.

---

## 4. Milestones

Ordered so each milestone ships something usable and later ones build on earlier scaffolding. Every milestone **degrades gracefully** on models lacking its layer.

### M0 — Asset contract & layer taxonomy (no app behaviour change)

- **[NEW] `docs/asset-spec-3d-heart.md`** — the §2 contract written for the 3D artist: mesh-name prefixes, sidecar formats, animation-clip requirement, the sketchfab reference links, and the vessel/territory/lead taxonomy table.
- **[NEW] `src/CardioSimulator.Core/Domain/CoronaryTaxonomy.cs`** — platform-neutral (Core, unit-tested, hand-portable to Android; mirrors `InfarctTextureBlender`/`EikonalSolver`). Defines the canonical vessel codes, EN/RU display names, territory code, and `affectedLeads[]`. Seeds the standard set: **LAD** (+diagonals/septals → anteroseptal, V1–V4), **LCX** (+OMs → lateral, I/aVL/V5–V6), **RCA** (+PDA → inferior, II/III/aVF). Loadable/overridable from `heart.coronaries.json`.
- Wire into `[[acronym-taxonomy-wiring]]` if the vessel codes should join the existing taxonomy spine.

### M1 — Layer manager + educational/realistic skin switch (G1, G2)

- **[NEW] `Heart3DDialog` layer model** — after import, classify every `MeshNode` into a `HeartLayer` enum (`EducationalSkin`, `RealisticSkin`, `Coronaries`, `Conduction`, `Leads`, `Territories`, `Other`) via the naming convention + `heart.layers.json` override. Generalises the current `_scaffoldMeshes` list into a `Dictionary<HeartLayer, List<MeshNode>>`.
- **[NEW] left-rail "Layers" group** — a checkbox/toggle per present layer (hidden if the layer has no meshes). Replaces the single "Leads scheme" button with a consistent layer panel; **keep** the existing leads reframe behaviour.
- **[CHANGE]** `IsolateHeart` / `InitLeadsScheme` / `ToggleLeadsScheme` refactored onto the layer map (leads become one layer among several).
- **Skin switch:** educational vs realistic = mutually-exclusive visibility of `EducationalSkin` / `RealisticSkin`. Default to realistic; switching to educational auto-suggests X-ray on. Infarct/wavefront/territory overlays must target **whichever skin is visible** (extend `SetupInfarct`'s mesh discovery to follow the active skin).

### M2 — Named coronary branches (G3)

- **[NEW]** Coronary layer is hit-testable: clicking a `coro_<CODE>_*` mesh shows its **name** (from `CoronaryTaxonomy`, localized) in the existing hotspot-details panel style, and **highlights** the whole branch (emissive tint of all meshes sharing that `<CODE>`), dimming the others.
- **[NEW]** A branch list in the left rail (or a legend overlay) — click a name to highlight/frame that vessel; reuses `FlyToHotspot`'s camera animation.
- Reuse `TraverseMeshes`, the `_originalDiffuse` cache, and the hotspot details UI — no new rendering primitives.

### M3 — Perfusion territories overlay (G4)

- **[NEW]** "Perfusion territories" toggle: tint each myocardial region with its supplying vessel's colour (semi-transparent overlay on the skin), driven by the §3-option-1 **territory label atlas** decoded like `InfarctTextureSet` (add `TerritoryTextureSet` alongside it, or extend it). Colours come from `CoronaryTaxonomy` so vessel highlight (M2) and territory tint agree.
- Legend maps colour → vessel name. This is the visual bridge the ТЗ calls out ("colours of coronary arteries … needed for the infarct textures").

### M4 — Territory-driven infarct + wire the MI button + affected leads (G5)

- **[CHANGE] `InfarctTextureBlender` / `InfarctTextureSet`** — blend the necrosis **only inside a selected territory** (mask the global blend by the territory label ⇒ `Blend(progress, territoryId)`), instead of one baked global mask. Keep the current whole-heart path as `territoryId = All`. Unit tests in `InfarctTextureBlenderTests`.
- **[NEW] wire `AppStrings.Monitor3DMi`** (`Heart3DDialog.cs:387`) → an **"MI / occlusion" panel**: pick a coronary branch (from M2/CoronaryTaxonomy) → its territory becomes the infarct target → the existing 0–1 slider + "develop" animation now blackens **that** territory. The infarct→conduction-block coupling in the wavefront (`MaybeResolveWavefrontForInfarct`) already keys off the mask, so it follows for free.
- **[NEW] affected-lead readout** (§3 Q4) — show the occluded vessel's `affectedLeads[]` beside the reference ECG strip.
- Replaces the standalone "Infarct (necrosis)" group's *global* behaviour with a vessel-scoped one (global stays available as "whole heart").

### M5 — Beating-heart animation (G6)

- **[NEW]** If the imported `.glb` carries an animation clip, drive it with HelixToolkit's animation updater from the existing `CompositionTarget.Rendering` loop (`OnCompositionRendering`), paced to `_bpm`, with a "Beat" play/pause in the left rail. **Sync** the animation phase with the conduction pulse / R-wave so contraction and depolarisation agree.
- **[NEW] procedural fallback** (§3 Q3) — no clip ⇒ a subtle BPM-paced anisotropic scale pulse of the myocardium meshes, so the control is never dead.
- Interactions to settle: animation + cutaway (freeze or animate the cross-section), animation + infarct (necrotic tissue should visibly hypo/a-kinese if cheap; otherwise just don't fight the blend).

### M6 — Android parity sync plan

- Run the **`create-prompt`** skill to emit `docs/plans/sync/2026-08-android-3d-heart-layers-…-parity.md`, mirroring: `CoronaryTaxonomy` (Core, hand-ported), the layer/naming convention, territory-scoped infarct, and the beating animation, into the Android Kotlin renderer. Same pattern as `2026-08-android-3d-heart-texture-infarct-parity.md`.

---

## 5. Cross-cutting concerns

- **Graceful degradation** is mandatory: classify → if a layer/sidecar/clip is absent, its control is `Collapsed` (the infarct feature is the template). A plain single-mesh heart must still load and orbit exactly as today.
- **Localization**: every new label goes through `AppStrings` (`Monitor3D*`) with EN/RU, per `[[acronym-taxonomy-wiring]]` and the existing dialog strings. Vessel/territory/lead names are RU-first from the taxonomy.
- **Admin vs User role** (`_isAdmin`): authoring-type controls (editing layer maps, territory authoring) are instructor-only, consistent with the existing hotspot/pathway gating (`[[admin-user-runtime-role]]`).
- **Performance**: the dataset can be large (`[[large-dataset-virtualize-lists]]`); keep decode/blend on background threads (existing `Task.Run` + coalescing pattern in `BuildAndApplyAsync`), and keep territory decode one-shot like `InfarctTextureSet`.
- **Rendering gotchas** already learned: Assimp imports glTF as **Phong not PBR** (`[[heart3d-infarct-texture-pipeline]]`); `PhongMaterial.VertexColorBlendingFactor` must be 1 for per-vertex colour (`[[wavefront-vertex-color-rendering-broken]]`). Territory tint and branch highlight must respect these.
- **Theme**: dialog + any new sub-dialogs must set `RequestedTheme` (`[[dialog-webview-theme-propagation]]`) and honour light-dismiss (`[[dialog-light-dismiss]]`).

## 6. Risk / dependency summary

- **Primary dependency is the 3D asset**, not code. M1–M5 are blocked on the artist delivering a model that honours the M0 contract (named layers, territory encoding, animation clip, two skins). The app work can proceed against a **stub model** authored to the contract in parallel.
- **Territory ↔ UV alignment**: territories must share the skin's UV atlas (same requirement `InfarctTextureSet` already enforces by rejecting mismatched dimensions).
- **Animation retargeting**: skeletal clips can import with scale/orientation quirks through Assimp; the procedural fallback de-risks a "beating" control that must always work.
- **Scope**: coupling the occlusion to the *real* ECG engine is explicitly deferred (§3 Q5).

---

## 7. Deliverables checklist

- [ ] M0 `docs/asset-spec-3d-heart.md` + `CoronaryTaxonomy.cs` (+ tests)
- [ ] M1 Layer manager + educational/realistic skin switch
- [ ] M2 Named, clickable, highlightable coronary branches
- [ ] M3 Perfusion-territory overlay + legend
- [ ] M4 Territory-scoped infarct + wired MI button + affected-lead readout
- [ ] M5 Beating animation (clip + procedural fallback)
- [ ] M6 Android parity sync plan
