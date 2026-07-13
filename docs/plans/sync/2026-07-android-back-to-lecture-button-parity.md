# Plan: Port Back to Lecture Button Behavior to Android

**Created:** 2026-07-12  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

When a user (e.g. teacher or student) navigates from a course lecture to **Testing** (self-assessment) or **Examination** (exam), they need a convenient way to go back to the lecture they were viewing.
Currently, when transitioning back to Teaching mode from another mode, the app resets the course filter to "All rhythms" (the monitor) by default.
We need to:
1. Add a `preserveCourseSelection` flag to the main AppViewModel.
2. Intercept the mode switch in the shell layout (`MainScreen.kt`), avoiding resetting the course selection if `preserveCourseSelection` is true.
3. Add a "Back to lecture" button to `TestingScreen.kt` (visible on the test picker and test results screens).
4. Add a "Back to lecture" button to the top tab/header area of `ExaminationScreen.kt` (visible at the top, showing a confirmation prompt if clicked while taking an exam).

---

## 2. Part A: Localization

Add the `back_to_lecture` resource string to the Android locale files under `app/src/main/res/`:

### `values/strings.xml` (English)
```xml
<string name="back_to_lecture">Back to lecture</string>
```

### `values-ru/strings.xml` (Russian)
```xml
<string name="back_to_lecture">Вернуться в лекцию</string>
```

### `values-zh/strings.xml` (Chinese)
```xml
<string name="back_to_lecture">返回课件</string>
```

### `values-es/strings.xml` (Spanish)
```xml
<string name="back_to_lecture">Volver a la lección</string>
```

### `values-hi/strings.xml` (Hindi)
```xml
<string name="back_to_lecture">लेक्चर पर वापस जाएं</string>
```

---

## 3. Part B: ViewModel & Navigation Logic

### `AppViewModel.kt`
- Add a new boolean state variable `preserveCourseSelection` (e.g., using Compose state or LiveData, depending on project style):
```kotlin
var preserveCourseSelection by mutableStateOf(false)
    private set

fun setPreserveCourseSelection(value: Boolean) {
    preserveCourseSelection = value
}
```

### `MainScreen.kt`
- Locate the code handling the transition to `OperatingMode.Teaching`. It currently resets the selected course:
```kotlin
// Before:
if (newMode == OperatingMode.Teaching && lastMode != OperatingMode.Teaching) {
    appViewModel.selectCourse(null)
}
```
- Modify this to respect the `preserveCourseSelection` flag:
```kotlin
if (newMode == OperatingMode.Teaching && lastMode != OperatingMode.Teaching) {
    if (appViewModel.preserveCourseSelection) {
        appViewModel.setPreserveCourseSelection(false) // Reset flag
    } else {
        appViewModel.selectCourse(null)
    }
}
```

---

## 4. Part C: UI Implementation

### `TestingScreen.kt`
- In the test selector/picker screen layout (when no test is active), check if `appViewModel.selectedCourseId != null`. If true, render a "Back to lecture" button (using style matching other primary buttons) below the "Start Test" button.
- In the test results screen layout (when the test is finished), check if `appViewModel.selectedCourseId != null`. If true, render the "Back to lecture" button below or beside the "Restart"/"Choose test" buttons.
- Clicking this button should:
  1. Call `appViewModel.setPreserveCourseSelection(true)`.
  2. Switch the active mode to `OperatingMode.Teaching`.

### `ExaminationScreen.kt`
- Find the top tab/header layout (which shows "Exam" / "Results").
- Add a "Back to lecture" button (using `stringResource(R.string.back_to_lecture)`) aligned to the top-right corner of the header area.
- Make it visible only when `appViewModel.selectedCourseId != null`.
- When clicked:
  - If an individual exam is in progress (`viewModel.isTakingExam`), show a confirmation dialog (e.g., "Abort exam?"). If the user confirms:
    1. Call `appViewModel.setPreserveCourseSelection(true)`.
    2. Switch the active mode to `OperatingMode.Teaching`.
  - If no exam is in progress, perform the transition immediately.

---

## 5. Verification

### Manual Parity Verification Flow
1. Run the Android app, enter Teaching mode, select a course and choose a lecture.
2. Click "Take Test" to switch to the Testing screen.
3. Verify that the "Back to lecture" button is visible. Click it, and verify that it returns you back to the active lecture.
4. Click "Take Test" again, start the test, and complete it.
5. Verify that the "Back to lecture" button is visible on the results page. Click it, and verify it returns to the lecture.
6. Verify the same behavior for the Examination screen tabs header.
7. Start an individual exam, click the "Back to lecture" button, and verify that it prompts you to confirm aborting the exam.
