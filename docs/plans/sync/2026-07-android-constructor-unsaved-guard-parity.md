# Plan: Port Constructor "unsaved-edit guard + follow-into-correct-list" to Android

**Created:** 2026-07-13
**Status:** ACTIVE — NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`

---

## 1. Background & Goals

### The bug (Windows, now fixed)
In the ECG Constructor, changing a pathology's **clinical case** (or name/group) moved it out of the currently-shown rhythm list. The list did not follow it, and — worse on Windows — the picker auto-selected a *different* pathology and called `SelectPathology`, silently discarding all unsaved edits.

### The fix that shipped on Windows (5 files, App layer only)
1. **Never let list filtering reassign the Constructor's selection.** New `RhythmChoosingPanel.AutoSelectOnFilter` (default `true`; the old "auto-select first match on filter" behaviour). The Constructor sets it `false`, so filtering can never switch the pathology being edited.
2. **Follow the pathology into its correct list.** New settable `RhythmChoosingPanel.ClinicalMode` (mirrors the header clinical toggle). On every edited-pathology change the Constructor sets the drawer's clinical/rhythm filter to match the pathology's clinical status: giving it a clinical case moves it into the clinical-cases list (still selected + visible); clearing it moves it back. The in-memory (unsaved) title/group/clinicalCase are already patched into the list so the move reflects immediately.
3. **Prompt before losing edits.** `ConstructorViewModel.HasUnsavedChanges` + `DiscardChanges()`. Selecting a *different* pathology with unsaved edits shows a Save / Don't save / Cancel dialog; Cancel keeps the current pathology and restores the drawer highlight. Leaving the Constructor (mode switch) prompts the same way via a registered leave-guard.

### Desired Android outcome
- Give the edited pathology a clinical case → the rhythm drawer switches to the **clinical-cases list** and shows the pathology there, selected, with its clinical dashboard. Clearing it moves it back to the rhythm list. It is never lost, never silently swapped for another pathology.
- Picking a different pathology, or leaving the Constructor, with unsaved edits → a Save / Don't save / Cancel prompt.

### Android starting point (already in place — verify, don't rebuild)
- `ConstructorScreen.kt` already patches the list with the edited pathology's in-memory metadata (`editedRhythms`, ~lines 522–534) — the equivalent of Windows `RefreshRhythmListNames`. **Keep it.**
- `RhythmSelector.kt` uses a **global** `AppViewModel.isClinicalMode` / `setClinicalMode(...)` shared across all rhythm selectors (unlike Windows' per-panel flag). This is the knob Part A uses.
- `RhythmSelector.kt` auto-selects the first match **only in clinical mode** (`LaunchedEffect(isClinicalMode, filtered)`, ~lines 200–204) — the Android analogue of `AutoSelectOnFilter`.
- `ConstructorViewModel` already has `dirtyLeads`, `isMetadataDirty`, `save()`, `selectPathology(id)` (re-reads from disk and clears dirty — so it doubles as "discard"). No `hasUnsavedChanges`/`discardChanges` helpers yet.
- There is **no** leave-guard / `requestOperatingMode` infrastructure on Android yet (`AppViewModel.updateOperatingMode` is called directly). The Course-Constructor unsaved guard is also still pending, so Part C builds shared infra.

---

## 2. Part A — Follow the edited pathology into its correct list  *(priority; this is the reported bug)*

**File:** `ui/screens/ConstructorScreen.kt`

Add a `LaunchedEffect` that flips the global clinical mode to match the edited pathology's clinical status whenever the edited pathology (or its clinical case) changes:

```kotlin
// Keep the rhythm drawer's clinical/rhythm filter following the edited pathology so it always
// appears in its correct list — giving it a clinical case moves it into the clinical-cases list
// (still selected + visible), clearing it moves it back. Mirrors Windows ConstructorScreen.
LaunchedEffect(targetFile?.id, targetFile?.clinicalCase) {
    val f = targetFile
    if (f != null) appViewModel.setClinicalMode(!f.clinicalCase.isNullOrBlank())
}
```

Notes / correctness:
- `editedRhythms` already reflects the unsaved `clinicalCase`, and `filtered` inside `RhythmSelector` is keyed on the passed `rhythms` + `isClinicalMode`, so after the flip the pathology is present in the clinical list → the clinical-mode auto-select (`filtered.none { it.id == selectedId }`) is **false** and does **not** fire. No silent switch, no loop.
- Keying on `targetFile?.clinicalCase` (value, not identity) means a manual clinical-toggle to *browse* does not re-trigger the flip (it doesn't change `targetFile`), so browsing is preserved until the user picks/edits a pathology — matching Windows.
- `setClinicalMode` is global on Android (affects the Teaching/Monitor drawers' shared state too). That is the existing Android design for the clinical toggle; flipping it from the Constructor is the parity behaviour. Acceptable divergence from Windows' per-panel flag — call it out in the PR description.

**Do not** add a per-panel `AutoSelectOnFilter` to `RhythmSelector`: Android's auto-select is already gated to clinical mode only, and Part A guarantees the selected item is present in the clinical list, so it never reassigns the Constructor's selection. (If a future change makes rhythm mode auto-select too, revisit.)

---

## 3. Part B — Prompt when selecting a *different* pathology with unsaved edits

**File:** `ui/screens/ConstructorScreen.kt`

### 3.1 ViewModel helpers (optional but recommended) — `ui/viewmodels/ConstructorViewModel.kt`
```kotlin
val hasUnsavedChanges: Boolean
    get() = _dirtyLeads.value.isNotEmpty() || _isMetadataDirty.value

