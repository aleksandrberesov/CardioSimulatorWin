# Plan: Port Monitor Vertical Baseline Padding (Shift Down) Behavior to Android

**Created:** 2026-09-19  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the Windows version, the lead baseline row compaction constant `RowEdgePad` was previously set to `0.3f`, placing row baselines too close to top cell edges. This caused calibration pulses (1 mV) and high R-wave peaks in the top row (e.g. Lead I / V1) to extend beyond the top border of the monitor grid and clip/cut off.

Setting `RowEdgePad = 0.5f` in `EcgRenderer.cs` centers the row baselines within their respective cells, shifting all monitor traces and calibration pulses slightly down away from the top monitor canvas border.

---

## 2. Part A: Renderer Baseline Padding Update

### Reference File (Windows)
- `E:\VLN_Project\CardioSimulator\Win\src\CardioSimulator.App\Rendering\EcgRenderer.cs`

### Target File (Android)
- `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\rendering\EcgRenderer.kt` (or Compose ECG layout)

### Changes Required
1. Ensure the row edge padding factor (`RowEdgePad`) or vertical row baseline calculation uses standard centered cell height (`0.5f * cellH` padding).
2. Verify that top row baselines (`baselineY` for `row = 0`) leave sufficient clearance for 1 mV calibration pulses (10 mm) without clipping against top canvas bounds.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Launch the ECG Monitor view displaying a 12-lead (or 6-lead / multi-lead) layout with high amplitude rhythms or calibration pulses enabled.
2. Inspect the top row (Lead I, Lead V1).
3. Confirm that calibration pulses (10 mm / 1 mV) and peak R-waves remain fully visible and are not clipped by the top edge of the monitor grid.
