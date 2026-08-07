# Plan: Port All Leads Overlay Dark Theme Styling to Android

**Created:** 2026-08-07  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

In dark mode, the "Show All Leads" 12-lead overview overlay (`BuildAllLeadsOverlay` in `ConstructorScreen.cs`) previously rendered hardcoded light background colors (`#FAFAFA` root grid and `WhiteSmoke` top bar), causing default text elements (`_allLeadsTitle`) to render white text on a white background.

In Windows, `ConstructorScreen.cs` was updated to use `AppTheme.AppPageBackground` (`#1C1C1E` in dark mode) for `_allLeadsOverlay.Background`, `AppTheme.PanelBackground` for `_allLeadsTopBar.Background`, and `AppTheme.TextPrimary` for `_allLeadsTitle.Foreground`, and re-applied theme tokens in `OnThemeChanged()`.

This plan details the Android Compose/State equivalents to ensure the 12-lead overview dialog/overlay adapts dynamically to light/dark themes.

---

## 2. Part A: All-Leads Overview Overlay Theme Styling (`AllLeadsOverlay.kt`)

- **Matching Kotlin Files**:
  - `app/src/main/java/com/example/cardiosimulator/ui/AllLeadsOverlay.kt` (or 12-lead overview composable)

- **Porting Steps**:
  1. Replace hardcoded light surface fills (`Color.White` / `#FAFAFA`) with `MaterialTheme.colorScheme.background` / `AppTheme.AppPageBackground`.
  2. Use `MaterialTheme.colorScheme.surface` / `AppTheme.PanelBackground` for the top header bar container.
  3. Ensure overlay title text uses `MaterialTheme.colorScheme.onSurface` / `AppTheme.TextPrimary`.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Open the ECG constructor screen in Dark mode.
2. Tap the "Show All Leads" overview button on the toolbar.
3. Verify that the 12-lead overview overlay displays a dark background (`#1C1C1E`) and that the overlay title text renders in high-contrast white.
