# Plan: Port Treatment Dialog Fixes, Protocol Presets, and Custom Drug Enhancements to Android

**Created:** 2026-09-21  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

The Treatment / Resuscitation subsystem («Лечение») allows instructors and students to simulate emergency cardiac care (ACLS/BLS protocols) on top of the shared Teaching monitor. Recent testing and clinical requirements identified two critical bugs and several UX/architectural enhancements:

1. **Bug 1 (Specific Pathology Rule Override):**
   - *Problem:* When authoring an explicit transition rule (e.g., Sinus Rhythm #100 -> Artificial Paced Rhythm #26 on Valsalva maneuver), applying the action converted the rhythm to #11988 (the default representative paced rhythm) instead of specific pathology #26.
   - *Root Cause:* The transition table only checked the coarse `ClinicalRhythmState` enum without preserving specific `FromPathologyId` or returning `TargetPathologyId`.
   - *Fix:* Added `FromPathologyId` to `AuthoredTransition` (with priority over generic state matching) and `TargetPathologyId` to `AuthoredOutcome`, `TreatmentResult`, and `ShowRhythm`.

2. **Bug 2 (Missing Name for Asystole):**
   - *Problem:* Defibrillating Ventricular Fibrillation #45108 into Asystole caused the teaching monitor header to show placeholder "Ритм..." because flatline set `SelectedRhythm = null`.
   - *Fix:* Added canonical synthetic `PathologyEntry.SyntheticAsystole` and `SyntheticTorsades` so flatline and fallback rhythms always retain valid localized titles ("Асистолия" / "Asystole").

3. **Protocol Presets Architecture:**
   - Introduced `TreatmentPresetContainer` and `TreatmentProtocolPreset` allowing multiple named clinical protocols (e.g., "Стандарт приказа № 2345 ДЗМ Москвы", "Индия", custom hospital protocols).
   - Supported selecting the active preset, saving copies as new presets, deleting custom presets, and JSON export/import.
   - Header displays active protocol: `Лечение — <Название протокола>`.

4. **Extensible Custom Drugs Catalog:**
   - Added "+" buttons to IV and Pill cards with flyouts to define custom drugs (name, dose, unit, max dose) stored in `TreatmentProtocolSet.CustomDrugs`.
   - Custom drugs are dynamically injected as selectable pick chips.

5. **Visual Restyle & Real-time ACLS Feedback:**
   - Card redesign: Only the top header strip has the category accent color; the card body uses a clean neutral background.
   - Pick chips: Subtle tint when inactive, full accent fill when selected.
   - CPR Animation: Real-time 30-compression animation loop (1..30) and cycle counter progress bar.
   - Action Journal: Logs "Исходный ритм: <название>" on open, "Ритм изменился на: <название>" upon conversion, and displays a countdown timer + progress bar during pending delayed actions.

---

## 2. Part A: Domain Model Updates

### Reference Windows files:
- `src/CardioSimulator.Core/Domain/Pathology.cs`
- `src/CardioSimulator.Core/Domain/Treatment/TreatmentModel.cs`
- `src/CardioSimulator.Core/Domain/Treatment/AuthoredTreatment.cs`
- `src/CardioSimulator.Core/Domain/Treatment/TreatmentEngine.cs`
- `src/CardioSimulator.Core/Domain/Treatment/TreatmentRhythmMap.cs`
- `src/CardioSimulator.App/Data/TreatmentProtocolModel.cs`
- `src/CardioSimulator.App/Data/TreatmentProtocolStore.cs`

### Kotlin Porting Steps:

1. **Synthetic Asystole and Torsades (`domain/Pathology.kt`):**
   Add companion object properties for synthetic entries when no `.dat` file exists:
   ```kotlin
   data class PathologyEntry(
       val id: String,
       val titleEn: String,
       val nameRu: String?,
       val leadsCount: Int,
       val fileName: String,
       val group: String? = null,
       val clinicalCase: String? = null,
       val number: Int? = null,
       val acronyms: List<String>? = null,
   ) {
       companion object {
           val SyntheticAsystole = PathologyEntry(
               id = "asystole",
               titleEn = "Asystole",
               nameRu = "Асистолия",
               leadsCount = 12,
               fileName = "asystole.dat",
               group = "arrest",
               acronyms = listOf("ASYSTOLE")
           )
           val SyntheticTorsades = PathologyEntry(
               id = "torsades",
               titleEn = "Torsades de pointes",
               nameRu = "Пируэтная тахикардия (Torsades)",
               leadsCount = 12,
               fileName = "torsades.dat",
               group = "arrhythmia",
               acronyms = listOf("TDP")
           )
       }
   }
   ```

2. **Treatment Domain & Transitions (`domain/treatment/`):**
   - In `TreatmentAction.Drug`, add optional `customName: String? = null`.
   - In `TreatmentResult`, add optional `targetPathologyId: String? = null`.
   - In `AuthoredTransition`, add `val fromPathologyId: String? = null`.
   - In `AuthoredOutcome`, add `val targetPathologyId: String? = null`.
   - Update `AuthoredTreatmentTable.match(state, action, currentPathologyId)`:
     Prioritize matching rules where `fromPathologyId == currentPathologyId` over generic `fromState == state`.

3. **Protocol Presets Storage (`data/treatment/`):**
   - Define `CustomDrugItem(id, name, isIv, defaultDoseMg, unit, maxDoseMg)`.
   - Define `TreatmentProtocolPreset(id, name, description, isBuiltIn, protocolSet)`.
   - Define `TreatmentPresetContainer(activePresetId, presets)`.
   - In `TreatmentProtocolStore`, update `loadContainer()` to handle both legacy single-set JSON and the new preset container schema, preserving backward compatibility.

---

## 3. Part B: Treatment ViewModel & Monitor Synchronization

### Reference Windows files:
- `src/CardioSimulator.App/ViewModels/TreatmentViewModel.cs`
- `src/CardioSimulator.App/ViewModels/RhythmViewModel.cs`

### Kotlin Porting Steps:

1. **Rhythm Selection & Flatline:**
   - In `RhythmViewModel.kt`, when switching to flatline/asystole (`showFlatline()`), set `selectedRhythm = PathologyEntry.SyntheticAsystole` instead of `null`.
   - For Torsades fallback (`showTorsades()`), set `selectedRhythm = PathologyEntry.SyntheticTorsades`.

2. **Treatment Execution & Countdown:**
   - In `TreatmentViewModel.kt`:
     - Keep track of `currentPathologyId` when seeding state from `selectedRhythm`.
     - Log initial rhythm: `"Исходный ритм: $initialTitle"`.
     - Upon successful rhythm conversion, log: `"Ритм изменился на: $newTitle"`.
     - For delayed actions (e.g. amiodarone, adrenaline), track remaining seconds and total seconds with a 100ms ticker, allowing the UI to render a countdown text and progress bar.
     - Pass `targetPathologyId` to `showRhythm(newState, targetPathologyId)`.
     - In `showRhythm`, if `targetPathologyId != null`, find and select that specific pathology directly on the monitor.

---

## 4. Part C: Treatment Panel UI (Compose / Android)

### Reference Windows file:
- `src/CardioSimulator.App/Screens/TreatmentPanel.cs`

### Kotlin Porting Steps:

1. **Header:**
   - Display active protocol title in header bar: `«Лечение — ${activePreset.name}»`.
   - Add preset switcher dropdown or quick-action dialog.

2. **Card Visuals:**
   - Style cards with top accent header strip:
     - IV Drugs: Green (`#2EA04A`)
     - Defibrillator: Red (`#E03B30`)
     - Pills: Blue (`#1E6FE0`)
     - Pacing: Orange (`#E88A00`)
     - Vagal / Airway: Cyan (`#2EA6C7`) / Yellow (`#E8C000`)
   - Use neutral dark container surface (`#232326` / `AppColors.Surface`) for card body with subtle outline.

3. **Custom Drugs ("+" button):**
   - Add "+" icon button in the header of IV Drugs and Pills cards.
   - Display dialog to input drug name, default dose, and unit.
   - On confirm, append to `activePreset.protocolSet.customDrugs` and refresh pick chips.

4. **ACLS CPR Animation:**
   - When CPR toggle switch is turned ON:
     - Run a continuous compression counter timer (100–120 bpm, ~550ms per compression).
     - Display `СЛР: компрессия X/30 (цикл Y)` with animated progress bar (0..30).
     - Reset and increment cycle count after 30 compressions.

5. **Journal & Status Area:**
   - Render pending effect timer countdown and progress bar.
   - Show color-coded log entries (Actions in neutral text, Warnings/Blocks in amber/red).

---

## 5. Verification Plan

### 5.1 Issue 1 Verification:
1. Open Treatment Protocols screen (`Протоколы лечения`).
2. Add a rule:
   - Initial rhythm: Sinus Rhythm 100 (`fromPathologyId = "100"`).
   - Trigger: Vagal Maneuver (Valsalva).
   - Result: Artificial Paced Rhythm 26 (`targetPathologyId = "26"`).
3. Open Teaching screen and select Sinus Rhythm 100.
4. Open Treatment panel and tap "Проба Вальсальвы" (Valsalva).
5. **Verify:** The monitor switches specifically to rhythm 26 (and not default 11988).

### 5.2 Issue 2 Verification:
1. Open Teaching screen and select Ventricular Fibrillation 45108.
2. Open Treatment panel and apply Defibrillation (200 J).
3. If it converts to Asystole, verify that the monitor and panel title clearly display "Асистолия" / "Asystole" (and never "Ритм...").

### 5.3 Preset and Custom Drug Verification:
1. In Treatment panel, tap "+" on IV card and add "Кордарон экспресс" 150 мг.
2. Verify that the chip appears under IV drugs, can be selected, and given with dose 150 мг.
3. Switch presets and verify protocol transitions change accordingly.
4. Turn on CPR toggle switch and verify the 1..30 compression counter and cycle bar animate smoothly.
