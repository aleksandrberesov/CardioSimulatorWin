# Plan: Port App Title ("antiAI ECG Simulator") to Android

**Created:** 2026-08-14  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

The Windows application title (`BuildInfo.Name`) has been updated from `"Cardio Simulator"` to `"antiAI ECG Simulator"`. To maintain brand parity across platforms, the Android app name/title resources and UI title headers should be updated accordingly.

---

## 2. Part A: Android String Resources & Manifest Label

- **Target File:** `E:\VLN_Project\CardioSimulator\app\src\main\res\values\strings.xml`
- **Target File:** `E:\VLN_Project\CardioSimulator\app\src\main\AndroidManifest.xml`

### Instructions:
1. Update `app_name` string in `strings.xml`:
   ```xml
   <string name="app_name">antiAI ECG Simulator</string>
   ```
2. Ensure `AndroidManifest.xml` references `@string/app_name` for the application label:
   ```xml
   <application
       android:label="@string/app_name"
       ... >
   ```

---

## 3. Part B: Compose TopAppBars & Header Displays

- **Target Files:** Any Compose top bars or headers that display the app title.

### Instructions:
1. Replace any hardcoded `"Cardio Simulator"` string references with `stringResource(R.string.app_name)`.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Build and launch the Android application in an emulator or connected device.
2. Verify that the app launcher icon label displays `"antiAI ECG Simulator"`.
3. Open the main screen and verify any header/title text reflects `"antiAI ECG Simulator"`.
