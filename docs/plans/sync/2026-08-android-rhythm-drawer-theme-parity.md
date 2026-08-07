# Plan: Port Rhythm Drawer Container Dark Theme Styling to Android

**Created:** 2026-08-07  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

In dark mode, the left rhythm drawer container in Windows (`RhythmChoosingDrawer.cs`) previously retained hardcoded light background colors (`WhiteSmoke` / `Gainsboro`), causing rhythm list item titles (`#FFFFFF` / `#8E8E93`) to render as low-contrast white/gray text on a white background.

In Windows, `RhythmChoosingDrawer.cs` was updated to use `AppTheme.PanelBackground` (`#1C1C1E` in dark mode), `AppTheme.ControlFill`, `AppTheme.ControlBorder`, and `AppTheme.TextPrimary`, and to subscribe to `AppTheme.Changed` on `Loaded` (unsubscribing on `Unloaded`).

This plan details the Android Compose/State equivalents to ensure the rhythm drawer background and side-handle container dynamically adapt to light/dark themes.

---

## 2. Part A: Rhythm Drawer Container Styling (`RhythmChoosingDrawer.kt`)

- **Matching Kotlin Files**:
  - `app/src/main/java/com/example/cardiosimulator/ui/RhythmChoosingDrawer.kt` (or Compose side drawer)

- **Porting Steps**:
  1. Replace hardcoded `Color.White` / `Color.LightGray` drawer surface fills with `MaterialTheme.colorScheme.surface` or `AppTheme.PanelBackground`.
  2. Ensure the toggle handle background uses `MaterialTheme.colorScheme.surfaceVariant` / `AppTheme.ControlFill`.
  3. Ensure rotated drawer title label observes `MaterialTheme.colorScheme.onSurface` / `AppTheme.TextPrimary`.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Open the Android application and switch to Dark mode in Settings.
2. Open the rhythm selector drawer on the left side of the screen.
3. Verify that the drawer surface is dark (`#1C1C1E`) and that all rhythm titles and category headers display crisp, high-contrast white and gray text.
