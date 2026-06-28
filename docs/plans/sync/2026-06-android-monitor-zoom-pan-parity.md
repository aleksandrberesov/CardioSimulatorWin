# Plan: Sync constant-thickness zoom + constructor Pan tool to Android

**Created:** 2026-06-27
**Status:** NOT STARTED
**Direction:** **Windows → Android** (reverse of the usual). Both changes were made in the WinUI 3
port first; Android must mirror them so the two platforms behave identically.

**Target (Android) source root:** `C:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`

This plan covers two independent features. They can land in separate commits:

- **Feature A — Constant-thickness zoom.** When the monitor is zoomed, line thickness must stay
  *visually equal* at every zoom level (and the trace must stay crisp, not blurry).
- **Feature B — Pan tool in the constructor.** A first-class "Pan" tool on the editable-lead
  monitor so the user can drag to move the (zoomed) view, plus a "Reset view" action.

> Note on platform gestures: Windows uses **mouse wheel = zoom**, **right-drag = pan (any tool)**,
> and **left-drag = pan only in Pan mode**. Android is touch-first, so the *mechanics* differ
> (pinch = zoom, one-finger drag), but the **rendering math and the Pan tool concept are identical**.
> Mirror the math exactly; adapt only the gesture plumbing.

---

# Feature A — Constant-thickness zoom

## Background — what was wrong and why

The Windows monitor (and the Android `Monitor`, per the existing port comments) applied zoom/pan as
a **layer transform** — Windows used a XAML `ScaleTransform`; Android uses `Modifier.graphicsLayer`
with `scaleX/scaleY/translation` (the "pinch/graphicsLayer transform" the WinUI `MonitorView` was
ported from). A layer/graphics-layer scale **rasterizes then scales**, which:

1. **Thickens every stroke** — a 1.5 px trace becomes 3 px at 2× — and
2. **blurs** the whole trace, and at high zoom the thin 0.5 px grid lines fade out.

You **cannot** fix this by only dividing stroke widths while keeping the layer scale: under a
rasterize-then-scale layer the grid sub-pixel lines vanish. The zoom has to move **inside the draw**
(vector scaling) so geometry stays crisp, *and* stroke widths are counter-scaled by `1/zoom` so
thickness is constant.

## The fix (what Windows now does)

1. Zoom/pan is applied **inside the Win2D draw** via a transform on the drawing session:
   `ViewTransform = Scale(zoom about canvas centre) · Translate(panOffset)`.
2. `strokeScale = 1 / zoom` is computed once and **every line stroke width is multiplied by it**.
   Marker dot radii and text are **not** scaled (they zoom with the content, as before).
3. Inner per-draw transform reassignments **compose** with the base view transform instead of
   overwriting it (trace tiling and the reference-image underlay), so they keep zoom/pan.
4. At `zoom == 1` (identity, `strokeScale == 1`) output is byte-identical to before — no regression.

## Reference: exact Windows changes to mirror

| Concern | Windows file | Change |
|---|---|---|
| Renderer: view transform + stroke scale | `CardioSimulator.App/Rendering/EcgRenderer.cs` | `Render` & `RenderEditableLead` take `viewZoom/viewOffsetX/viewOffsetY` (default `1/0/0`); add `ViewTransform(w,h,zoom,offX,offY)`; set `ds.Transform = ViewTransform(...)`; `strokeScale = zoom>0 ? 1/zoom : 1` threaded through `DrawGrid`, `DrawCalibrationPulse`, `DrawTrace`, `DrawSignificantPoints`, `DrawComparePane`, `DrawComparePlaceholder`; **every line stroke `* strokeScale`**; inner transforms compose with base (`CreateTranslation(...) * original`, image `matrix * original`) |
| Monitor host: drive zoom | `CardioSimulator.App/Controls/EcgMonitorControl.cs` | `SetView(zoom, offX, offY)` stores view fields + `Invalidate()`; passes them into `Render` |
| Monitor wrapper | `CardioSimulator.App/Controls/MonitorView.cs` | removed the XAML `ScaleTransform`/`TranslateTransform`; `ApplyTransform()` now calls `_monitor.SetView(_scale,(float)_offsetX,(float)_offsetY)` |
| Editable lead | `CardioSimulator.App/Controls/EditableLeadControl.cs` | removed the XAML transform; `OnDraw` passes `_viewScale,(float)_viewOffsetX,(float)_viewOffsetY` into `RenderEditableLead`; `ApplyViewTransform()` → `_canvas.Invalidate()` |

