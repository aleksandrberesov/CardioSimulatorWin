# Plan: Port Welcome Overlay Theme Support to Android

**Created:** 2026-09-20  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the Windows app (`CardioSimulator.App`), the onboarding `WelcomeOverlay` (`WelcomeOverlay.cs`) is a theme-invariant branded overlay matching other chrome elements (such as the header bar and bedside monitor).

Previously, `Background` was set only on the `UserControl` instance instead of its root `Grid` container. When rendered while the window host was in Light Mode, the root container had a transparent background allowing the window's light background to bleed through under white text, producing white-on-white invisible text.

The control was updated so that:
- `RequestedTheme = ElementTheme.Dark` is explicitly assigned to both the control and its root `Grid` container.
- The dark branded ECG background gradient (`#0B1E2B` → `#103344` → `#164A52`) is painted directly on the root `Grid.Background`.
- The welcome overlay consistently maintains its branded dark theme (dark background, white text `#FFFFFF`, cyan accent `#5CE1C8`, green button `#18C4A6`) in **both Light and Dark app schemas**.

This plan documents how to ensure theme-invariant dark branded styling on the Android onboarding welcome component.

---

## 2. Part A: Welcome Screen / Dialog UI Theme Adaptation

Identify the corresponding welcome dialog/screen component in Android Compose or View layer:
- Target File: `app/src/main/java/com/example/cardiosimulator/ui/components/WelcomeOverlay.kt` (or equivalent onboarding screen/dialog).

### Modifications:
1. Ensure the welcome screen uses fixed branded dark colors regardless of system Light/Dark theme:
   - **Background:** Opaque linear gradient brush from `Color(0xFF0B1E2B)` to `Color(0xFF164A52)` on the root container.
   - **Title & Body Text:** `Color.White` / `Color(0xDDFFFFFF)`.
   - **Accent Elements (Heart Icon, Checkmarks, PQRST Trace Path):** `Color(0xFF5CE1C8)`.
   - **Tagline:** `Color(0xFF9FF0E2)`.
   - **Start Button:** Background `Color(0xFF18C4A6)`, text `Color(0xFF061A16)`.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Launch the Android application on first run or clear app data to trigger the welcome screen.
2. Toggle system theme between Light and Dark mode while on the welcome screen.
3. Verify that:
   - In **both Light and Dark Modes**, the welcome overlay displays the exact same opaque dark branded ECG gradient background.
   - All text remains white and clearly legible with zero background bleed-through.
