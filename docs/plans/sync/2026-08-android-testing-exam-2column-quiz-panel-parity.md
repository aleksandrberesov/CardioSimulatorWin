# Plan: Port Testing and Exam 2-Column ECG Display & Quiz Control Panel to Android

**Created:** 2026-08-15  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

In the Windows edition of CardioSimulator (`CardioSimulatorWin`), the customer requested:
1. Standard ECG display in Testing (`TestingScreen`) and Examination (`ExaminationScreen`) modes to default to **2 columns of 6 leads** (`SeriesScheme.TwoColumn`).
2. Bottom control panel in Testing and Examination modes to feature **Zoom**, **Filter**, and **Start-Stop** buttons when an ECG question stimulus is active.

This plan details the steps required to achieve 1:1 functional parity in the Android application (`CardioSimulator`).

---

## 2. Part A: Domain Model Defaults (`Test.kt` / `TestQuestion`)

### 1. Default Lead Scheme in `TestQuestion`
- **File:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\domain\Test.kt` (or domain model for test questions).
- **Change:** Update default `scheme` in `TestQuestion` constructor from `SeriesScheme.Grid` to `SeriesScheme.TwoColumn`.

---

## 3. Part B: Quiz Mode Control Panel (`MonitorControlPanel.kt` / `TestingControlPanel.kt`)

### 1. Monitor Control Panel Filtering
- **File:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\ui\controls\MonitorControlPanel.kt`
- **Change:**
  - Add a flag or function (e.g., `configureForQuiz()`) to hide non-quiz tools (electrodes, 3D heart, pQRSt, EOS, tips, compare, ruler, artifacts) while displaying **Zoom** (`ScaleTab`), **Filter** (`FiltersTab`), and **Start-Stop** (`StartStopTab`).

---

## 4. Part C: Screen Integration (`TestingScreen.kt` & `ExaminationScreen.kt`)

### 1. Default Mode Settings & Bottom Panel Wiring in `MainScreen.kt` / Screen Hosts
- **Files:**
  - `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\ui\screens\TestingScreen.kt`
  - `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\ui\screens\ExaminationScreen.kt`
  - `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\ui\screens\MainScreen.kt`
- **Changes:**
  - On switching to `OperatingMode.Testing` or `OperatingMode.Examination`, set `monitorViewModel.setSeriesCount(12)` and `monitorViewModel.setSeriesScheme(SeriesScheme.TwoColumn)`.
  - Pass/bind a quiz-configured `MonitorControlPanel` to `BottomControlPanel.content`.
  - Listen to monitor visibility changes (when live ECG question is showing vs. launcher/score/image question) and collapse/show the bottom panel accordingly.

---

## 5. Part D: Verification

### 5.1 Manual Verification Flow
1. Open the Android application in emulator or on device.
2. Select **Testing** mode and start an ECG test. Verify:
   - Monitor lays out 12 leads into **2 columns × 6 rows**.
   - Bottom bar contains **Zoom**, **Filter**, and **Start-Stop** controls.
   - Non-ECG or score views automatically hide the bottom monitor controls.
3. Select **Examination** mode (Individual Exam) and repeat the verification.
