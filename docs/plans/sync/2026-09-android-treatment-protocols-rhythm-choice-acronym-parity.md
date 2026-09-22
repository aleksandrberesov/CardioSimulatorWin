# Plan: Port Treatment Protocol Rhythm Choice & Acronym Architecture to Android

**Created:** 2026-09-22  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In previous versions of the Treatment Protocols editor («Протоколы лечения»), authored transitions and result outcomes were bound exclusively to an abstract, coarse enum (`ClinicalRhythmState`, e.g., Sinus, VentricularFibrillation, Asystole) or an optional manual text field for pathology ID.

This presented several clinical and UX limitations:
1. **Taxonomy disconnect:** The simulator taxonomy classifies ECGs using clinically standard rhythm acronyms (`SR`, `VFIB`, `AFIB`, `3AVB`, `ASYSTOLE`, `TDP`, `WPW`, `SVT`, etc.), but authors had to pick from a fixed internal state enum.
2. **Abstract vs Real rhythm:** Authors could not easily choose a concrete rhythm from the pathology library (e.g. `№100 100 — Ventricular fibrillation`) or an acronym with instant search suggestions.
3. **Manual typing of titles:** Authors had to manually translate and type the Russian/English rhythm names for table headers and result rows instead of having them automatically populated from the taxonomy or library entry.
4. **Coarse matching priority:** The engine lacked priority resolution when matching incoming ECG rhythms with multiple authored rules (Pathology ID vs Acronym vs coarse ClinicalRhythmState).

### Objectives:
- Provide a unified **Rhythm Choice Control** in the transition edit dialog for both the transition source (From Rhythm) and target results (Result Rhythms).
- Support three modes: **Acronym** (suggested from taxonomy), **Real Rhythm** (suggested from library pathologies), and **Display Only / None** (for transition triggers without simulator binding).
- Support **Auto-fill Title** (`🪄 Заполнить название`), populating Russian/English names automatically.
- Introduce priority matching in the authored treatment engine: **Concrete Pathology ID (specificity 3) > Acronym (specificity 2) > Clinical State fallback (specificity 1)**.
- Propagate `TargetAcronym` on outcomes so the resuscitation panel can resolve the representative pathology dynamically.
- Maintain full backwards compatibility with legacy protocol presets that only have `FromState` / `State`.

---

## 2. Part A: Domain Model & Engine Updates

### Reference Windows files:
- `src/CardioSimulator.Core/Domain/Treatment/TreatmentModel.cs`
- `src/CardioSimulator.Core/Domain/Treatment/AuthoredTreatment.cs`
- `src/CardioSimulator.Core/Domain/Treatment/TreatmentEngine.cs`
- `src/CardioSimulator.Core/Domain/Treatment/TreatmentRhythmMap.cs`

### Kotlin Porting Steps:

1. **Update `TreatmentResult`:**
   Add `targetAcronym: String? = null`:
   ```kotlin
   data class TreatmentResult(
       val newState: ClinicalRhythmState,
       val effectSeconds: Int = 0,
       val warning: TreatmentReason = TreatmentReason.None,
       val blocked: Boolean = false,
       val targetPathologyId: String? = null,
       val targetAcronym: String? = null
   )
   ```

2. **Update `AuthoredTransition` & `AuthoredOutcome`:**
   ```kotlin
   data class AuthoredTransition(
       val from: ClinicalRhythmState,
       val trigger: AuthoredTrigger,
       val drug: TreatmentDrug?,
       val outcomes: List<AuthoredOutcome>,
       val effectSeconds: Double = 0.0,
       val fromPathologyId: String? = null,
       val fromAcronym: String? = null,
       val customDrugId: String? = null
   )

   data class AuthoredOutcome(
       val state: ClinicalRhythmState,
       val weight: Double,
       val targetPathologyId: String? = null,
       val targetAcronym: String? = null
   )
   ```

