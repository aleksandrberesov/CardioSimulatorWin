# Plan: Sync BioSPPy ECG DSP, Synthesis, and Landmark Features to Android

**Created:** 2026-06-27  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `C:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

During the Windows development phase, we ported Python's **BioSPPy** library to C# (`BioSPPy.Net`) and integrated its features into the WinUI 3 user interface. This added:
- **Zero-Phase Digital Filtering** (Lowpass, Highpass, Bandpass Butterworth filters).
- **Signal Quality Indices (SQI)** with ZZ2018 fuzzy classification metrics.
- **Dolinský et al. Analytical Synthesizer** for generating physiological waveforms.
- **Landmark Auto-Detection** (QRS Hamilton segmenter + fiducial point extraction for P-Q-R-S-T peaks and boundaries).

To maintain absolute feature parity across platforms, the Android codebase must implement the same DSP capabilities in Kotlin (or utilizing a Kotlin-port library) and wire them to the corresponding Jetpack Compose UI elements.

---

## 2. Part A: Porting Core Algorithms to Kotlin

Create a package (e.g., `com.example.cardiosimulator.signals.biosppy`) containing the mathematical translation of the ported C# code.

### 2.1 Filtering & DSP Primitives
Implement:
1. **Butterworth Filter Design**: Design critical poles and zero-locations, warp frequencies, and execute the Bilinear Transform.
   - Signature matching: `fun butterworth(order: Int, Wn: DoubleArray, band: String = "lowpass"): Pair<DoubleArray, DoubleArray>`
2. **Direct Form II `lfilter`**:
   - Signature matching: `fun lfilter(b: DoubleArray, a: DoubleArray, x: DoubleArray, zi: DoubleArray? = null): Pair<DoubleArray, DoubleArray>`
3. **Linear-Time Steady-State Initializer `lfilter_zi`**:
   - Matches SciPy's output using a linear algebra solver (or companion matrix setup) to compute the steady-state step response.
4. **Zero-Phase Forward-Backward Filter `filtfilt`**:
   - Pad the input array with reflective padding of size `3 * (order + 1)`, run `lfilter` forward with steady-state initial conditions, reverse the output, run `lfilter` backward, reverse the result, and slice away the padding.

### 2.2 QRS Segmenters & Corrector
- **Hamilton Segmenter**: Baseline Butterworth bandpass filtering, absolute derivative, moving average smoothing, and adaptive dual-threshold tracking.
- **SSF Segmenter**: Slope sum function-based candidate selection.
- **Correct R-Peaks**: A window-based peak corrector that takes candidate R-peaks and snaps them to the local maximum within a tolerance window (default 50 ms):
  ```kotlin
  fun correctRPeaks(signal: DoubleArray, rpeaks: IntArray, fs: Double, tolSec: Double = 0.05): IntArray
  ```

### 2.3 Fiducial Landmarks (P, Q, R, S, T Peaks & Boundaries)
- **Local Extrema (`argRelExtrema`)**: Finds indices of relative minima/maxima.
- **P, Q, R, S, T segmenters**: Port the template-based extraction loops in `FiducialPoints.cs` where:
  - Templates of size 300 are extracted using $200\text{ ms}$ before and $400\text{ ms}$ after R-peaks.
  - Peaks and onset/offset boundaries are localized relative to R-peaks.

### 2.4 Signal Quality Indices (SQI)
- **SSQI** (Skewness), **KSQI** (Kurtosis), and **PSQI** (Flatline percentage).
- **FSQI**: Welch PSD power ratio (using Trapezoidal integration).
- **ZZ2018 Classifier**: Classifies quality into `"Excellent"`, `"Barely acceptable"`, or `"Unacceptable"`. Provide a `"fuzzy"` mode implementing the fuzzy membership functions (triangular and trapezoidal) mapped in `Sqi.cs`.

### 2.5 Dolinský Analytical Synthesizer
- Generates a synthetic heartbeat cycle using mathematical segment formulations.
- Parameters: `Kb` (PR segment duration), `Ap`/`Kp` (P-wave amp/dur), `Aq`/`Kq` (Q-wave amp/dur), `Ar`/`Kr` (R-wave), `As`/`Ks` (S-wave), `At`/`Kt` (T-wave), `Ki` (TP interval flat line duration), and normal-distribution variability (`var`).

---

## 3. Part B: UI Integration in Android (Compose)

### 3.1 Live Monitor Digital Filter Dropdown
1. **Model state**: Add `EcgFilterType` enum (`NONE`, `LOWPASS`, `HIGHPASS`, `BANDPASS`) to `MonitorMode` / `MonitorViewModel`.
2. **Dropdown Menu**: Add a "Filters" dropdown tab button in the monitor's control panel (next to "Artifacts"). Let users choose the active filter.
3. **Dynamic Filtering**: Before passing waveforms to the charts canvas renderer:
   - If a filter is selected, run `filtfilt` on the double-precision copy of the waveform.
   - Convert back to floats and render the smoothed waveform. Ensure comparison mode waveforms are also filtered.

### 3.2 Floating SQI Status Card
1. **UI Layout**: Add a floating glassmorphic `Card` overlay on the top-right of the ECG chart grid.
2. **Real-time update**: Compute SQI statistics on the first visible lead:
   - Run Hamilton + SSF segmenters to get detectors 1 and 2.
   - Run `ZZ2018` fuzzy classification to check quality.
   - Display the skewness, kurtosis, flatline percentage, and quality text.
   - Render a colored LED dot: Green (`Excellent`), Yellow (`Barely acceptable`), or Red (`Unacceptable`).

### 3.3 Dolinský Synthesizer Dialog
1. **Toolbar Button**: Add an `Audio` symbol button to the constructor toolbar next to "Manage elements".
2. **Parameter Sheet**: On tap, open a `ModalBottomSheet` or `AlertDialog` with sliders for Heart Rate (BPM), wave amplitudes, durations, and variability.
3. **Generation**: On confirmation:
   - Calculate sample rate (from Calibration) and determine target RR interval samples.
   - Compute remaining flat line segment (`Ki`) dynamically.
   - Execute generation, convert output millivolts to baseline-calibrated ADC values (`1024` baseline), and repeat the generated beat to fill the duration of the edited lead.
   - Overwrite lead samples and record undo state.

### 3.4 Landmark Auto-Detection
1. **Sidebar Button**: Add an "Auto-Detect" button at the top of the Significant Points marker sidebar panel.
2. **Pipeline Execution**: On tap, run:
   - QRS Hamilton segmentation + peak correction on the active lead samples.
   - Fiducial landmark boundaries locator.
   - Auto-fill the lead's marked points with the detected `R_PEAK`, `Q_PEAK`, `S_PEAK`, `P_PEAK`, `T_PEAK`, and boundaries. Update the graph labels immediately.

---

## 4. Acceptance Checklist

- [ ] Core `biosppy` package implemented in Kotlin with zero-phase filtering, segmenters, landmarks, SQIs, and synthesizers.
- [ ] Filters tab added to the monitor toolbar; waveforms filter dynamically in zero-phase forward-backward Butterworth format.
- [ ] Glassmorphic floating SQI card displays sSQI, kSQI, pSQI, and fuzzy ZZ2018 quality state (LED dot).
- [ ] Synthesizer dialog implemented in the editor, allowing user-customized analytical signal generation.
- [ ] "Auto-Detect" button in landmark panel extracts R-peaks and segment boundaries automatically.
- [ ] Unit tests written to verify algorithm correctness against reference Python data.
