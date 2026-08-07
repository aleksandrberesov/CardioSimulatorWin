# Plan: Port ECG Constructor Lead Selector Tabs Dark Theme Styling to Android

**Created:** 2026-08-07  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

In dark mode, the lead selection buttons in the ECG constructor (`ConstructorScreen.cs`) rendered black symbols (`Colors.Black`) on top of dark button backgrounds, rendering lead names unreadable.

In Windows, `ConstructorScreen.cs` was updated to use dynamic `AppTheme.TextPrimary` (or `AppTheme.Negative` for dirty leads) instead of hardcoded `Colors.Black`, and subscribed to `AppTheme.Changed` on `Loaded` (unsubscribing on `Unloaded`).

This plan details the Android Compose/State equivalents to ensure the lead selection tab strip dynamically adapts to light/dark themes.

---

## 2. Part A: Lead Tab Button Color Styling (`ConstructorScreen.kt` / `LeadTabStrip.kt`)

- **Matching Kotlin Files**:
  - `app/src/main/java/com/example/cardiosimulator/ui/ConstructorScreen.kt` (or lead selection tab composable)

- **Porting Steps**:
  1. Replace hardcoded black button text color (`Color.Black`) with `MaterialTheme.colorScheme.onSurface` / `AppTheme.TextPrimary`.
  2. Maintain `MaterialTheme.colorScheme.error` / `AppTheme.Negative` for modified/dirty lead indicators.
  3. Ensure lead selector tabs re-evaluate text colors when switching between light and dark themes.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Open the ECG constructor screen in Dark mode.
2. Observe the lead tab bar at the top of the constructor canvas.
3. Verify that all lead symbols (I, II, III, aVR, aVL, aVF, V1-V6) display crisp, high-contrast white text in Dark mode and dark text in Light mode.
