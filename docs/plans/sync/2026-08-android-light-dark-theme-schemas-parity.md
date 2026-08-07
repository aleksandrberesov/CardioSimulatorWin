# Plan: Port Light/Dark Mode Theme Schemas Behavior to Android

**Created:** 2026-08-07  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

The Windows application was updated to support dynamic, full-surface Light and Dark mode theme schemas using `NeoCryBaby` (`E:\VLN_Project\NeoCryBaby\win`) as a design and architectural template:
- `App.xaml` now defines theme-dependent palette tokens inside `<ResourceDictionary.ThemeDictionaries>` with `Light` and `Dark` keys.
- `AppTheme.cs` provides static helpers (`AppTheme.Current`, `AppTheme.IsDark`, `AppTheme.Set()`, `AppTheme.Changed` event) and theme-dependent color/brush accessors (`PageBackground`, `PanelBackground`, `ControlFill`, `ControlBorder`, `Hairline`, `HoverFill`, `TextPrimary`, `TextSecondary`, `AccentTint`).
- All XAML surfaces use dynamic `{ThemeResource}` bindings for automatic runtime theme switching.

The goal on Android is to maintain design parity by updating Jetpack Compose color schemes and theme tokens in `Theme.kt` / `Color.kt`.

---

## 2. Part A: Theme Scheme Definitions (`Theme.kt` / `Color.kt`)

- Update `LightColorScheme` and `DarkColorScheme` in Compose to mirror the Windows design palette tokens:
  - **Light Theme**:
    - `pageBackground`: `#FFE8EAF4`
    - `panelBackground`: `#FFFFFFFF`
    - `controlFill`: `#FFEFF1F7`
    - `controlBorder`: `#FFE0E4EC`
    - `hairline`: `#FFE2E5EE`
    - `hoverFill`: `#14808080`
    - `textPrimary`: `#FF1B2430`
    - `textSecondary`: `#FF5A6B82`
    - `accentTint`: `#FFDCF1E6`
  - **Dark Theme**:
    - `pageBackground`: `#FF000000`
    - `panelBackground`: `#FF1C1C1E`
    - `controlFill`: `#FF2C2C2E`
    - `controlBorder`: `#FF38383A`
    - `hairline`: `#FF2C2C2E`
    - `hoverFill`: `#28FFFFFF`
    - `textPrimary`: `#FFFFFFFF`
    - `textSecondary`: `#FF8E8E93`
    - `accentTint`: `#FF1E3B2B`
  - **Brand & Accent (Theme-Invariant)**:
    - `accent`: `#FF33A06A`
    - `positive`: `#FF2E9E5B`
    - `negative`: `#FFCC3A3A`

---

## 3. Part B: App State & Theme Toggle Integration

- Ensure `AppViewModel.kt` or `ThemeState` exposes a reactive state (`isDarkTheme`) updated when user changes theme settings.
- Pass `isDarkTheme` into the top-level `CardioSimulatorTheme` Compose wrapper to instantly update all screens and dialogs.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Launch Android app in Android Studio emulator or physical device.
2. Open Settings screen and toggle between Light and Dark mode.
3. Verify page background, card surfaces, text primary/secondary colors, borders, and control fills transition smoothly between Light and Dark palettes without requiring app restart.