/** Throw away unsaved edits by re-reading the current pathology from disk. */
fun discardChanges() {
    val id = _targetFile.value?.id ?: return
    selectPathology(id)  // re-reads, clears dirtyLeads/isMetadataDirty/undo
}
```
(The screen can also compute `dirtyLeads.isNotEmpty() || isMetadataDirty` inline from the already-collected state; the helpers exist mainly for Part C.)

### 3.2 Guard the drawer selection
Replace the drawer wiring (currently `onRhythmSelect = { constructorViewModel.selectPathology(it.id) }`, ~line 545) with an intercept:

```kotlin
var pendingSwitchId by remember { mutableStateOf<String?>(null) }
...
onRhythmSelect = { entry ->
    if (entry.id != targetFile?.id && (dirtyLeads.isNotEmpty() || isMetadataDirty)) {
        pendingSwitchId = entry.id            // ask first
    } else {
        constructorViewModel.selectPathology(entry.id)
    }
},
```

### 3.3 The dialog (see shared composable in Part C)
```kotlin
if (pendingSwitchId != null) {
    UnsavedChangesDialog(
        onSave    = { constructorViewModel.save()                                 // captures current file; safe to switch immediately
                      constructorViewModel.selectPathology(pendingSwitchId!!); pendingSwitchId = null },
        onDiscard = { constructorViewModel.selectPathology(pendingSwitchId!!); pendingSwitchId = null },
        onCancel  = { pendingSwitchId = null },
    )
}
```
`save()` snapshots `_targetFile.value` into its coroutine before writing, so switching immediately after does not corrupt the save. `selectPathology` is what discards (re-read).

---

## 4. Part C — Prompt when leaving the Constructor (mode switch) + shared dialog

Android has no leave-guard yet, so build a minimal, reusable one (the pending **Course Constructor** unsaved guard should reuse it — implement once).

### 4.1 Shared dialog composable — new file `ui/components/UnsavedChangesDialog.kt`
```kotlin
@Composable
fun UnsavedChangesDialog(onSave: () -> Unit, onDiscard: () -> Unit, onCancel: () -> Unit) {
    AlertDialog(
        onDismissRequest = onCancel,
        title = { Text(stringResource(R.string.unsaved_changes_title)) },
        text  = { Text(stringResource(R.string.unsaved_changes_body)) },
        confirmButton = { TextButton(onClick = onSave)    { Text(stringResource(R.string.common_save)) } },
        dismissButton = {
            Row {
                TextButton(onClick = onDiscard) { Text(stringResource(R.string.common_dont_save)) }
                TextButton(onClick = onCancel)  { Text(stringResource(R.string.common_cancel)) }
            }
        },
    )
}
```

### 4.2 Leave-guard infra — `ui/viewmodels/AppViewModel.kt`
Add a `pendingMode` + a registerable guard, and route mode switches through a request function:
```kotlin
private val _pendingMode = MutableStateFlow<OperatingModeModel?>(null)
val pendingMode: StateFlow<OperatingModeModel?> = _pendingMode.asStateFlow()

/** The active screen registers this to veto/deferred-confirm leaving it (null = no guard). */
var leaveGuard: (() -> Boolean)? = null   // returns true = OK to leave now

