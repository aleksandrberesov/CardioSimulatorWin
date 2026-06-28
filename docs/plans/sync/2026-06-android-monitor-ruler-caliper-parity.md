# Plan: Sync monitor ruler / caliper tool to Android

**Created:** 2026-06-28
**Status:** NOT STARTED
**Direction:** **Windows → Android** (Windows port first; Android must mirror it so both platforms behave identically).

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\CardioSimulator.App\`
**Android-side working plan (full phase/PR breakdown):** `E:\VLN_Project\CardioSimulator\docs\plans\active\2026-06-android-monitor-ruler-caliper-parity.md` ← keep these two in sync.

> Builds on **2026-06-android-monitor-zoom-pan-parity.md** (constant-thickness zoom): the caliper
> overlay reuses that zoom model — it is drawn in **screen space, outside the zoom transform**, and
> the readout multiplies the base pixel scale by the current zoom.

---

## Background — the dead button

The monitor's **Ruler** button (the `Straighten` icon rotated -45° in the Teaching control panel) does
nothing on either platform today: on Android `onRulerClick` defaults to `{}` and `MainScreen` never
passes it; on Windows the `Tab` had no `Click` handler. Windows now implements it as an ECG **caliper**:
toggle ruler mode, drag on the monitor to measure a **time interval (ms)**, the implied **rate (bpm)**
and **amplitude (mV)**, drawn as an overlay on the trace. Android must mirror this.

> **Icon note:** Android already uses `Icons.Default.Straighten` rotated -45° — **do not change the
> Android icon.** (Windows had to hand-draw a vector ruler `Path` because Segoe Fluent Icons has no
> ruler glyph; that was purely to *match* Android and is not part of this sync.)

## The measurement math (source of truth — identical on both platforms)

With `zoom` = current view zoom and caliper points A, B in screen px:

```
dtSec = abs(B.x - A.x) / (pixelScale.PxPerSec * zoom)
ms    = dtSec * 1000
bpm   = dtSec > 0 ? 60.0 / dtSec : null        // show "— bpm" when null
mV    = abs(B.y - A.y) / (pixelScale.PxPerMv * zoom)
```

`PxPerSec`/`PxPerMv` exclude zoom on both platforms (Win `PixelScale`, Android `PixelScale.kt`), so the
on-screen scale is `× zoom`. Sanity check: measuring the 1 mV / 0.2 s calibration pulse must read
≈ **1.00 mV** / ≈ **200 ms**.

## Reference: exact Windows changes to mirror

| Concern | Windows file | Change |
|---|---|---|
| Caliper state + overlay draw + readout | `Controls/EcgMonitorControl.cs` | `SetRulerActive(bool)`, `SetCalipers(Point?,Point?)`; `DrawRuler(ds,w,h)` draws a translucent band, two full-height vertical legs, an A→B connector, endpoint dots, and a rounded readout box (Δt ms / bpm / Δ mV). **Resets `ds.Transform` to identity first** because `EcgRenderer.Render` leaves the zoom matrix applied. |
| Pointer routing + zoom freeze | `Controls/MonitorView.cs` | `RulerActive` property → `_monitor.SetRulerActive`; while active a left-drag sets the two caliper points (`SetCalipers`) instead of panning, and **wheel-zoom is frozen**; a bare click (no drag) clears the measurement. |
| Overlay host passthrough | `Controls/MonitorViewerOverlay.cs` | `RulerActive` get/set forwarding to the inner `MonitorView`. |
| Teaching screen | `Screens/TeachingScreen.cs` | `SetRulerActive(bool)`; clears the measurement when the monitor is dismissed (course shown). |
| Toggle button + visual | `Controls/MonitorControlPanel.xaml(.cs)` | Ruler button raises `RulerToggled`, lights up with the accent when active (`ResetRuler` clears it), tooltip via `AppStrings.MonitorRuler`. *(Windows icon = vector ruler `Path`; N/A on Android.)* |
| Host wiring | `Screens/MainScreen.xaml.cs` | `teachingPanel.RulerToggled += (_, active) => teaching.SetRulerActive(active);` and `teachingPanel.ResetRuler()` when the monitor is hidden. |
| Strings | `Localization/AppStrings.cs` | `monitor_ruler` description in EN/RU/ZH/ES. |

## Steps (Android / Compose) — summary

The full phased breakdown lives in the Android active plan; the essentials:

1. **State.** Add `showRuler: Boolean` to the monitor-mode model + `setShowRuler(Boolean)` on
   `MonitorViewModel`, mirroring `showTips` / `showImpulseLabels` exactly (default `false`, same
   persistence behaviour).
2. **Button.** In `ui/panels/MonitorControlPanel.kt` change the ruler `Tab` to the sibling-toggle
   pattern: `onClick = { viewModel.setShowRuler(!monitorMode.showRuler) }`, `isActive =
   monitorMode.showRuler`. Drop the now-unused `onRulerClick` param.
3. **Caliper + overlay** in `ui/display/Monitor.kt`: when `mode.showRuler`, **freeze the
   transformable zoom/pan**, capture two points via `pointerInput { detectDragGestures(...) }`
   (positions are in untransformed screen px), and draw the band + legs + connector + dots + readout
   as a sibling `Canvas`/`Text` **outside** the `withTransform { scale(...); translate(...) }` block
   (lines ~173-182). Use `pixelScale.pxPerSec * scale` and `pixelScale.pxPerMv * scale`. Clear on
   toggle-off and on a bare tap.
4. **Polish.** Disable in compare mode (`mode.isCompareMode`); reset `showRuler` when leaving the
   monitor; optional `monitor_ruler` string for the content description (parity with Windows).

Key Android anchors: ruler tab `MonitorControlPanel.kt:321-327`; missing wiring `MainScreen.kt:353`;
zoom transform `Monitor.kt:173-182`; `Tab.isActive` styling `ui/components/Tab.kt`; `pxPerSec`/`pxPerMv`
in `data/PixelScale.kt`.

## Verification
- `./gradlew :app:assembleDebug` passes.
- Teaching → monitor: tap Ruler → button turns green; drag across two R-peaks → overlay shows ms /
  bpm / mV; the 1 mV / 0.2 s calibration pulse reads ≈ 1.00 mV / ≈ 200 ms; pinch is frozen while the
  ruler is on; turning it off clears the measurement; compare mode shows no ruler overlay.

## Acceptance checklist
- [ ] `showRuler` state added to the monitor mode + `setShowRuler` (mirrors `showTips`).
- [ ] Ruler tab toggles it and lights up (`isActive`); `onRulerClick` param removed.
- [ ] Caliper drag + overlay drawn **outside** the zoom transform; readout uses `* scale`.
- [ ] Zoom/pan frozen while ruler active; measurement clears on toggle-off and bare tap.
- [ ] Disabled in compare mode; reset when leaving the monitor.
- [ ] Android icon left as `Straighten` rotated -45° (unchanged).
- [ ] Both platforms report the same ms/bpm/mV for the same dataset + zoom.
