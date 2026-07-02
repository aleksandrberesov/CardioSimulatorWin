# Task: Add Sketchfab-style annotation hotspots to my HelixToolkit.SharpDX 3D viewer

## Context

- .NET desktop app (WPF) rendering 3D models in a `Viewport3DX` from **HelixToolkit.Wpf.SharpDX**.
- Models are loaded with **HelixToolkit.SharpDX.Assimp** (`Importer`, i.e. SharpAssimp).
- The current model is a cardiac anatomy scene: ~38 sub-meshes, vertex-colored, one shared PBR material. It was originally a Sketchfab USDZ; since Assimp doesn't read USDZ it has been converted to a supported format (glTF/GLB or OBJ/FBX). Do not add USDZ import support — assume the model loads through the existing Assimp pipeline.
- On Sketchfab's web viewer this model has "annotations": numbered clickable pins anchored to points on the model; clicking one smoothly flies the camera to a saved viewpoint and shows a title/description. That data is NOT in the exported file — we are rebuilding the feature in-app.

Before writing code, explore the solution:
1. Locate the `Viewport3DX`, the model-loading code, and the camera setup. Identify whether MVVM is used and follow the existing pattern.
2. Check the exact HelixToolkit package versions in the .csproj, and verify real API names against the installed version (e.g. camera animation extensions, `FindHits`, Project/UnProject helpers) — these have drifted between versions. Prefer whatever the codebase already uses.

## Goal

Replicate Sketchfab's annotation UX:

1. **Markers**: numbered clickable pins rendered over the model, anchored to fixed world-space points on its surface. They must track the model correctly while orbiting/panning/zooming, and hide when their anchor is behind the camera.
2. **Fly-to on click**: clicking a marker smoothly animates the camera (position + look direction + up direction) to that hotspot's stored viewpoint over ~600–900 ms with easing. Show the hotspot's title and optional description (small panel or tooltip).
3. **Persistence**: hotspots load from / save to a JSON file next to the model, e.g. `<modelFileName>.hotspots.json`.
4. **Authoring mode**: a toggle (button or hotkey) that lets me create hotspots at runtime:
   - I first frame the camera exactly how I want the saved view to look.
   - Then I click a point on the model surface → ray hit-test (`Viewport3DX.FindHits` or equivalent) gives the anchor point → prompt me for title/description → save hotspot with the CURRENT camera pose as its viewpoint.
   - Also support deleting a hotspot (e.g. right-click a marker in authoring mode).
   - Changes persist to the JSON file immediately.

## Suggested data model

```csharp
public class Hotspot
{
    public string Id { get; set; }              // GUID
    public int Number { get; set; }             // display order, 1-based
    public string Title { get; set; }
    public string Description { get; set; }
    public float[] Anchor { get; set; }         // world-space [x,y,z] on model surface
    public float[] CameraPosition { get; set; } // [x,y,z]
    public float[] CameraLookDirection { get; set; }
    public float[] CameraUpDirection { get; set; }
}
```

Keep serialization simple (System.Text.Json). Feel free to adjust types, but keep the JSON human-editable.

## Implementation notes

- **Marker rendering**: preferred approach is a transparent `Canvas` overlaying the `Viewport3DX` in the same Grid cell (`IsHitTestVisible=false` on the canvas itself, true on the marker buttons). Each frame the camera changes (camera-changed event, `OnRendered`, or `CompositionTarget.Rendering` — pick what's least wasteful), project each anchor from world space to screen space and position the markers. Cull markers whose projection lands behind the camera. Account for DPI scaling when mapping projected coordinates to canvas coordinates. If a clean 3D alternative fits the codebase better (billboard sprites via HelixToolkit's billboard primitives), you may use it instead — but screen-space buttons are simpler for click handling and text.
- **Camera animation**: use HelixToolkit's built-in camera animation extension if the installed version has one (e.g. an `AnimateTo`-style method taking position/look/up/duration). If not, hand-roll a tween: interpolate `Position`, `LookDirection`, `UpDirection` with an ease-in-out curve on the UI dispatcher; normalize/re-orthogonalize directions each step to avoid roll flips. Never block the UI thread.
- **Type conversions**: watch SharpDX `Vector3` vs `System.Windows.Media.Media3D` `Point3D`/`Vector3D` — the WPF camera uses Media3D types while hit results use SharpDX types. Centralize the conversions.
- **Occlusion (nice-to-have, only if quick)**: fade markers whose anchor is occluded by geometry (ray from camera to anchor; if the first hit is nearer than the anchor, dim the marker). Skip if it complicates things — Sketchfab-style always-visible pins are acceptable.

## Acceptance criteria

- Solution builds; existing model loading and navigation are unaffected.
- Markers stay glued to their anatomical landmarks during orbit/pan/zoom, with no visible lag, and disappear when behind the camera.
- Clicking a marker flies the camera smoothly to the stored pose and displays title/description.
- Authoring mode: can add a hotspot (surface click + current camera captured), can delete one; JSON file updates and reloads correctly on app restart.
- Hotspot JSON lives beside the model file and is created automatically if missing.
- Brief usage notes added (README section or code comments): how to toggle authoring mode, where the JSON lives.

## Out of scope

- USDZ parsing/import.
- Reading annotation data from Sketchfab or its API.
- Multi-model management — single active model is fine.

---

## Outcome — shipped (2026-07-02)

Implemented in `Heart3DDialog` (WinUI3 / `HelixToolkit.WinUI.SharpDX`), not WPF as the prompt
assumed — the viewport is a `Viewport3DX` on a `SwapChainPanel`. All acceptance criteria met:

- **Markers** — `UpdateHotspotMarkers()` projects each anchor to screen space (`Viewport3DX.Project`)
  onto a transparent overlay `Canvas`; re-run on every camera change via `CompositionTarget.Rendering`
  (`OnCompositionRendering`, guarded by a last-pose check). Culls anchors behind the camera
  (`Dot(toAnchor, look) <= 0`) and divides by `XamlRoot.RasterizationScale` for DPI.
- **Fly-to** — `FlyToHotspot` → `CameraAnimator` tweens position/look/up over 800 ms with a cubic
  ease-in-out on the UI dispatcher, re-normalizing directions each step; title/description shown in a
  bottom-center details card.
- **Persistence** — `<modelFileName>.hotspots.json` via `System.Text.Json` (`WriteIndented`), with a
  `%LOCALAPPDATA%\...\Models` fallback when the model dir is read-only.
- **Authoring mode** — toggle button; surface click uses `Viewport3DX.FindHits` for the anchor and
  captures the current camera pose; right-tap a marker deletes it; every change saves immediately.

Code: `src/CardioSimulator.App/Controls/Heart3DDialog.cs`, `.../Controls/Hotspot.cs`. Committed
(working tree clean); usage documented in the class/method doc comments. Occlusion fade was left out
(explicitly nice-to-have only).
