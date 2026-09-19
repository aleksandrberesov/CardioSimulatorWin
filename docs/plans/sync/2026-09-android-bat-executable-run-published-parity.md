# Plan: Port Batch Executable Launcher to Android Project Tooling

**Created:** 2026-09-19  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

A Windows batch executable file (`run-publish.bat` and `run.bat`) was added to the root of `CardioSimulator\Win` to launch the compiled app binary directly from `artifacts/publish/antiAI-ECG-Simulator.exe`.

On Android, equivalent script tooling (`run-apk.bat` / `run-apk.sh`) allows running the assembled APK output directly from `app/build/outputs/apk/release/` onto connected devices or emulators.

---

## 2. Part A: Root Launcher Tooling

- Create `run-apk.bat` in the root of the Android repository (`CardioSimulator`).
- Script checks for existing built APK in `app/build/outputs/apk/release/app-release.apk` or `app/build/outputs/apk/debug/app-debug.apk`.
- If found, deploys and launches the application via ADB (`adb install -r` and `adb shell am start -n com.example.cardiosimulator/.MainActivity`).

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Execute `run-publish.bat` or `run.bat` in `CardioSimulator\Win` to launch the published WinUI 3 executable from `artifacts/publish`.
2. Execute `run-apk.bat` in `CardioSimulator` to verify APK deployment and startup on an active Android emulator or device.
