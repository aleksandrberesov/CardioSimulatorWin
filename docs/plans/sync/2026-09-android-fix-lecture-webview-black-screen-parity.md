# Plan: Port Lecture WebView Black Screen & Reload Fix to Android

**Created:** 2026-09-19  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

When selecting a lecture in Teaching mode ("Обучение") or re-entering Teaching mode after navigating to another screen (e.g. ECG Constructor), a black screen was displayed instead of rendering the lecture or showing the loading indicator.

### Root Cause Analysis (Windows):
1. **Untriggered Render on Bind/Refresh:** When `CourseViewerPanel` bound to an existing `CourseViewerViewModel` (which persists across screen transitions on `AppViewModel`), `_viewer.LectureContent` was already non-null. However, `Bind()` and `Refresh()` only subscribed to `PropertyChanged` and did not invoke `_web.SetLecture(...)`. Since `_web` (a native `WebView2` control) was freshly constructed without HTML, setting its visibility to `Visible` resulted in a blank/black native surface.
2. **Suppressed Property Changed Notifications on Re-selection:** In `CourseViewerViewModel`, `LectureContent` is a C# `record`. When re-selecting the current lecture or picking a lecture that evaluated as equal via record value comparison (`Equals(old, new) == true`), CommunityToolkit.Mvvm's `[ObservableProperty]` suppressed the `PropertyChanged` event for `LectureContent`. As a result, `_web.SetLecture(...)` was never called, keeping the black screen.

### Fix Implemented (Windows):
1. Created `LoadCurrentLecture()` in `CourseViewerPanel.cs` and invoked it in `Bind()`, `Refresh()`, `OnViewerChanged`, and `OnThemeChanged` so `_web.SetLecture(...)` is explicitly called whenever `LectureContent` is present.
2. Added `OnPropertyChanged(nameof(LectureContent))` in `CourseViewerViewModel.SelectLecture(...)` to guarantee that choosing a lecture always emits a notification even if record equality is `true`.

---

## 2. Part A: CourseViewerViewModel State Emission & Re-open Support

**Target File:** `ui/viewmodels/CourseViewerViewModel.kt`

### Instructions:
1. Ensure `loadLecture(courseId, lectureId)` forces a state update or re-evaluates `_lecture` even when `_lecture.value` matches the previous `Lecture` instance.
2. If `_lecture.value` is identical, emit `_lecture.value = null` momentarily before updating to the loaded `Lecture`, or expose a `reload()` function that forces the `StateFlow` collectors (e.g. in `TeachingScreen`) to re-trigger the Compose `LectureWebView` renderer.

```kotlin
fun selectLecture(lectureId: String) {
    val courseId = _selectedCourseId.value ?: return
    _selectedLectureId.value = lectureId
    viewModelScope.launch { prefs?.setLastLectureId(mode.name, lectureId) }
    // Force reload so collectors re-trigger even if re-selecting identical lecture content
    _lecture.value = null
    loadLecture(courseId, lectureId)
}
```

---

## 3. Part B: TeachingScreen & LectureWebView Component Binding

**Target File:** `ui/screens/TeachingScreen.kt` and `ui/components/LectureWebView.kt`

### Instructions:
1. In `TeachingScreen.kt`, ensure that when `LectureWebView` is composed or re-attached (such as returning to `TeachingScreen` from another screen), it receives the current `lecture` state immediately upon composition.
2. Verify `LectureWebView` displays the loading indicator (`CircularProgressIndicator` / progress ring) while parsing HTML / KaTeX / inline ECG figures before setting `WebView` visibility to `VISIBLE`.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open the Android application.
2. Navigate to Education ("Обучение") screen.
3. Select a course and choose a lecture.
4. **Verification 1:** Confirm the loading progress bar is displayed while the lecture initializes, followed immediately by the formatted lecture content (no black screen).
5. Navigate away to another screen (e.g., ECG Constructor).
6. Return to Education ("Обучение") screen.
7. Select a lecture from the dropdown (or re-select the open lecture).
8. **Verification 2:** Confirm the loading progress indicator appears and the lecture re-loads cleanly without a black screen.
