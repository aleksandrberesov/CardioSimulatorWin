# Plan: Port Last-Built App Launcher Tooling to Android / Script Environment

**Created:** 2026-08-23  
**Status:** COMPLETE (Windows Tooling Added) / NOT APPLICABLE (Android uses Gradle / adb install-run)  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\`  

---

## 1. Background & Goals

A new developer tool script (`tools/run-last-built.ps1`, with `tools/run.ps1` and `run.ps1` wrappers) was created in the Windows repository to inspect build output folders (`artifacts/publish`, `src/CardioSimulator.App/bin/...`), discover the most recently built application binary (`antiAI-ECG-Simulator.exe`), stop any old running instances, and launch the binary immediately.

This document records the tooling change for parity assessment on the Android side.

---

## 2. Part A: Windows Tooling Implementation

- `tools/run-last-built.ps1`:
  - Dynamically reads `<AppBrandFileName>` from `Directory.Build.props`.
  - Searches `artifacts/` and `src/CardioSimulator.App/bin/` for candidate `.exe` files.
  - Sorts candidate executables by `LastWriteTime` (newest first).
  - Supports `-List` to preview candidate builds.
  - Supports `-Configuration`, `-Platform`, `-Publish`, `-Select`, `-Path`, `-NoKill`, and `-AppArgs`.
  - Stops previous app processes before launching.
- `tools/run.ps1` and `run.ps1`: Forwarding script wrappers for quick execution (`.\run.ps1`).

---

## 3. Part B: Android Equivalents

Android development uses `adb` / `./gradlew installDebug` / `tools/android-cli` commands:
- `./gradlew installDebug` builds and installs the latest APK to a connected device or emulator.
- `adb shell am start -n com.example.cardiosimulator/.MainActivity` launches the installed app on the target device.

If equivalent PowerShell/Bash runner scripts are desired for Android device execution, create `tools/run-last-apk.ps1` using `adb install -r <path_to_latest_apk>` and `adb shell am start`.

---

## 4. Part C: Verification

### 4.1 Windows Verification
1. Run `.\run.ps1 -List` to view all candidate builds sorted by last modified time.
2. Run `.\run.ps1` to launch the most recently built binary automatically.
3. Run `.\run.ps1 -Publish` or `.\run.ps1 -Configuration Debug` to run specific target configurations.
