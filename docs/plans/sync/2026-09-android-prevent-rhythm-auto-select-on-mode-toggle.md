# Plan: Prevent Rhythm Auto-Selection on Mode Toggle or Search Filtering (Android Parity)

**Created:** 2026-09-19  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

### Problem
When the user is on the Teaching ("Обучение") or Monitor screen with an active rhythm (e.g., rhythm `11280` / academic rhythm) and opens the rhythm drawer to enable Clinical Cases mode ("Включить режим клинических случаев") or types a search query:
- **Expected result:** The list in the drawer filters to show Clinical Cases (or search matches), but the active rhythm currently playing on the monitor **does not change** until the user explicitly taps a rhythm row in the list.
- **Actual result (before fix):** Because rhythm `11280` is an academic rhythm and not in the clinical cases list, the drawer filter logic automatically re-assigned `selectedId` to the first clinical case (`11188`) and triggered a rhythm change event on the monitor without any user tap.

### Fix Implemented on Windows
In `CardioSimulator.App.Controls.RhythmChoosingPanel.xaml.cs`:
- Removed the automatic selection re-assignment and `RhythmSelected` invocation block in `RebuildInternal()`.
- Defaulted `AutoSelectOnFilter` to `false`.
- Filtering the list (switching Academic vs. Clinical mode or searching in the `SearchBox`) now only filters the displayed drawer rows. The monitor's active rhythm is preserved and only changes upon an explicit row tap.

---

## 2. Part A: Update `RhythmSelector.kt` on Android

**File:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\ui\panels\RhythmSelector.kt`

### Instructions
1. Locate lines 201–205 in `RhythmSelector.kt`:
   ```kotlin
   LaunchedEffect(isClinicalMode, filtered) {
       if (isClinicalMode && filtered.isNotEmpty() && (selectedId == null || filtered.none { it.id == selectedId })) {
           onRhythmSelect(filtered.first())
       }
   }
   ```
2. Remove this `LaunchedEffect` (or remove the `onRhythmSelect(filtered.first())` auto-trigger).
3. Ensure that switching `isClinicalMode` or updating `filtered` only updates the visible list rows without calling `onRhythmSelect(...)`.
4. Rhythm selection should only be triggered by the user's explicit row click in the list:
   ```kotlin
   onRhythmSelect(item)
   ```

---

## 3. Part B: Verification Plan

### Manual Verification Flow
1. Launch the Android app and navigate to the **Teaching ("Обучение")** screen.
2. Verify that an initial rhythm (e.g. rhythm `11280` / Sinus rhythm) is active on the monitor.
3. Open the left rhythm drawer and tap the **Clinical Cases ("Клинические случаи")** mode toggle.
4. **Verify:** 
   - The drawer list filters and displays clinical cases.
   - The ECG monitor **continues playing rhythm 11280** (does NOT change to `11188`).
5. Tap on a clinical case in the list (e.g. `11188`).
6. **Verify:** The monitor switches to rhythm `11188`.
7. Toggle back to **Academic Rhythms ("Академические ритмы")**.
8. **Verify:** The monitor continues playing rhythm `11188` until an academic rhythm row is explicitly clicked.
