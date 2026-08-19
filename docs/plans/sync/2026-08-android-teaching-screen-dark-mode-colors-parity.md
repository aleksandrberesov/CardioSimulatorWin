# Plan: Port Teaching Screen Dark Mode Color Adjustments to Android

**Created:** 2026-08-19  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the CardioSimulator Windows app, the Teaching screen displayed incorrect colors when dark mode was enabled:
1. In `LectureWebView`, base component CSS (`HtmlComponents.Css`) was loaded after theme styles, causing structural elements (cards, notes, sections, quotes, and figure captions) to render with hardcoded light mode backgrounds and unreadable text. Ordering `ThemeCss` after `HtmlComponents.Css` and supplying explicit dark mode overrides for `.lecture-card`, `.lecture-section-title`, `.lecture-note-*`, `.lecture-quote`, `.lecture-figure figcaption`, and `.lecture-divider` resolved the issue.
2. In `MonitorViewerOverlay`, hardcoded light colors (`#FAFAFA` background, `WhiteSmoke` top bar, hardcoded white floating cards, and gray dividers) were used instead of dynamic `AppTheme` design tokens, and theme change notifications (`AppTheme.Changed`) were unhandled.
3. The floating "About this rhythm" button (`_info`) on the monitor was defaulting to white styling in dark mode, making it almost invisible over light paper grids (Cream, Pink, Blue-Gray). Its styling was updated so that its background, border, and icon foreground derive directly from the **selected grid palette** (`EcgColors.Palette(scheme, blankSheet)`) rather than app theme mode.

This plan outlines porting and verifying dark mode theme parity for lecture web views, HTML component styles, and monitor overlay surfaces (including grid-palette-dependent floating controls) on Android.

---

## 2. Part A: Lecture Web View & HTML Component Styling

- **Target Android Component:** `app/src/main/java/com/example/cardiosimulator/LectureWebView.kt` (or corresponding HTML renderer)
- **Reference Windows Component:** `src/CardioSimulator.App/Controls/LectureWebView.cs`

### Steps:
1. Verify stylesheet order: ensure dark theme CSS overrides apply **after** base `HtmlComponents.Css` rules.
2. Ensure CSS classes for lecture components include explicit dark theme overrides when dark mode is enabled:
   - Cards (`.lecture-card`, `.lecture-card-title`): dark background (`#2C2C2E`), dark border (`#38383A`), white text (`#FFFFFF`).
   - Notes (`.lecture-note`, `.lecture-note-tip`, `.lecture-note-warning`, `.lecture-note-important`): dark tint backgrounds with high-contrast text (`#E2E8F0`).
   - Section titles (`.lecture-section-title`): white text (`#FFFFFF`), dark bottom border (`#38383A`).
   - Quotes & Figcaptions (`.lecture-quote`, `.lecture-figure figcaption`): muted light text (`#94A3B8`).

---

## 3. Part B: Monitor Overlay & Drawer Surfaces

- **Target Android Component:** `RhythmChoosingDrawer.kt` / `TeachingScreen.kt` / `MonitorOverlay` composables
- **Reference Windows Component:** `src/CardioSimulator.App/Controls/MonitorViewerOverlay.cs`

### Steps:
1. Verify Compose / Material Theme token usage across monitor overlay surfaces and floating cards.
2. For floating overlay controls over the ECG canvas (such as the "About this rhythm" info button), ensure colors adapt to the active ECG grid paper palette (`Yellow/Cream`, `Pink`, `BlueGray`, or `BlankSheet`/Scope) rather than general app theme mode:
   - **Background**: Match grid background color with translucency.
   - **Icon & Border**: Match grid trace accent color (e.g. Teal for cream/blue-gray, Near-black for pink, Green for bedside scope).

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Run the Android app on an emulator or test device.
2. Navigate to the Teaching screen in both Light and Dark mode.
3. Open a lecture with cards, notes, sections, and quotes — confirm all text and backgrounds are legible in Dark mode.
4. Open the monitor / rhythm info overlay:
   - Switch between Cream, Pink, Blue-Gray paper grids and Bedside scope mode in both Light and Dark modes.
   - Confirm the "About this rhythm" button remains crisp, high-contrast, and clearly visible against all grid backgrounds.
