# Plan: Port Clinical-Cases-Mode Icon Fix (flame → patient) to Android

**Created:** 2026-08-11
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goals

**Reported bug (Windows):** In **clinical-cases mode** («Режим клинических случаев»), the toggle button
that opens the clinical-cases list carries an **unclear icon**.

- **Steps:** open the rhythm-selection panel.
- **Expected:** the button's purpose is obvious.
- **Actual:** the icon is meaningless.

### Root cause on Windows

The clinical-cases toggle in the rhythm-selection panel used Segoe MDL2 Assets glyph **`U+ECAD`**. A code
comment (and the earlier sync note `2026-07-android-clinical-case-presentation-mode.md`) called it a
"stethoscope icon" — **but that is wrong**. Segoe MDL2 Assets has no stethoscope, and `U+ECAD` actually
renders as a **flame** 🔥 (verified by rendering the installed font). A flame on a "clinical cases" button
is unrelated to its function — exactly the reported symptom. The button's tooltip
(`clinical_mode_tooltip` = "Clinical cases mode" / "Режим клинических случаев") was already correct; only
the glyph was wrong.

### Fix applied on Windows

Two sites used the flame glyph for the clinical-case concept; both were swapped to **`U+E77B`** — Segoe
MDL2 **"Contact"**, a **person/patient** silhouette — with a comment recording *why*:

- `Win/src/CardioSimulator.App/Controls/RhythmChoosingPanel.xaml` — the `ClinicalToggle`'s
  `<FontIcon x:Name="ClinicalIcon">` glyph `&#xECAD;` → `&#xE77B;`.
- `Win/src/CardioSimulator.App/Screens/ConstructorScreen.cs` — `_clinicalCaseButton`'s
  `FontIcon.Glyph = "\uECAD"` → `"\uE77B"`.

**Why a person and not a heart/vitals icon:** a clinical case here is a rhythm **plus patient context**
(name / age / gender / HR / BP shown in the clinical dashboard). The distinguishing concept vs a plain
rhythm is *the patient*, so a person reads as "patient case". A heart-with-pulse glyph (Segoe MDL2
`U+E95E`) was rejected because in a cardiology/ECG app it risks reading as the ECG/rhythm itself — i.e.
the *other* mode. Nothing else changed; the toggle/edit behaviour and the tooltip are untouched.

**Goal for Android:** the clinical-cases toggle and the constructor's clinical-case button use an icon
whose purpose is obvious and that matches the Windows choice — a **patient/person** — for cross-platform
consistency.

---

## 2. Android — how it differs (read before editing)

**Android is NOT flame-broken.** Android is Jetpack Compose, not Segoe MDL2 glyphs, so there is no
`U+ECAD` here. Both clinical entry points currently use Material **`Icons.…Healing`** — a first-aid /
bandage-cross "healing" glyph. That is at least *medical*, unlike the Windows flame, so **this is a
consistency + clarity change, not a broken-icon bug**. Severity is lower than the Windows fix, but the
same clarity rationale applies: `Healing` (a bandage) does not clearly say "patient clinical case", and it
diverges from the patient icon Windows now shows.

Two target sites (both use `Icons.Default.Healing` for filled/active):

1. **Clinical-mode toggle** — `ui/panels/RhythmSelector.kt`
   - `RhythmSelector.kt:261-269` (the "Clinical mode toggle" `IconButton`):
     ```kotlin
     imageVector = if (isClinicalMode) Icons.Default.Healing else Icons.Outlined.Healing,
     contentDescription = stringResource(R.string.clinical_mode_tooltip),
     tint = if (isClinicalMode) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant
     ```
   - Explicit imports: `RhythmSelector.kt:28` `import androidx.compose.material.icons.filled.Healing`,
     `RhythmSelector.kt:31` `import androidx.compose.material.icons.outlined.Healing`.

2. **Constructor clinical-case button** — `ui/screens/ConstructorScreen.kt`
   - `ConstructorScreen.kt:729-733` (the `IconButton(onClick = { showClinicalDialog = true })`):
     ```kotlin
     imageVector = Icons.Default.Healing,
     contentDescription = stringResource(R.string.clinical_edit_tooltip)
     ```
   - Imports: `ConstructorScreen.kt:16` `import androidx.compose.material.icons.filled.*` (wildcard —
     covers `Icons.Default.Person` with no new import); `ConstructorScreen.kt:17`
     `import androidx.compose.material.icons.outlined.Healing` (appears **unused** — the constructor only
     uses the filled `Healing` — verify and remove if so).