The view-transform / stroke-scale core (source of truth for the math):

```csharp
private static Matrix3x2 ViewTransform(float width, float height, float zoom, float offsetX, float offsetY) =>
    Matrix3x2.CreateScale(zoom, zoom, new Vector2(width / 2f, height / 2f))
    * Matrix3x2.CreateTranslation(offsetX, offsetY);

// inside Render / RenderEditableLead:
var strokeScale = viewZoom > 0f ? 1f / viewZoom : 1f;
ds.Transform = ViewTransform(width, height, viewZoom, viewOffsetX, viewOffsetY);
// ... every line: ds.DrawLine(..., baseStroke * strokeScale);
```

## Steps (Android / Compose)

### A1. Find the monitor's zoom transform
Search the Android monitor composables (`Monitor`, `ChartCanvas`, `ekgGrid`, `CalibrationPulse`,
`LeadsGrid`) for the zoom/pan. Hints: `graphicsLayer`, `scaleX`, `scaleY`, `translationX`,
`detectTransformGestures`, `pinch`, `rememberTransformableState`, `zoom`, `pan`. The current code
almost certainly puts `Modifier.graphicsLayer { scaleX = zoom; scaleY = zoom; translationX = ...; }`
on the Canvas/Box — that is the thing to **remove**.

### A2. Move zoom/pan inside the Canvas draw
Apply the transform **within the `DrawScope`** instead of on a `graphicsLayer`:

```kotlin
Canvas(modifier) {
    val cx = size.width / 2f; val cy = size.height / 2f
    withTransform({
        scale(zoom, zoom, pivot = Offset(cx, cy))
        translate(panX, panY)
    }) {
        // draw grid + traces + calibration pulse + overlays here, in canvas coords
    }
}
```

Keep the gesture state (`zoom`, `panX`, `panY`) hoisted exactly as today (pinch updates `zoom`,
drag updates `pan`, clamped to `size * (zoom-1) / 2` on each axis — same clamp Windows uses). Only
the *application point* of the transform moves from the layer to the DrawScope.

### A3. Counter-scale every stroke by `1 / zoom`
Compute `val strokeScale = if (zoom > 0f) 1f / zoom else 1f` and multiply it into **every**
`drawLine(... strokeWidth = ...)` / `drawPath(... style = Stroke(width = ...))` for:
- grid small lines, grid large lines,
- the trace path, the calibration-pulse path,
- the significant-point overlay (boundary lines, interval/segment brackets, R-R brackets),
- the constructor ghost-trace line and the selected-sample handle ring/cross.

Do **NOT** scale: marker-dot radii, text size (`drawText`/`drawIntoCanvas` font sizes), or label
halos — these intentionally grow with zoom, matching Windows.

Windows stroke constants for reference (pre-scale, in the 160-dp anchor space): grid small `0.5`,
grid large `1.5`, trace `1.5`, calibration `1.5`, ghost `2.5`, sample handle `1.0`, significant-point
boundary `1.5`, interval/RR brackets `3.0`. Match whatever the Android originals are — just multiply
each by `strokeScale`.

### A4. If the editable-lead (constructor) monitor zooms separately
Apply the same A2/A3 treatment to the Android `EditableLead` Canvas. Also compose any inner
transform used for the **reference-image underlay** with the base view transform (so the image
zooms/pans with the trace), mirroring `RenderEditableLead`'s `matrix * original`.

### A5. Redraw on zoom/pan
Because the transform now lives in the draw, ensure a zoom/pan change triggers recomposition/redraw
of the Canvas (Compose `State` already does this when `zoom`/`pan` are `mutableStateOf`). Confirm the
non-running (stopped) monitor still redraws on pinch/drag.

