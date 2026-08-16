# Plan: Port Students Screen Learning Scale Button & Direct Navigation to Android

**Created:** 2026-08-15  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

In the Windows version (`CardioSimulatorWin`), each item in the registered Students roster screen (`StudentsScreen.cs`) now has a "View Learning Scale" (📈) button next to the edit/delete actions. Clicking this button sets the target student as `PendingLearningScaleStudent` in `AppViewModel` and navigates directly to `LearningScale` mode. Upon initializing `LearningScaleViewModel`, if an initial student is specified, the dashboard opens pre-focused on that student's mastery data.

This plan details how to mirror this functionality in the Android codebase (`CardioSimulator`).

---

## 2. Part A: AppViewModel Navigation State

- **Target File:** `AppViewModel.kt`
- Add a field or `StateFlow` to hold a pending student selection for Learning Scale navigation:
  ```kotlin
  var pendingLearningScaleStudent: Student? = null
  ```
- When navigating to `OperatingMode.LearningScale`, check and consume `pendingLearningScaleStudent` to initialize `LearningScaleViewModel` with `initialStudent = pendingLearningScaleStudent`.

---

## 3. Part B: Students Screen Item Action

- **Target File:** `StudentsScreen.kt` (or `StudentRegistrationScreen.kt`)
- In each student list item row, add an action icon button (📈 / `Icons.Default.BarChart` or localized tooltip "Learning scale"):
  ```kotlin
  IconButton(onClick = {
      appViewModel.pendingLearningScaleStudent = student
      appViewModel.selectOperatingMode(OperatingMode.LearningScale)
  }) {
      Icon(
          imageVector = Icons.Default.BarChart,
          contentDescription = stringResource(R.string.students_learning_scale)
      )
  }
  ```

---

## 4. Part C: LearningScaleViewModel Selection Logic

- **Target File:** `LearningScaleViewModel.kt`
- Update `LearningScaleViewModel` constructor to accept an optional `initialStudent: Student? = null`:
  ```kotlin
  class LearningScaleViewModel(
      course: Course?,
      language: Language,
      roster: List<Student>,
      masteryFor: (Student?) -> MasteryReport,
      initialStudent: Student? = null
  ) {
      init {
          val selected = if (initialStudent != null) {
              roster.firstOrNull { it.id == initialStudent.id || (it.fullName == initialStudent.fullName && it.group == initialStudent.group) } ?: initialStudent
          } else {
              roster.firstOrNull()
          }
          selectStudent(selected)
      }
  }
  ```

---

## 5. Part D: Verification

### 5.1 Manual Verification Flow
1. Open the app on Android and navigate to the **Students** («Регистрация студентов») screen.
2. Register a new student if none exist.
3. Locate the new **Learning Scale** (📈) button in the action column of a student row.
4. Tap the button.
5. Verify that the app transitions to the **Learning Scale** («Шкала обучения») screen and the dropdown selector pre-selects the clicked student with their corresponding mastery stats loaded.
