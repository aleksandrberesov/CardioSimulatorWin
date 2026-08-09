# Plan: Port Same-Path Package Reload Parity to Android

**Created:** 2026-08-08  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

When a user reloaded a course package or pathology dataset from the exact same file path/location with the same name (e.g. overwriting `course.pak` or `course.zip` at `C:\path\course.pak`), the application failed to update the active dataset and retained the old content. Loading a package from a new location/name worked properly.

### Root Causes Identified & Fixed on Windows:
1. **Overlay Key Collision (`AppPaths.PackKey`)**:
   `PackKey` computed the key for the writable overlay based only on file path (for fallback/non-pak files) or pack identity without file modification timestamps/sizes. Overwriting/replacing a package at the same path caused `OverlayCourseSource` / `OverlayPathologySource` to reopen the existing overlay containing stale deltas and tombstones.
   *Fix:* Incorporated `LastWriteTimeUtc` and file `Length` into `PackKey` so that modified/replaced files at the same path generate a fresh overlay key and clean state.
2. **ViewModel Event Notification (`ReopenAfterCourseReload`)**:
   When re-opening content after reload, `SelectedCourseId` did not raise `PropertyChanged` if the string ID was unchanged.
   *Fix:* Explicitly raise `PropertyChanged` for `SelectedCourseId` and `SelectedCoursePathologies` to force UI selectors, teaching rhythm filters, and drawers to refresh.
3. **`LectureWebView` Render Cache**:
   WebView2 rendering skipped re-navigating if the rendered HTML string equalled `_currentHtml`.
   *Fix:* Added `ClearCache()` / cache invalidation when reloading package content so web views always force a re-render.

---

## 2. Part A: Overlay Key & Storage Cache Parity (Android)

- In Android data/storage layer (`AppPaths.kt` / `DataSourcePrefs.kt` / overlay storage):
  - Ensure overlay file keys for user-picked packages (`courses-{key}.pak` / `pathologies-{key}.pak`) include the file modification timestamp (`lastModified()`) and file length.
  - This ensures that updating a package file on disk at the same URI/path creates a clean overlay for the new version rather than reusing stale deltas.

---

## 3. Part B: ViewModel & UI Re-binding Parity (Android)

- In `AppViewModel.kt` / `CourseViewModel.kt` / Compose UI:
  - When reloading or replacing a course package, force notification of `selectedCourseId` and `selectedCoursePathologies` state updates even if the course ID string is identical.
  - Trigger refresh of `RhythmViewModel` filters and drawer selectors upon manifest reload.
  - In `LectureWebView` (or Compose WebView wrapper), invalidate the cached HTML string on course reload so the WebView re-renders.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Load a course package `course.pak` or `course.zip` in the app.
2. Update/overwrite the package file on disk at the same file location with updated lectures or courses.
3. Select "Change Folder" / "Change ZIP" and pick `course.pak` from the same location.
4. Verify that the app updates its dataset, displays the new lectures/courses, and refreshes the monitor rhythm filter immediately.
