# Plan: Port Equal Size Rhythm Panel Header Buttons to Android

**Created:** 2026-08-23  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the Windows version (`RhythmChoosingPanel.xaml`), the top control panel icon buttons (Academic Mode Toggle, Clinical Mode Toggle, Group/Sort Toggle, Expand All, Collapse All, and Pin Toggle) previously relied on content padding without explicit uniform dimensions. This resulted in slight variations in button sizes due to font metrics and glyph bounding boxes. Additionally, `SortIcon` previously specified `FontFamily="Segoe UI Symbol"` which caused MDL2 list/sort glyphs (`0xE8FD` / `0xE8CB`) to render as missing glyph rectangles.

To ensure visual consistency and a uniform toolbar appearance:
- Explicit dimensions (`Width="32"`, `Height="32"`, `Padding="0"`) were applied to all top control panel icon buttons in `RhythmChoosingPanel.xaml`.
- `SortIcon`'s font family was set to `Segoe MDL2 Assets` so that `0xE8FD` and `0xE8CB` render properly.
- `ExpandAllButton` and `CollapseAllButton` font families were set to `Segoe UI Emoji` for U+1F4C2 and U+1F4C1 folder emojis.

This plan details how to ensure identical uniform button dimensions and correct icon/vector drawables for icon buttons in the Android `RhythmSelector` component (`RhythmSelector.kt`).

---

## 2. Part A: Android RhythmSelector Top Controls Sizing & Icons

In `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\ui\panels\RhythmSelector.kt`:
- Ensure top action `IconButton` and `FilledIconButton` instances have explicit fixed size modifiers `Modifier.size(32.dp)` (or standard `32.dp` square layout).
- Verify vector icon drawables (`Icons.Default.Sort`, `Icons.Default.ViewList`, `Icons.Default.AddBox`, `Icons.Default.IndeterminateCheckBox`) are properly bound and rendered.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Open the Android application in an emulator or device.
2. Open the Rhythm Selector / Choosing Panel drawer.
3. Observe the top header toolbar icon buttons (Expand All, Collapse All, Sort/Group Toggle, Mode Toggles, Pin).
4. Verify that all icon buttons render with equal width and height across the top toolbar and all icons/vectors render properly without missing glyph symbols.
