# Plan: Port Assemble ECG Test Constructor Layout Fix to Android

**Created:** 2026-08-07  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the Test Constructor when authoring an "Assemble ECG" («Собери ЭКГ») question type, selecting a rhythm with a long title (e.g., "Предсердная тахикардия и миграция водителя ритма") caused the rhythm picker control to expand horizontally and push adjacent controls ("Lead" and "Number of Parts" / "Частей:") past the container boundary, resulting in the part count selection dropdown being visually clipped.

### Fix Applied on Windows
1. **RhythmPickerButton Width Cap & Tooltip**: Set `MaxWidth = 320` on `sourcePicker` in `TestConstructorScreen.cs` and added full text tooltip on button hover in `RhythmPickerButton.cs`.
2. **Dedicated Parameter Row**: Re-organized `BuildAssembleEditor` so the Source Rhythm dropdown occupies its own row (`row1`), while the Lead selection and Part Count selection (`partsCombo`) occupy a separate parameters row (`row2`). This prevents long rhythm names from crowding or clipping the parts selector combo box regardless of screen resolution or font scale.

---

## 2. Part A: Test Constructor Compose UI / Layout Structure on Android

- Locate the Assemble ECG question editor screen / composables in `app/src/main/java/com/example/cardiosimulator/`.
- Ensure the rhythm selector composable is constrained with `modifier.widthIn(max = 320.dp)` or wrapped appropriately.
- Ensure the Lead and Part Count selection dropdowns are laid out cleanly on a secondary row or responsive grid row so they remain fully visible and non-clipped.

---

## 3. Part B: Tooltip / Ellipsis Support

- Ensure long rhythm names display character ellipsis (`Ellipsis`) and support tooltip / tap to view full title when truncated.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open Test Constructor -> Question Bank -> New Question.
2. Select question type "Assemble ECG" ("Собрать ЭКГ").
3. Select rhythm "Предсердная тахикардия и миграция водителя ритма".
4. Verify that:
   - The rhythm picker truncates cleanly with ellipsis and does not overflow the card.
   - The "Number of parts" ("Количество частей") dropdown is displayed on the parameters row cleanly and without any clipping.
