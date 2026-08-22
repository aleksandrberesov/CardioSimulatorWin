# Plan: Port ECG Constructor Display Filter Rendering Behavior to Android

**Created:** 2026-08-22  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the ECG Constructor mode, users can select an ECG display filter (None, Lowpass 40Hz, Highpass 0.5Hz, Bandpass 0.5–40Hz) via the constructor control panel. While the bottom looping preview strip and 12-lead overview applied this display filter, the primary editable lead canvas (`RenderEditableLead` in `EcgRenderer.cs`) drew raw baseline-zeroed samples without filtering. As a result, selecting a display filter had no visual effect on the main editable lead graphic.

In Windows, `RenderEditableLead` now checks `mode.FilterType` and applies `EcgDisplayFilter.Filter(...)` to the baseline-zeroed values array before constructing the Canvas geometry, rendering significant points, and positioning the selected-sample handle.

This plan details porting this behavior to the Android edition so that selecting a filter in the Android ECG Constructor filters the primary canvas graphic identically.

---

## 2. Part A: Editable Lead Canvas Rendering (Android Canvas / Compose)

### Target Component
- `EcgRenderer.kt` (or `EditableLeadCanvas.kt` / `EditableLeadView.kt` under `com.example.cardiosimulator.ui` or `rendering`).

### Instructions
1. Inspect the method responsible for drawing the editable lead trace in the Android ECG Constructor (`renderEditableLead` or equivalent canvas draw function).
2. Ensure the baseline-zeroed sample array is processed through `EcgDisplayFilter.filter(values, mode.filterType, sampleRate)` before drawing the path line, significant points, and selection handle dot.
3. Verify that raw sample editing (ADC value modifications) continues to mutate the underlying `samples` array, while display rendering applies the selected zero-phase filter band (`Lowpass`, `Highpass`, `Bandpass`).

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Launch CardioSimulator on Android.
2. Open **ECG Constructor** mode.
3. Observe the main editable lead canvas waveform.
4. Open the filter menu in the constructor bottom control panel and select **Lowpass (40 Hz)** or **Highpass (0.5 Hz)** or **Bandpass (0.5–40 Hz)**.
5. Verify that the primary editable lead graphic immediately reflects the filtered signal (smoothing high frequency noise or stripping baseline wander), matching the bottom preview strip and 12-lead overview.
