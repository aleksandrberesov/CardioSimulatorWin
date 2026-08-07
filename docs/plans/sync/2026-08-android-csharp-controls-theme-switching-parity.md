# Plan: Port C# Controls Dynamic Theme Switching to Android

**Created:** 2026-08-07  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

During theme switching between Light and Dark modes in Windows (`CardioSimulatorWin`), custom C#-built controls (`Tab`, `RhythmChoosingPanel`, `ToolModePanelControl`, `CourseSelectorDrawer`, `CourseViewerPanel`, `TestQuestionPanel`, `ExamQuestionPanel`, `OskeFormEditor`, `SignificantPointPanel`, `SegmentRangeCanvas`) initially retained stale theme colors because their background, border, and text properties evaluated theme brushes once on construction without subscribing to dynamic theme change events.

In Windows, controls were updated to subscribe to `AppTheme.Changed` on `Loaded` (and unsubscribe on `Unloaded`) to re-trigger `ApplyVisualState()`, `Rebuild()`, or `Render()`.

This sync plan details the Android Compose/State equivalents to ensure all custom buttons, icon buttons, list items, sidebars, and drawer containers automatically react to dynamic theme state changes.

---

## 2. Part A: Custom Control Theme Reaction (`Tab`, `RhythmChoosingPanel`, `ToolModePanel`)

- **Matching Kotlin Files**:
  - `app/src/main/java/com/example/cardiosimulator/ui/Tab.kt` (or Compose equivalents)
  - `app/src/main/java/com/example/cardiosimulator/ui/RhythmChoosingPanel.kt`
  - `app/src/main/java/com/example/cardiosimulator/ui/ToolModePanel.kt`

- **Porting Steps**:
  1. Ensure all custom composables consume `AppTheme.colorScheme` or `isSystemInDarkTheme()` / `AppThemeViewModel.isDark` state within their recomposition scope.
  2. For list items in `RhythmChoosingPanel`, ensure `RhythmItem` row text color evaluates dark/light text primary color dynamically rather than storing a static `Color` instance.

---

## 3. Part B: Drawers & Question Panels Theme Synchronization

- **Matching Kotlin Files**:
  - `app/src/main/java/com/example/cardiosimulator/ui/CourseSelectorDrawer.kt`
  - `app/src/main/java/com/example/cardiosimulator/ui/TestQuestionPanel.kt`
  - `app/src/main/java/com/example/cardiosimulator/ui/ExamQuestionPanel.kt`

- **Porting Steps**:
  1. Update drawer container background and handle border brushes to observe active `AppTheme` colors.
  2. Ensure question panels and answer options re-evaluate card background, border, and text colors when `isDark` state changes.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open the Android application in an emulator or device.
2. Open Settings and toggle between Light and Dark mode.
3. Verify that all tabs, icon sidebar buttons, rhythm list items, drawers, and question panel options instantly update their colors to match the selected theme.