## Verification (Feature A)
- Zoom the monitor to ~3–5×: the **trace and grid line thickness look the same** as at 1× (no
  fattening), and the trace stays **crisp** (no blur); thin grid lines remain visible.
- At 1× the view is unchanged from before.
- Compare mode (if it disables zoom) still tap-selects panes correctly (it runs at zoom 1).

---

# Feature B — Pan tool in the constructor

## Background

The constructor's editable-lead monitor supports zoom, but on the desktop port panning the zoomed
view was hidden behind a right-drag. A first-class **Pan tool** was added to the vertical tool-mode
sidebar (alongside Select / Trace / Position / Points / Image) so the user can drag to move the view
and reset it. On Android (touch) this is even more important because there is no right-click — the
Pan tool is how a one-finger drag becomes "move the view" instead of "edit a sample".

## Reference: exact Windows changes to mirror

| Concern | Windows file | Change |
|---|---|---|
| Tool enum | `CardioSimulator.Core/Domain/ToolMode.cs` | added `Pan` value (now `Select, Trace, Position, Points, Photo, Pan`) |
| Tool sidebar | `CardioSimulator.App/Controls/ToolModePanelControl.cs` | added `(ToolMode.Pan, <Move/4-arrows glyph>, "Pan view (drag to move, wheel to zoom)")` |
| Editable lead gesture | `CardioSimulator.App/Controls/EditableLeadControl.cs` | in Pan mode a primary drag **pans** the view (Windows: `IsRightButtonPressed || _toolMode == ToolMode.Pan`); a move cursor while Pan is active; public `ResetView()` (zoom→1, offset→0) |
| Mode panel | `CardioSimulator.App/Screens/ConstructorScreen.cs` | `BuildPanPanel()` (title + usage hint + **"Reset view"** button calling `_editable.ResetView()`) wired into the tool-mode switch |

## Steps (Android / Compose)

### B1. Add `Pan` to the Android tool-mode enum
Find the Android analog of `ToolMode` (search: `enum class ToolMode`, `Select`, `Trace`, `Points`,
`Position`, the constructor `ToolModePanel`). Add a `Pan` entry.

### B2. Add the Pan button to the tool sidebar
In the Android `ToolModePanel` composable add a Pan icon (a four-arrows / `OpenWith` / pan-tool
material icon) with a content description like "Pan view". Highlight it when active like the others.

### B3. Make a drag pan the view in Pan mode
In the editable-lead gesture handler, branch on the active tool: when `ToolMode.Pan` is selected, a
one-finger drag updates the pan offset (reusing the same `panX/panY` state and clamp from Feature A)
instead of selecting/tracing a sample. (Pinch-zoom should remain available in every tool, as today.)
Optionally show a subtle affordance that Pan is active.

### B4. Add a "Reset view" action
Add a panel/button shown for the Pan tool (the Android analog of `BuildPanPanel`) that resets
`zoom = 1f`, `panX = 0f`, `panY = 0f` — the Android equivalent of `EditableLeadControl.ResetView()`.

## Verification (Feature B)
- Selecting the Pan tool and dragging moves the zoomed editable lead without editing samples;
  switching back to Select/Trace edits as before.
- "Reset view" returns the editable lead to 1× and centred.
- With Feature A in place, panning at zoom keeps line thickness constant.

---

## Acceptance checklist
- [ ] **A:** Android monitor zoom/pan applied inside the Canvas `DrawScope` (no `graphicsLayer` scale on the trace).
- [ ] **A:** every line stroke multiplied by `1/zoom`; marker radii + text left unscaled.
- [ ] **A:** reference-image underlay (constructor) composes with the view transform.
- [ ] **A:** at 1× identical to before; at high zoom thickness constant + trace crisp + grid visible.
- [ ] **B:** `Pan` added to the Android `ToolMode`; Pan button in the tool sidebar.
- [ ] **B:** drag pans the view in Pan mode; pinch-zoom still works in all tools.
- [ ] **B:** "Reset view" resets zoom + pan.
- [ ] Both platforms render the shared dataset identically when zoomed.
