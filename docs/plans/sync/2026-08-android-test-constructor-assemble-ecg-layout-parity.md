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
1. **RhythmPickerButton Width Cap & Tooltip**: Set `MaxWidth = 320` (with `HorizontalAlignment.Left`) on `sourcePicker` in `TestConstructorScreen.cs` and added full text tooltip on button hover in `RhythmPickerButton.cs`. Left-align + max-width caps it on wide panes and lets it shrink/ellipsize on narrow ones.
2. **One-control-per-row form grid** (supersedes the earlier single "parameters row" approach): `BuildAssembleEditor` now lays the parameters out as a 2-column `Grid` (labels column `Auto`, controls column `Star`) with **each control on its own row** — Rhythm (row 0), Lead (row 1), Parts (row 2). Because the control column is star-sized, every field shrinks with the editor pane instead of overflowing it. The earlier fix put Lead + Parts side-by-side on a single horizontal `StackPanel` (~318 DIP) that never wraps; in the ~40%-wide editor pane that row overflowed on high-DPI / non-maximized layouts and the rightmost control (the Parts combo) was clipped by the non-horizontally-scrolling `ScrollViewer`. Giving Parts its own row makes it impossible to clip regardless of pane width or display scaling.

---

## 2. Part A: Test Constructor Compose UI / Layout Structure on Android

- Locate the Assemble ECG question editor screen / composables in `app/src/main/java/com/example/cardiosimulator/`.
- Ensure the rhythm selector composable is constrained with `modifier.widthIn(max = 320.dp)` or wrapped appropriately.
- Lay the parameters out one control per row (Rhythm, then Lead, then Parts) — e.g. a `Column` of label+control `Row`s, each control `Modifier.weight(1f)`/`fillMaxWidth` inside its row — so every field shrinks with the pane and the Part Count dropdown can never be pushed off-screen. Do **not** place Lead and Part Count side-by-side on one non-wrapping row (that is the layout that caused the clip on Windows).

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
