# Prompt & Specification: Reimplementing BioSPPy ECG Toolbox in C#

Use the following detailed specification to design, architect, and implement a C# library (`BioSPPy.NET`) that ports the ECG processing, analysis, and synthesis capabilities of the Python package **BioSPPy**.

---

## 1. Project Architecture & Target Environment
- **Target Framework**: .NET Core 6.0 or higher.
- **Language**: C# 10+.
- **Design Philosophy**: High performance, memory efficiency (avoid unnecessary array allocations using `ReadOnlySpan<double>` where possible), and strict alignment with original mathematical outputs.
- **Math Library Mapping**:
  - `numpy` & `scipy` -> Use **`MathNet.Numerics`** (linear algebra, statistics, integration, and signal processing) or implement core signal primitives directly.
  - Zero-phase filtering (`scipy.signal.filtfilt`) -> Must be custom-implemented or mapped to a library since standard forward-backward IIR/FIR filters are required.
- **Visualizations**: Code should separate core math/logic from plotting. For optional plotting, define interfaces that can bind to **`ScottPlot`** or **`OxyPlot`**.

---

## 2. Core Data Structures
In place of Python's dynamic `ReturnTuple`, define strongly typed records or read-only structs to enforce type-safety and developer ergonomics:

```csharp
namespace BioSPPy.Net.Signals.Ecg;

public record EcgResult(
    double[] TimeAxis,         // ts (seconds)
    double[] Filtered,         // filtered signal
    int[] RPeaks,              // corrected R-peak indices
    double[] TemplatesTime,    // templates time axis relative to R-peak
    double[,] Templates,       // 2D array [heartbeats, samples] of templates
    double[] HeartRateTime,    // heart rate time axis reference
    double[] HeartRate         // instantaneous heart rate (bpm)
);
```

---

## 3. Module Specification

