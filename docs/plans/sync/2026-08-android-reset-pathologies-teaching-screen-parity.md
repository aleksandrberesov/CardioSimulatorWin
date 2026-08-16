# Plan: Port Reset Pathologies & Teaching Screen Sync Behavior to Android

**Created:** 2026-08-15  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

When resetting pathologies to default in the Settings dialog on Windows:
1. **Overlay persistence**: Resetting the dataset path (`Prefs.TreeUri = null`) did not delete the existing overlay file (`AppPaths.PathologyOverlayPak`). Any user deletions/tombstones in the overlay file remained applied on top of the default bundle, leaving the pathology manifest empty.
2. **Background thread UI dispatching**: When reloading the manifest off the UI thread (via `Task.Run`), `PathologyRepository.ManifestChanged` fired on a ThreadPool thread. In `RhythmViewModel.LoadManifestAsync()`, updating `Rhythms` on a background thread resulted in cross-thread UI update failures on XAML controls (such as `RhythmChoosingPanel`), leaving the Teaching screen's rhythm drawer empty.
3. **Monitor Canvas Disposal on Dialog Unload**: `EcgMonitorControl` called `_canvas.RemoveFromVisualTree()` inside its `Unloaded` event handler. When a XAML dialog/overlay (like Settings) opened, `Unloaded` fired on `EcgMonitorControl`, permanently destroying the Win2D `CanvasControl` rendering surface. Consequently, selecting a pathology after closing Settings invalidated a destroyed canvas and rendered an empty monitor until navigating away to create a fresh screen instance.

### Fix Summary (Windows):
- Removed `_canvas.RemoveFromVisualTree()` from `Unloaded` handlers in `EcgMonitorControl`, `EditableLeadControl`, and `PreviewPaneControl`. Added `Loaded` handlers to invalidate the canvas and resume rendering when restored to the visual tree.
- Added explicit deletion of `AppPaths.PathologyOverlayPak` when `ResetDataFolderToDefaultAsync()` is called (and `AppPaths.CourseOverlayPak` in `ResetCourseFolderToDefaultAsync()`).
- Added `SelectCourse(null)` on reset to clear active course filters so all default rhythms are shown.
- Updated `RhythmViewModel` to capture `DispatcherQueue` and ensure manifest reloading, filtering (`ApplyFilter()`), `_allRhythms` assignment, and selection restoration are strictly executed on the UI thread (`RunOnUi(...)`).
- Enhanced `SelectRhythm` in `RhythmViewModel` to clear `Waveforms` and metadata when no rhythm is selected, and explicitly raise property changed notifications for `SelectedRhythm` and `Waveforms`.

---

## 2. Part A: Canvas / View Lifecycle & Surface Preservation

### Android Target:
- `app/src/main/java/com/example/cardiosimulator/ui/MonitorView.kt` (or SurfaceView / Canvas / Compose components)

### Instructions:
1. Ensure that modal dialogs, bottom sheets, or popups opening over the monitor view do not permanently tear down or destroy the underlying rendering surface.
2. When the view is re-attached / resumed (`onStart` / `onResume` or Compose `DisposableEffect`), invalidate the canvas and request a redraw to immediately display the current rhythm waveform.

---

## 3. Part B: Pathology Dataset Reset & Main Thread Sync

### Android Target:
- `app/src/main/java/com/example/cardiosimulator/viewmodels/AppViewModel.kt`
- `app/src/main/java/com/example/cardiosimulator/viewmodels/RhythmViewModel.kt`

### Instructions:
1. Clear active course filters (`selectCourse(null)`) and delete overlay files (`pathologies_overlay.pak`) on dataset reset.
2. Ensure manifest updates and waveform state assignments occur on `Dispatchers.Main`.
3. Force a waveform reload and state notification when a pathology is selected to immediately update the monitor.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow:
1. Open the app on Android.
2. Open Settings and reset pathologies to default.
3. Close Settings (without navigating away to other screens).
4. Select any pathology from the left rhythm drawer on the Teaching screen.
5. Verify that:
   - The rhythm list displays all default pathologies.
   - The ECG monitor immediately draws the 12-lead waveforms on screen (monitor is NOT blank/empty).
