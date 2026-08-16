# Plan: Port Equal Corner Radius Alignment in Learning Scale Screen to Android

**Created:** 2026-08-16  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the **Learning Scale («Шкала обучения»)** dashboard screen, various cards, containers, buttons, and sub-elements previously had inconsistent corner radius values (ranging from 7px, 8px, 10px, 20px, 30px to 40px). All layout containers, card panels, section list items, task items, slider borders, dialog details, chips, and toast overlays have been updated to use a unified **16dp corner radius** for design parity and visual elegance.

---

## 2. Part A: Android Compose Screen Corner Radius Standardisation

**Target:** `app/src/main/java/com/example/cardiosimulator/ui/screens/LearningScaleScreen.kt`

Ensure all card shapes, container surface shapes, drawer item buttons, task item shapes, and modal borders in `LearningScaleScreen.kt` consistently use `RoundedCornerShape(16.dp)` (or `CornerRadius(16.dp)`):

1. **Global Progress Card**: Set surface/card shape to `RoundedCornerShape(16.dp)`.
2. **Sections Map & Items**:
   - Main card container: `RoundedCornerShape(16.dp)`.
   - Section expander item buttons: `RoundedCornerShape(16.dp)`.
   - Subtopic item buttons: `RoundedCornerShape(16.dp)`.
3. **Adaptive Plan Panel & Tasks**:
   - Main card container: `RoundedCornerShape(16.dp)`.
   - Plan task item cards: `RoundedCornerShape(16.dp)`.
   - Task badges / section chips: `RoundedCornerShape(16.dp)`.
   - Difficulty slider container border: `RoundedCornerShape(16.dp)`.
4. **Header Elements**:
   - User chip wrapper: `RoundedCornerShape(16.dp)`.
   - Level badge wrapper: `RoundedCornerShape(16.dp)`.
5. **Drawer & Toast**:
   - Student drawer item buttons: `RoundedCornerShape(16.dp)`.
   - Toast overlay box: `RoundedCornerShape(16.dp)`.

---

## 3. Verification

### 3.1 Manual Verification Flow
1. Open the Android application and navigate to the **Learning Scale («Шкала обучения»)** mode.
2. Verify visually that all card containers, section rows, subtopic buttons, adaptive plan task items, user chips, badges, and difficulty slider containers have uniform rounded corners (`16.dp`).
