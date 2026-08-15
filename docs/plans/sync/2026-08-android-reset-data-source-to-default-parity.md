# Plan: Port Reset Data Source to Default Behavior to Android

**Created:** 2026-08-15  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

In the Windows version of CardioSimulator, when a user selects a custom pathology or course content pack (`.pak`), the file path is saved in persistent application preferences (`tree_uri` and `courses_tree_uri`). During application updates, this custom pack takes precedence over updated default bundled content.

To allow users to easily restore default application data, "Reset to default" buttons were added to the Settings dialog for both ECG/Pathology and Course data sources. When clicked, these buttons clear the custom pack URI preferences and re-seed the repositories from the bundled default content packs.

This plan details how to port this behavior and UI action to the Android version of CardioSimulator.

---

## 2. Part A: View Model & DataStore Updates

1. **`DataSourcePrefs.kt` / DataStore:**
   * Ensure methods exist to clear `tree_uri` and `courses_tree_uri` (set keys to null/empty).
2. **`AppViewModel.kt` (or `DataSourceViewModel.kt`):**
   * Add `resetDataFolderToDefault()` method:
     * Clear `treeUri` preference.
     * Re-seed pathology dataset from bundled default asset/pack.
   * Add `resetCourseFolderToDefault()` method:
     * Clear `coursesTreeUri` preference.
     * Re-seed courses dataset from bundled default asset/pack.

---

## 3. Part B: Settings UI Updates (Compose / SettingsScreen)

1. **`SettingsContent.kt` / `SettingsScreen.kt`:**
   * In the ECG Data section, add a "Reset to default" button next to "Change content pack" and "Export pack".
   * In the Course Data section, add a "Reset to default" button next to "Change course pack" and "Export course pack".
   * Wire the button click handlers to call `appViewModel.resetDataFolderToDefault()` and `appViewModel.resetCourseFolderToDefault()`.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Build and launch the Android application on an emulator or physical device.
2. Open Settings.
3. Pick a custom content pack via SAF (Storage Access Framework).
4. Verify that the custom pack is active.
5. Click "Reset to default" in Settings.
6. Verify that the custom pack URI is cleared and the application reloads the default bundled pathology and course datasets.
