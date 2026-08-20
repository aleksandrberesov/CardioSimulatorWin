# Plan: Constructor Dialog Theme Parity (verify Compose dialogs follow the in-app theme)

**Created:** 2026-08-17
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goals

On Windows, WinUI `ContentDialog` does **not** inherit the app's currently-selected light/dark
theme automatically — it defaults to the system theme unless each dialog explicitly sets
`RequestedTheme`. The Windows fix set `RequestedTheme = Theming.AppTheme.Current` on every
`ContentDialog`:

- `Win/src/CardioSimulator.App/Screens/CourseConstructorScreen.cs` — fixed earlier.
- `Win/src/CardioSimulator.App/Screens/ConstructorScreen.cs` — the ECG pathology constructor,
  **21** `new ContentDialog { ... }` initializers (new/duplicate/delete/rename pathology, WFDB &
  PhysioNet import, import-record, generate-derived-leads, insert/manage elements, group edit,
  clinical case, description, synth, auto-detect, tip comments/caption, and the shared `ShowError`
  helper).
- `Win/src/CardioSimulator.App/Controls/UnsavedChangesDialog.cs` — both overloads
  (`CourseConstructorViewModel` already had it; the `ConstructorViewModel` overload was missing it).

**Goal on Android:** guarantee that every dialog opened from the ECG constructor (and its siblings)
renders in the app's *selected* light/dark theme, not the system theme — the same guarantee the
Windows fix provides. **Why now:** to keep the Win/Android theming behavior in lock-step while the
Windows dialog-theme sweep is fresh.

## 2. Current state (Android) — the WinUI defect most likely does NOT reproduce here

Android's dialogs are built on Jetpack Compose Material3 `AlertDialog`, which is architecturally
different from WinUI `ContentDialog`:

- **The app theme is app-selected, not system-driven.** `MainActivity.kt:100-101` collects the
  in-app toggle and wraps the whole UI tree:
  ```kotlin
  val isDarkTheme by viewModel.isDarkTheme.collectAsState()
  CardioSimulatorTheme(darkTheme = isDarkTheme) { ... }
  ```
  `CardioSimulatorTheme` (`ui/theme/Theme.kt:68-83`) maps that flag to a `MaterialTheme(colorScheme
  = Dark/LightColorScheme)`.
- **Compose dialogs inherit `MaterialTheme` via composition locals.** A Compose `AlertDialog` /
  `Dialog` opens a new window but composes its content as a sub-composition of the parent, so
  `LocalColorScheme` (and the rest of the `MaterialTheme`) propagate into the dialog. A dialog shown
  while `isDarkTheme = true` therefore already paints dark.
- **No View-based dialogs exist.** A sweep for `MaterialAlertDialogBuilder`,
  `android.app.AlertDialog`, `AlertDialog.Builder`, and `androidx.appcompat.app.AlertDialog` returns
  **zero** hits. All ~48 dialog call-sites under `ui/` are Compose `AlertDialog` or composables built
  on it (e.g. `ui/components/UnsavedChangesDialog.kt`, `SynthesizerDialog`, `ClinicalCaseDialog`, and
  the inline `AlertDialog`s in `ui/screens/ConstructorScreen.kt` at lines ~217, ~238, ~295, ~350).
  View-based dialogs are the ones that would *not* inherit the Compose theme; there are none.

**Conclusion:** the direct WinUI analog ("dialog ignores the app's theme selection") should not
reproduce on Android. This plan is therefore **verification-first** — confirm the behavior, and only
write code if a specific dialog is found escaping the app theme scope.

## 3. Non-goals

- No mechanical port of `RequestedTheme` — there is no per-dialog theme property to set in Compose,
  and adding one would be redundant with `MaterialTheme` inheritance.
- Not touching the app-level theme wiring (`isDarkTheme` StateFlow, `CardioSimulatorTheme`) — that is
  already correct and is covered by earlier sync plans
  (`2026-08-android-light-dark-theme-schemas-parity.md`,
  `2026-08-android-csharp-controls-theme-switching-parity.md`).
- Not re-styling dialog visuals; only ensuring they follow the selected theme.

## 4. Plan

### Phase 1 — Verify (likely the whole job)
- Run the app; in Settings toggle the in-app Dark/Light theme so it differs from the device's system
  theme (this is what separates "follows app" from "follows system").
- From the ECG constructor, open each dialog and confirm it matches the *app* theme:
  new/duplicate/delete/rename pathology, group edit, clinical case, description, calculate-derived,
  insert/manage element, synthesizer, auto-detect result, tip comments/caption, WFDB/PhysioNet
  import, and the unsaved-changes guard (switch pathology / leave screen with unsaved edits).
- If **all** dialogs already follow the app theme: move this plan to `completed/` with an
  *Outcome: no code change needed — Compose inherits `MaterialTheme`* note. Done.

### Phase 2 — Fix only what Phase 1 flags (contingent)
- **Nested theme scopes that re-default to system.** Any `CardioSimulatorTheme { ... }` /
  `MaterialTheme(...)` call nested below the root that omits `darkTheme` re-defaults to
  `isSystemInDarkTheme()` and will ignore the app override. Known instance to check:
  `ui/components/ChartCanvas.kt:104` (`CardioSimulatorTheme { ... }`). If any such scope hosts dialog
  or dialog-preview content, thread the app's `isDarkTheme` into it (read from the app/theme
  ViewModel, or hoist a `LocalAppIsDark` composition local) so the nested scope matches the root.
- **Dialogs launched outside the composition** (should be none — verify): if any future dialog is
  created imperatively (`Dialog(...)`/`ComposeView` island / `AndroidView`), wrap its content in
  `CardioSimulatorTheme(darkTheme = <app isDarkTheme>) { ... }`.

## 5. Risks & open questions
- **Q:** Does a Compose `AlertDialog` opened while `isDarkTheme=true` inherit the dark scheme?
  **Expected A (verify in Phase 1):** yes — dialog content is a child sub-composition and receives
  `MaterialTheme` locals. If a device/Compose version is found where it does not, Phase 2's wrap
  applies.
- **Edge:** `@Preview` composables call `CardioSimulatorTheme { }` with the default system flag — that
  is preview-only and not user-facing; ignore.

## 6. Verification
- Manual: with app theme set opposite to system theme, every constructor dialog listed in Phase 1
  renders in the app theme (dark chrome/text on dark, light on light); no white flash / stale-theme
  dialog.
- Build: `./gradlew :app:assembleDebug` passes.

## 7. PR breakdown

| # | PR title | Phase | Notes |
|---|----------|-------|-------|
| 1 | Verify constructor dialogs follow in-app theme (Android) | 1 | Likely doc-only; close as no-op if all pass |
| 2 | Thread app isDarkTheme into nested theme scopes (if needed) | 2 | Only if Phase 1 finds an escaping scope (e.g. ChartCanvas) |

---

## Outcome

*(Fill in when status moves to completed/dropped.)*

- **Result:** shipped / dropped / partial
- **PRs:** #…
- **Deviations from plan:** …
- **Follow-ups spawned:** …