3. **Priority Matching in `AuthoredTreatmentTable`:**
   In `match(state, action, currentPathologyId, currentAcronyms)`:
   - Rank candidate transitions by specificity:
     - Specificity 3: `t.fromPathologyId != null && t.fromPathologyId.equals(currentPathologyId, ignoreCase = true)`
     - Specificity 2: `t.fromAcronym != null && currentAcronyms.any { it.equals(t.fromAcronym, ignoreCase = true) }`
     - Specificity 1: `t.from == state && t.fromPathologyId == null && t.fromAcronym == null`
     - Specificity 0: No match.
   - Choose the candidate with the highest specificity > 0.
   - When picking an outcome by random draw, populate `targetPathologyId` and `targetAcronym` into the resulting `TreatmentResult`.

4. **Pass Acronyms through `TreatmentEngine`:**
   Add `currentAcronyms: List<String>? = null` parameter to `TreatmentEngine.apply(...)`, threading it into `resolveAuthored(...)` and `table.match(...)`.

---

## 3. Part B: Protocol Data Models & Bridge

### Reference Windows files:
- `src/CardioSimulator.App/Data/TreatmentProtocolModel.cs`
- `src/CardioSimulator.App/Data/TreatmentProtocolBridge.cs`

### Kotlin Porting Steps:

1. **Update `TransitionProtocol` & `ResultItem`:**
   ```kotlin
   data class TransitionProtocol(
       var id: String = UUID.randomUUID().toString(),
       var currentKind: RhythmKind = RhythmKind.Normal,
       var current: LocText = LocText(),
       var fromState: ClinicalRhythmState? = null,
       var fromPathologyId: String? = null,
       var fromAcronym: String? = null,
       var trigger: TransitionTrigger = TransitionTrigger.Drug,
       var triggerDrug: TreatmentDrug? = null,
       var triggerCustomDrugId: String? = null,
       var effectSeconds: Int = 0,
       var actions: MutableList<ActionItem> = mutableListOf(),
       var results: MutableList<ResultItem> = mutableListOf(),
       var time: LocText = LocText(),
       var conditions: LocText = LocText()
   )

   data class ResultItem(
       var kind: RhythmKind = RhythmKind.Normal,
       var text: LocText = LocText(),
       var state: ClinicalRhythmState? = null,
       var targetPathologyId: String? = null,
       var targetAcronym: String? = null,
       var weight: Double = 1.0
   )
   ```

2. **Bridge Grouping & Conversion:**
   In `TreatmentProtocolBridge.buildAuthoredTable(set)`:
   - Group transitions sharing the same source:
     `key = Triple(t.fromState, t.fromPathologyId, t.fromAcronym)`
   - When converting `TransitionProtocol` to `AuthoredTransition`:
     - If `fromState` is null and `fromAcronym` is present, derive `fromState = TreatmentRhythmMap.classifyByAcronyms(listOf(fromAcronym))`.
     - Pass `fromAcronym` and `targetAcronym` to authored domain records.

---

## 4. Part C: UI & Protocol Editor Dialogs

### Reference Windows files:
- `src/CardioSimulator.App/Screens/TreatmentProtocolsScreen.cs`
- `src/CardioSimulator.App/Localization/AppStrings.cs`

### Kotlin / Compose Porting Steps:

1. **Localization Strings:**
   Add localized keys to `AppStrings`:
   - `tp_mode_acronym`: "Акроним" / "Acronym"
   - `tp_mode_real_rhythm`: "Реальный ритм" / "Real rhythm"
   - `tp_mode_display_only`: "Только текст (без симулятора)" / "Display only (no simulator)"
   - `tp_acronym_search_prompt`: "Поиск акронима (VFIB, SR, AFIB...)" / "Search acronym (VFIB, SR, AFIB...)"
   - `tp_real_rhythm_search_prompt`: "Поиск патологии по названию или ID..." / "Search pathology by name or ID..."
   - `tp_autofill_title`: "Заполнить название" / "Auto-fill title"