fun requestOperatingMode(mode: OperatingModeModel) {
    if (mode.id == _selectedOperatingMode.value.id) return
    if (leaveGuard?.invoke() == false) { _pendingMode.value = mode; return }  // screen will confirm
    updateOperatingMode(mode)
}
fun confirmPendingMode()  { _pendingMode.value?.let { updateOperatingMode(it) }; _pendingMode.value = null }
fun cancelPendingMode()   { _pendingMode.value = null }
```
Point the mode-switch call sites at `requestOperatingMode` instead of `updateOperatingMode`:
- `ui/panels/TopControlPanel.kt:99`
- `ui/screens/TeachingScreen.kt:583` and `:593`

### 4.3 Register the guard + confirm dialog in the Constructor — `ui/screens/ConstructorScreen.kt`
```kotlin
// Report unsaved state to the leave-guard; clear it on dispose.
DisposableEffect(Unit) {
    appViewModel.leaveGuard = { !(dirtyLeadsState.value.isNotEmpty() || metadataDirtyState.value) }
    onDispose { appViewModel.leaveGuard = null }
}
val pendingMode by appViewModel.pendingMode.collectAsState()
if (pendingMode != null) {
    UnsavedChangesDialog(
        onSave    = { constructorViewModel.save(); appViewModel.confirmPendingMode() },
        onDiscard = { constructorViewModel.discardChanges(); appViewModel.confirmPendingMode() },
        onCancel  = { appViewModel.cancelPendingMode() },
    )
}
```
(Use the ViewModel `StateFlow`s directly inside the guard lambda so it reads live values, not a captured snapshot.)

> If building the full leave-guard infra is deferred, still ship Parts A + B — they fix the reported bug and the in-screen switch, and are self-contained. Mark Part C as follow-up shared with the Course-Ctor guard.

### 4.4 String resources — add to `values`, `values-ru`, `values-zh`, `values-es`, `values-hi`
None of these exist yet on Android (the Course-Ctor guard never landed). Add:
- `unsaved_changes_title` — EN "Unsaved changes" · RU "Несохранённые изменения" · ZH "未保存的更改" · ES "Cambios sin guardar" · HI "असहेजे बदलाव"
- `unsaved_changes_body` — EN "You have unsaved changes. Save them before continuing?" · RU "Есть несохранённые изменения. Сохранить перед продолжением?" · ZH "您有未保存的更改。是否在继续前保存？" · ES "Tiene cambios sin guardar. ¿Guardarlos antes de continuar?" · HI "आपके पास असहेजे बदलाव हैं। जारी रखने से पहले उन्हें सहेजें?"
- `common_save`, `common_dont_save`, `common_cancel` — reuse existing equivalents if present (e.g. `constructor_save`, `constructor_rename_cancel`); otherwise add. EN don't-save = "Don't save".

(These map to the Windows `course_ctor_unsaved_title/body` + `common_*` strings, which the Windows fix reused.)

---

## 5. Part D — Verification

### 5.1 Manual flow (Android emulator/device)
1. Enter **Constructor**. Pick a plain rhythm; edit a lead (a tab turns red — dirty).
2. Tap the clinical (Healing) edit button, fill in a **Title** + fields, save the dialog.
   - **Expect:** the rhythm drawer switches to the **clinical-cases list**, the pathology appears there, still selected (red) with the clinical dashboard; the Save button is visible; edits are intact. It is **not** left in the rhythm list and **not** replaced by another pathology.
3. Open the clinical dialog again, tick **Clear all fields**, save.
   - **Expect:** the pathology moves **back** to the plain-rhythm list, still selected.
4. With unsaved edits, tap a **different** pathology in the drawer.
   - **Expect:** Save / Don't save / Cancel prompt. *Cancel* → stays on current pathology (drawer highlight restored). *Don't save* → switches, edits gone. *Save* → writes, then switches.
5. With unsaved edits, switch the operating mode (top bar).
   - **Expect:** same prompt (once Part C lands). *Cancel* stays in Constructor.
6. Rename / change group with edits present → pathology stays selected, re-labelled / re-sectioned in place (no loss).

### 5.2 Regression checks
- Teaching / Monitor rhythm drawers: selecting rhythms and toggling clinical mode still behave as before (global `isClinicalMode` unchanged in those screens except when the Constructor set it — acceptable).
- No infinite recomposition / flicker when toggling clinical case (watch for the auto-select `LaunchedEffect` firing repeatedly).

---

## 6. File-by-file summary

| Area | Windows (reference) | Android (target) | Action |
|---|---|---|---|
| Follow into correct list | `ConstructorScreen.cs` sets `_drawer.ClinicalMode` on edited-pathology change | `ui/screens/ConstructorScreen.kt` `LaunchedEffect` → `appViewModel.setClinicalMode(...)` | **Add (Part A)** |
| No silent reassign | `RhythmChoosingPanel.AutoSelectOnFilter=false` | `RhythmSelector.kt` auto-select already clinical-mode-only | **Verify only** |
| List reflects unsaved metadata | `RefreshRhythmListNames` | `editedRhythms` (already present) | **Verify only** |
| Dirty/discard helpers | `HasUnsavedChanges`, `DiscardChanges` | `ConstructorViewModel.kt` | **Add (Part B)** |
| Guard on picking another pathology | guarded `RhythmSelected`→`OnRhythmChosen` | `ConstructorScreen.kt` `pendingSwitchId` + dialog | **Add (Part B)** |
| Unsaved dialog | `UnsavedChangesDialog.ConfirmAsync` | new `ui/components/UnsavedChangesDialog.kt` | **Add (Part C)** |
| Leave-guard on mode switch | `AppViewModel.LeaveGuardAsync` + `RequestOperatingModeAsync` | `AppViewModel.requestOperatingMode` + `pendingMode`; retarget call sites | **Add (Part C, shared w/ Course-Ctor guard)** |
| Strings | reused `course_ctor_unsaved_*` + `common_*` | new `unsaved_changes_*` + `common_*` ×5 locales | **Add (Part C)** |
