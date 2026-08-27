# Plan: Port Default Window Width & Layout Fitting Behavior to Android

**Created:** 2026-08-26  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the Windows version (`CardioSimulator\Win`), the default initial window size was previously set to 1200x850, which caused the bottom control panel (`BottomControlPanel` / `MonitorControlPanel`) containing up to 19 control tabs/buttons (lead count, scheme, sweep speed, gain, scale, filters, electrodes, 3D heart, pQRSt, EOS, calipers, compare, settings, etc.) to squeeze or overflow horizontally on standard displays.

The default initial window size was updated to `1440x900` in `src/CardioSimulator.App/MainWindow.xaml.cs`, ensuring all bottom panel controls fit comfortably on initial startup.

For Android (tablet / desktop / DeX windowing modes), we need to ensure that the bottom control panel scales properly and has sufficient horizontal layout space or scrollable container fallback when displayed in narrower window dimensions.

---

## 2. Part A: Main Window & Bottom Panel Layout Configuration

- **Reference (Windows):** [MainWindow.xaml.cs](file:///e:/VLN_Project/CardioSimulator/Win/src/CardioSimulator.App/MainWindow.xaml.cs#L48)
  - `AppWindow.Resize(new SizeInt32(1440, 900));`
- **Target (Android):** `app/src/main/java/com/example/cardiosimulator/` (Activity configuration / Compose layout)
  - Ensure Jetpack Compose bottom bar containers or Activity initial window width constraints in multi-window / desktop mode default to appropriate width (e.g. 1440dp or scrollable row fallback) so all controls render cleanly without clipping.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Launch the CardioSimulator app on Android tablet / emulator in freeform or landscape mode.
2. Verify that all tabs in the bottom control panel (Lead Count, Scheme, Speed, Gain, Scale, Filters, Electrodes, 3D Heart, pQRSt, EOS, Caliper, Compare, Settings) are visible and fully accessible without clipping.