### Module A: Signal Processing Primitives (`BioSPPy.Net.Signals.Tools`)
Reimplement the core math functions from [biosppy/signals/tools.py](file:///e:/VLN_Project/BioSPPy/biosppy/signals/tools.py):
1. **`FilterSignal`**:
   - Support both **FIR** (using window-based design) and **IIR** (Butterworth) filters.
   - **Crucial Requirement**: Zero-phase filtering (equivalent to Python `filtfilt`). The filter is applied forward, the array is reversed, the filter is applied again, and the array is reversed back. Boundary padding (e.g. padding by reflection) must match `scipy` to prevent transient startup artifacts.
2. **`GetHeartRate`**:
   - Computes instantaneous heart rate (bpm) from R-peak indices.
   - Smooths the heart rate timeline using a moving average window of a user-defined size (default = 3).

---

### Module B: R-Peak Segmentation Algorithms (`BioSPPy.Net.Signals.Ecg`)
Provide implementations for all six QRS detectors. Input signals should be pre-filtered `double[]`:

1. **Hamilton Segmenter (`HamiltonSegmenter`)**:
   - Takes absolute derivative, passes it through a moving average window of length `0.08 * fs`.
   - Maintains adaptive thresholds (`noise_level`, `signal_level`) that decay or adapt based on recent R-peak heights and typical RR intervals (refractory period of 200 ms).
2. **Slope Sum Function (`SsfSegmenter`)**:
   - Computes first-order differences `dx = diff(signal)`.
   - Discards positive slopes: `dx[dx >= 0] = 0`, then squares negative slopes: `dx = dx^2`.
   - Flags points where `dx > threshold`. Around these crossings, find the local maximum in a search window of `[index - 30ms, index + 10ms]`.
3. **Christov Segmenter (`ChristovSegmenter`)**:
   - Integrates derivative components, complex envelopes, and an exponential decay threshold curve that responds adaptively to QRS amplitude variations.
4. **Engzee Segmenter (`EngzeeSegmenter`)**:
   - Compares the differentiated signal against a moving threshold, searching for steep QRS slope changes and slope sign transitions.
5. **Gamboa Segmenter (`GamboaSegmenter`)**:
   - Compute amplitude histogram. Normalize signal by scaling it using bounds calculated from cumulative histogram thresholds (discarding the upper/lower 1% outliers).
   - Compute the second-order derivative `d2`.
   - Identify candidate peaks where `d2` zero-crosses and exceeds a noise tolerance.
   - Enforce a 300 ms refractory window; perform a local maximum search in a 100 ms forward window on the original signal.
6. **ASI Segmenter (`AsiSegmenter`)**:
   - Implements Gutierrez-Rivas (2015) low-complexity double-difference peak tracking with exponential decay curves.

---

### Module C: Cardiac Wave Landmark Detection (Fiducial Points)
Implement local minimum/maximum trackers relative to R-peak index locations:
- **`GetQPositions`**: Find the lowest point within a window of **20ms to 70ms before** the R-peak.
- **`GetSPositions`**: Find the lowest point within a window of **20ms to 70ms after** the R-peak.
- **`GetPPositions`**: Locate the maximum positive deflection preceding the Q-onset.
- **`GetTPositions`**: Locate the maximum deflection following the S-offset.

---

### Module E: Signal Quality Indices (SQIs)
Implement metric formulas to evaluate signal degradation:
1. **`BSQI` (Beat Matching)**: Compare indices from two detectors (e.g. `Hamilton` and `SSF`). Calculate matching beats within a 150 ms tolerance window. Return:
   - `matching = (2 * matches) / (len(det1) + len(det2))`
2. **`SSQI` & `KSQI`**: Stat skewness (`sSQI`) and kurtosis (`kSQI`) calculations over the raw signal strip.
3. **`PSQI` (Flatline Detection)**: Return percentage of indices where the difference `abs(diff(signal))` falls below a threshold (e.g., `0.01 mV`).
4. **`FSQI` (Spectral Ratio)**: Perform Welch PSD analysis. Calculate the ratio of power in the QRS band (5-20 Hz) against the overall spectrum.
5. **`ZZ2018` (Quality Classifier)**:
   - Run the metrics above and map them to quality tiers (Optimal=2, Suspicious=1, Unqualified=0).
   - Provide both `Simple Heuristics` and `Fuzzy Logic` modes. The fuzzy logic implementation requires membership evaluations:
     - $U_{qH}$ (excellent membership): piecewise functions scaling between $0.0$ and $1.0$ based on metric scores.

---

### Module F: ECG Waveform Synthesizer (`BioSPPy.Net.Synthesizers.Ecg`)
Reimplement the Dolinský et al. (2018) analytical synthesizer model.
- Model each beat using mathematical sub-functions:
  - Baseline Drift $B(l, K_b)$
  - P-wave $P(i, A_p, K_p)$
  - PQ interval $P_q(l, K_{pq})$
  - Q-wave (divided into $Q_1$, $Q_2$)
  - R-wave $R(i, A_r, K_r)$
  - S-wave $S(i, A_s, K_s, K_{cs})$
  - ST-segment $S_t$
  - T-wave $T$
  - Isoelectric segment $I(i, \dots)$
- Accept variance scaling factor (`var` $\in [0, 1]$) which shifts each coordinate parameter randomly using normal distribution generators to simulate natural heart rate variability (HRV) and morphology drift.
- Validate parameters against standard physiological limits and log warnings if PR interval, QRS duration, or QT duration exceed boundaries.

---

## 4. Verification Protocol
1. **Cross-Language Unit Testing**:
   - Expose test data from Python examples (`ecg.txt`).
   - Run C# and Python algorithms side-by-side. Assert that:
     - Filtered arrays match within a tolerance of $\epsilon = 10^{-5}$.
     - R-peak location indices match exactly.
     - Extracted heartbeat template values match within $\epsilon = 10^{-5}$.
     - Heart rate arrays match within $\epsilon = 10^{-2}$.
