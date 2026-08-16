# Plan: Port Learning Scale Full-Width Layout Alignment to Android

**Created:** 2026-08-16  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the **Learning Scale («Шкала обучения»)** dashboard screen, main page containers previously had a maximum width constraint (`MaxWidth = 1440`), and item text titles had artificial `MaxWidth` caps (e.g., `320px`, `300px`, `260px`). All main layout components (Header, Global Progress, Sections Map, Adaptive Plan, Progress Histogram, and Footer) have been updated to stretch to **100% full width** across the viewport, allowing child text titles to expand naturally across larger screens.

---

## 2. Part A: Android Compose Screen Full-Width Layout Alignment

**Target:** `app/src/main/java/com/example/cardiosimulator/ui/screens/LearningScaleScreen.kt`

1. **Main Column Container**: Ensure the top-level scrollable column uses `fillMaxWidth()` without any max-width constraints (`Modifier.fillMaxWidth()`).
2. **Main Dashboard Cards**:
   - Header container: `Modifier.fillMaxWidth()`.
   - Global progress card: `Modifier.fillMaxWidth()`.
   - Main two-column grid: `Modifier.fillMaxWidth()`.
   - Progress histogram card: `Modifier.fillMaxWidth()`.
   - Footer container: `Modifier.fillMaxWidth()`.
3. **Item Title Width Constraints**:
   - Ensure section title texts, subtopic label texts, and plan task title texts do not have hardcoded maximum width limits (`Modifier.fillMaxWidth()` or unbounded weight), allowing them to utilize full horizontal space.

---

## 3. Verification

### 3.1 Manual Verification Flow
1. Launch the Android app and open the **Learning Scale («Шкала обучения»)** dashboard screen on tablets or desktop/windowed view.
2. Verify visually that all main cards (Header, Global Progress, Sections Map, Adaptive Plan, Histogram, Footer) span the full available width of the screen.
