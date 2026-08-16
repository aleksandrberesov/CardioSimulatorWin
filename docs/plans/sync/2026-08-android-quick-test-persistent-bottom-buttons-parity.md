# Plan: Port Quick Test Launcher Persistent Extra Bottom Element & Full-Width Layout to Android

**Created:** 2026-08-15  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

When selecting an individual test or exam in `QuickTestScreen` (used for Testing, Examination, and post-lecture Quick Tests), the available ready tests list can contain many items.
Previously, the Continue/Start and Back action buttons were rendered at the very end of the scrollable container. Consequently, when the test list exceeded screen height, the action buttons were pushed off-screen. Additionally, the test launcher card had a fixed maximum width constraint (`MaxWidth = 820`).

To improve usability and presentation, `QuickTestScreen` was updated to:
1. **Full-Width Layout**: Expand the launcher container (`HorizontalAlignment.Stretch`) across the entire screen width instead of capping width at 820px.
2. **Top Section (Fixed)**: Header, mode choices (Ready / Generate), theme selector, and ready test count/filters.
3. **Middle Section (Scrollable)**: A `ScrollViewer` wrapping ONLY the test items list (or generator options).
4. **Extra Bottom Element (Fixed & Persistent)**: A dedicated bottom bar containing a separator hairline, action buttons (Continue / Back), and optional footer notes.

This plan details porting this full-width persistent bottom element layout to the Android Jetpack Compose / View UI equivalent.

---

## 2. Part A: QuickTestLauncher Layout Restructuring (Jetpack Compose / Android UI)

- **Target Component:** `QuickTestScreen.kt` or `QuickTestLauncher.kt` under `app/src/main/java/com/example/cardiosimulator/`
- **Layout Change:**
  - Wrap the test selection card in a `Column` with `fillMaxWidth()` and `fillMaxHeight()`.
  - Place header, topic info, action mode selection, and test list header in a fixed top `Column`.
  - Place the test items list inside a scrollable container (`LazyColumn` or `Column(modifier = Modifier.weight(1f).verticalScroll(rememberScrollState()))`).
  - Place the action buttons (Start/Continue and Back) in a persistent bottom bar (`Surface` / `Row` anchored at the bottom of the card/screen, spanning full width).

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Launch the Android app and navigate to **Individual Testing** or **Individual Examination**.
2. Pick **Ready Tests**.
3. Verify that the launcher card stretches full-width across the screen.
4. Verify that when the ready tests list contains multiple items, scrolling only scrolls the test items.
5. Verify that the Continue ("Начать") and Back ("Назад") buttons stay visible and fixed at the bottom at all times.