2. **Rhythm Choice Component (`RhythmChoiceControl` / Compose Composable):**
   Create a reusable composable/controller:
   - Mode selector tabs or dropdown:
     - For Transitions: `[Acronym, Real Rhythm, Display Only]`
     - For Results: `[Acronym, Real Rhythm]`
   - **Acronym mode:** Autocomplete text field querying `Taxonomy.shared.entries` by acronym, Russian name, and English name. Selecting an entry resolves `selectedAcronym` and derives `derivedState = TreatmentRhythmMap.classifyByAcronyms(...)`.
   - **Real Rhythm mode:** Autocomplete text field querying all available pathologies (including synthetic asystole/torsades) by ID, number, title EN, title RU, and acronyms.
   - **Auto-fill Title Button / Callback:** Emits localized `LocText(en, ru)` to populate the row title if requested or blank.
   - **Legacy Preset Migration:** If an existing preset item only has `fromState` or `state` set (with no acronym or pathology ID), initialize the picker in Acronym mode with `TreatmentRhythmMap.acronymsFor(state).firstOrNull()`.

3. **Transition & Result Edit Dialogs:**
   - In `EditTransitionDialog`, replace old state combo and pathology box with `RhythmChoiceControl`. Include the `🪄 Заполнить название` button.
   - In `ResultRowView`, replace state combo and target pathology box with `RhythmChoiceControl`.

---

## 5. Part D: Simulator Runtime Integration

### Reference Windows files:
- `src/CardioSimulator.App/ViewModels/TreatmentViewModel.cs`
- `src/CardioSimulator.App/Screens/TreatmentPanel.cs`

### Kotlin Porting Steps:

1. **Treatment ViewModel Tracking:**
   - In `TreatmentViewModel`, store `var currentAcronyms: List<String> = emptyList()`.
   - Update `seedState(state, pathologyId, acronyms)`.
   - In `scheduleCommit(...)` and `commitState(...)`, record `pendingTargetAcronym: String?`.
   - Update `showRhythm` lambda signature to `(ClinicalRhythmState, targetPathologyId: String?, targetAcronym: String?) -> Unit`.

2. **Treatment Panel Resolution:**
   - In `seedFromCurrentRhythm()`, pass `currentPathology.acronyms` into `viewModel.seedState(...)`.
   - In `showRhythm(state, targetPathologyId, targetAcronym)`:
     - If `targetPathologyId` is specified, resolve that concrete pathology.
     - Else if `targetAcronym` is specified, resolve representative pathology via:
       - Special cases: `"ASYSTOLE"` → `SyntheticAsystole`, `"TDP"` → `SyntheticTorsades`.
       - Standard: `Taxonomy.resolveRepresentativePathologyId(targetAcronym)`.
     - Else, fallback to `TreatmentRhythmMap.representativePathologyId(state)`.

---

## 6. Part E: Verification Plan

### 6.1 Unit Tests (Engine & Matching Priority)
Port unit tests from `tests/CardioSimulator.Core.Tests/AuthoredTreatmentTests.cs`:
- `Authored_Rule_Matches_By_FromAcronym_And_Returns_TargetAcronym`: Verifies rule matches incoming ECG acronyms and outcome returns `TargetAcronym`.
- `Authored_Rule_Priority_Pathology_Beats_Acronym_Beats_State`: Verifies priority specificity order (Pathology ID > Acronym > Coarse State).

### 6.2 Manual Verification Flow on Android
1. **Open Protocol Editor:**
   Navigate to «Протоколы лечения».
2. **Edit Existing Transition:**
   Open any transition (e.g. "Фибрилляция желудочков"). Verify the Rhythm selector opens in **Acronym** mode showing `VFIB — Фибрилляция желудочков`.
3. **Change Source Rhythm:**
   Switch to **Real Rhythm** mode. Type `100` or `пароксизмальная`. Pick a concrete pathology from suggestions. Click `🪄 Заполнить название` and verify the current rhythm title updates.
4. **Edit Result Outcome:**
   In results section, switch result rhythm between Acronym and Real Rhythm. Set target to an acronym (e.g., `AFIB`) and another outcome to a real rhythm (e.g., `#26`).
5. **Simulate in Teaching / Resuscitation:**
   Open Teaching monitor, launch resuscitation panel («Лечение»), apply the action and verify the monitor transitions to the expected pathology/acronym with correct localized header.