The `contentDescription`s (`clinical_mode_tooltip`, `clinical_edit_tooltip`) already exist in
`values`, `values-ru`, `values-es`, `values-hi`, `values-zh` — **no new strings needed**.

---

## 3. Android fix

Replace `Icons.…Healing` with the Material **`Person`** icon (the direct analog of Windows `U+E77B`
"Contact") in both sites. `Person` is in the Material Icons *core* set, so no extended-icons dependency is
required.

### 3.1 `RhythmSelector.kt` (clinical-mode toggle)

- Change line 266 to:
  ```kotlin
  imageVector = if (isClinicalMode) Icons.Filled.Person else Icons.Outlined.Person,
  ```
  (`Icons.Default.Person` is the same as `Icons.Filled.Person`.) Keep `contentDescription`, `tint`, and
  the filled/outlined active-state pattern exactly as they are.
- Update imports: replace the two `Healing` imports (lines 28, 31) with
  ```kotlin
  import androidx.compose.material.icons.filled.Person
  import androidx.compose.material.icons.outlined.Person
  ```
  Confirm `Healing` is not used elsewhere in the file before removing (grep shows the toggle is its only
  use).

### 3.2 `ConstructorScreen.kt` (clinical-case editor button)

- Change line 731 to:
  ```kotlin
  imageVector = Icons.Default.Person,
  ```
  Keep `contentDescription = stringResource(R.string.clinical_edit_tooltip)`. No new import — the
  `filled.*` wildcard at line 16 already covers `Person`.
- Remove the now-unused `import androidx.compose.material.icons.outlined.Healing` (line 17) **only after**
  confirming nothing else in the file references outlined `Healing`.

### 3.3 Comment parity

Add a short comment at each site (mirroring the Windows comment) so the icon choice isn't "corrected"
back to something medical-but-vague: e.g. `// Person = "patient clinical case"; matches the Windows
U+E77B Contact glyph. Not a heart/pulse — that reads as the ECG/rhythm (the other mode).`

### Do NOT

- Do **not** keep `Healing` "because it's already medical" — the point of the parity change is that both
  platforms show the *same* patient icon and apply the same rationale.
- Do **not** substitute a heart/`MonitorHeart`/pulse icon — that reproduces the confusion Windows
  deliberately avoided (it reads as the rhythm/ECG, i.e. the opposite mode).

---

## 4. Verification

### 4.1 Manual flow

1. **Toggle:** open the rhythm-selection panel (teaching drawer / picker). The header clinical-mode
   toggle shows a **person** icon; its long-press/`contentDescription` still reads "Clinical cases mode" /
   «Режим клинических случаев». Tapping it filters the list to clinical cases (and shows the clinical
   dashboard for a selected case) — behaviour unchanged.
2. **Constructor:** open a pathology in the constructor; the clinical-case toolbar button shows the same
   **person** icon and still opens the `ClinicalCaseDialog`. Behaviour unchanged.
3. Active/inactive states: in the toggle, confirm the filled icon + `error` tint appears when clinical
   mode is ON, and the outlined icon + muted tint when OFF (state logic must be untouched).

### 4.2 Build

- `./gradlew :app:assembleDebug` (or Android Studio build) succeeds with the swapped imports — watch for
  an unresolved-reference error if a `Healing` import was removed while still referenced, or an unused-
  import lint warning if one was left behind.

### 4.3 Definition of done

Both the clinical-mode toggle and the constructor clinical-case button display the **`Person`** icon on
Android, matching the Windows `U+E77B` patient glyph; tooltips/`contentDescription`s and all clinical
filtering/editing behaviour are unchanged; the app builds clean.

---

## 5. Notes

- Windows commit touches only the glyph value at two sites (`RhythmChoosingPanel.xaml` `ECAD → E77B`,
  `ConstructorScreen.cs` `\uECAD → \uE77B`) plus explanatory comments. No behaviour, string, or layout
  change. Windows build verified: `Build succeeded, 0 warnings, 0 errors`.
- Correct the stale claim in `2026-07-android-clinical-case-presentation-mode.md:41` that glyph `U+ECAD`
  is a stethoscope — it is a flame; Segoe MDL2 Assets has no stethoscope. (This plan supersedes that
  icon choice on both platforms.)
- Android's canonical rhythm chooser is **`RhythmSelector.kt`**, not `RhythmChoosingPanel.kt` — the
  latter only exists in feature worktrees under `Android/.claude/worktrees/…` and is not the shipping
  code; do not edit those.
