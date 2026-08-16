# Plan: Sync Default Courses Dataset (Courses.pak) to Android

**Created:** 2026-08-15  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\assets\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\CardioSimulator.App\Assets\`  

---

## 1. Background & Goals

The Windows application updated its default bundled course dataset by copying the full `Courses.pak` (476,273 bytes) dataset into `src/CardioSimulator.App/Assets/Courses.pak` and updating `src/CardioSimulator.App/Assets/Courses.zip`.

This plan documents updating the default course pack bundled with the Android app assets to ensure parity with Windows.

---

## 2. Part A: Update Android Default Courses Asset

- Copy `E:\VLN_Project\CardioSimulator\Data\Courses.pak` (or `E:\VLN_Project\CardioSimulatorWin\src\CardioSimulator.App\Assets\Courses.pak`) to the Android assets directory `E:\VLN_Project\CardioSimulator\Android\app\src\main\assets\Courses.pak`.
- Update any Android dataset loader preferences or asset references to load `Courses.pak` by default when no user-selected course pack is configured.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Launch the Android CardioSimulator app.
2. Verify that default courses load properly from the bundled `Courses.pak`.
3. Verify course lectures, tests, and content match the Windows version dataset.
