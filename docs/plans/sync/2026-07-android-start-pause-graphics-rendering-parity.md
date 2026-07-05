# Plan: Port Start-Pause Graphics Rendering Behavior to Android

**Created:** 2026-07-05  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

A critical bug was fixed in the Windows version where starting and stopping the ECG monitor caused the graph to jump visually. This occurred because stopping (pausing) the monitor reset the rendering offsets to `0`, and the background stopwatch kept running, causing a time jump when resumed. 

We transitioned the start-stop behavior into a clean start-pause behavior. On pause, the trace freezes in place, and on resume, it starts scrolling smoothly from the exact frozen state.

To maintain feature parity, we need to port this behavior to the Android repository.

---

## 2. Part A: Monitor Clock & State Updates

We need to implement a pause-resistant clock logic in the main ECG display control (equivalent to `EcgMonitorControl` or its Compose counterpart on Android).

### 2.1 Implementing Pause-Resistant Clock in Android View/Compose Component
- Identify the component driving the live monitor redraw timer (e.g. standard Compose drawing loop or canvas timer).
- Replace the raw/monotonically increasing time tracker (like `System.nanoTime()` or a running stopwatch) with a pause-resistant time tracker:
  - Add an `accumulatedTimeMs` variable (initial value `0`).
  - Add a helper stopwatch/timer state (`stopwatch` or custom startTime reference).
- Manage clock transitions when `isRunning` state of the monitor changes:
  - **Transition from stopped to running**: Start/restart the stopwatch/timer.
  - **Transition from running to stopped**: Stop the stopwatch/timer, add the elapsed time during this run to `accumulatedTimeMs`, and reset the stopwatch.
- Compute the elapsed seconds to pass to the renderer as `(accumulatedTimeMs + currentStopwatchElapsedMs) / 1000.0f`.

*Note: Android's `PreviewPane` or equivalent thumbnail loop might already have a similar pause-resistant pattern. Check it for reference implementation.*

---

## 3. Part B: Renderer Modifications

Update the ECG rendering pipeline (equivalent to `EcgRenderer.kt` on Android):

### 3.1 Trace Offset calculation
In the trace drawing logic (drawing the baseline-zeroed waveform):
- Remove the check on `isRunning` that clears the offset to `0f` when the monitor is stopped.
- Always compute the trace translation offset based on the elapsed seconds, even when `isRunning` is `false`:
  ```kotlin
  val xOffset = directionSign * (elapsedSeconds * pxPerSec % periodPx)
  ```
  *(Instead of returning `0f` when `isRunning` is `false`)*

### 3.2 Grid Offset calculation
In the grid drawing logic:
- Remove the check on `isRunning` that resets the grid scroll offset to `0f`.
- Always compute the grid scroll offset based on the elapsed seconds:
  ```kotlin
  val gridOffset = streamSign * (elapsedSeconds * scale.pxPerSec)
  ```
  *(Instead of returning `0f` when `isRunning` is `false`)*

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open the Android application on an emulator or device.
2. Select a rhythm and press the **Start** button to run the live monitor.
3. Press **Stop** (Pause) button. Verify that the ECG trace and grid freeze immediately in their current states, without snapping back to their initial positions.
4. Press **Start** again. Verify that the trace resumes scanning smoothly from the frozen positions without jumping.
