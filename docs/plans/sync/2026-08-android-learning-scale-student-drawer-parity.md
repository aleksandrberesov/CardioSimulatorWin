# Plan: Port Learning Scale Student Drawer & Header Title Parity to Android

**Created:** 2026-08-16  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the Windows app (`CardioSimulator.Win`), the Learning Scale dashboard previously hosted a `ComboBox` student selector inside the header user chip. This has now been refactored for improved usability and UI clarity:
1. The student dropdown (`ComboBox`) has been removed from the header user chip.
2. The user chip now displays a clean, static student title block showing the selected student's full name (and group) or "Все студенты" (all cohort aggregate).
3. A collapsible left side drawer (`_drawerPanelHost` + `_drawerHandle`) has been introduced. The drawer contains a scrollable list of registered students plus an "All students" aggregate option. Selecting any student item from the drawer re-derives mastery analytics for that student, updates the dashboard, and refreshes the header title.

This plan details the changes required to bring identical UI structure and selection behavior to the Android Compose version of `LearningScaleScreen.kt`.

---

## 2. Part A: Header User Chip Modification

### Target File:
`app/src/main/java/com/example/cardiosimulator/ui/screens/LearningScaleScreen.kt`

### Implementation Steps:
1. Remove any `DropdownMenu` or `ExposedDropdownMenuBox` attached to the user chip in `HeaderSection` / `UserChip`.
2. Replace the interactive picker inside the user chip with a simple text `Column`:
   - Primary Text: Selected student's `fullName` if a student is picked; `AppStrings.LsStudentAll` ("Все студенты") if `selectedStudent == null`; or demo user name if roster is empty.
   - Secondary Text: Student's `group` (if non-null/non-empty).
3. Preserve the avatar circle showing student initial or `👥` icon for cohort view.

---

## 3. Part B: Collapsible Left Student Drawer

### Target File:
`app/src/main/java/com/example/cardiosimulator/ui/screens/LearningScaleScreen.kt`

### Implementation Steps:
1. Wrap the screen layout in a horizontal container (e.g. `Row` or Jetpack Compose `ModalNavigationDrawer` / custom inline `AnimatedVisibility` drawer row).
2. Create a left drawer panel:
   - **Width:** ~260.dp.
   - **Header:** "👥 Студенты" section title.
   - **Item List (`LazyColumn`):**
     - First item: "Все студенты" (`selectedStudent == null`).
     - Subsequent items: `roster` items from `LearningScaleViewModel`.
     - Active item styling: subtle accent background (`SoftGreen`), green left border/indicator, and bold typography.
     - On item click: call `viewModel.selectStudent(student)`.
3. Create a side toggle handle attached to the right of the drawer:
   - Handle width: ~24.dp, height: ~64.dp, rounded right corners.
   - Icon: Chevron icon (`ChevronRight` / `ChevronLeft` or `KeyboardArrowRight` / `KeyboardArrowLeft`).
   - Clicking handle toggles drawer expansion state (`isDrawerOpen`).

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open the app on Android and navigate to the **Learning Scale** screen.
2. Verify that the header chip shows a static student name title without a dropdown menu.
3. Tap the left drawer handle to open the student drawer.
4. Verify all registered students and "Все студенты" option are listed.
5. Select a student from the drawer:
   - Confirm the dashboard mastery data and adaptive plan update.
   - Confirm the header chip updates to display the selected student's name.
6. Toggle dark and light theme to ensure drawer background and handle adapt smoothly.
