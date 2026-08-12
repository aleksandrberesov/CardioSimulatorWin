# Plan: "Click-outside-to-dismiss" for all dialogs — Android parity

**Created:** 2026-08-11
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & the platform asymmetry (read this first)

**Customer request:** every dialog should close when the user clicks/taps outside it (on the dimmed
backdrop), the same as pressing Close/Cancel.

**What Windows had to do.** WinUI's `ContentDialog` is fully modal with **no built-in light-dismiss**,
so Windows added it:
- A global attached behavior `DialogLightDismiss` + an implicit `ContentDialog` style in `App.xaml`
  (`ctl:DialogLightDismiss.IsEnabled="True"`) turns it on for all ~60 `ContentDialog`s with zero
  per-call-site edits. On `Opened` it finds the dim backdrop rectangle `SmokeLayerBackground` (which in
  WindowsAppSDK 1.8 lives in a **separate popup**, enumerated via
  `VisualTreeHelper.GetOpenPopupsForXamlRoot`) and calls `Hide()` on its `PointerPressed`. `Hide()`
  returns `ContentDialogResult.None` — identical to Close/Cancel — so it is always the non-destructive
  path (safe because **every** dialog already had a Close button, i.e. the `None` path was already
  handled everywhere).
- The two custom scrim overlays in `Heart3DDialog` (Add-hotspot / Clear-all prompts) got a backdrop-tap
  handler; the main 3D overlay already had one.
- **Excluded by design:** full-window overlays with no "outside" (`MonitorViewerOverlay`,
  `WelcomeOverlay`, `ConstructorScreen._allLeadsOverlay`) keep an explicit close button.

**Why Android is different — and probably already conforms.** Jetpack Compose's
`androidx.compose.ui.window.Dialog` and Material3 `AlertDialog` **dismiss on outside-tap by default**
(`DialogProperties(dismissOnClickOutside = true)`), calling `onDismissRequest`. So the behavior Windows
had to *build* is the Compose *default*. An audit of the Android app (2026-08-11) found:

| Signal | Count | Meaning |
| --- | --- | --- |
| `AlertDialog(` | 48 | Material3 default → dismiss on scrim tap |
| `Dialog(` (compose.ui.window) | 10 | all `onDismissRequest = onDismiss`; content sized with margins |
| `dismissOnClickOutside = false` | **0** | nothing disables light-dismiss |
| empty `onDismissRequest = {}` | **0** | every dialog wires a real dismiss |
| `Popup(` / `ModalBottomSheet` | 0 / 0 | none |
| non-`Dialog` modal scrim overlays | 0 | none swallow the outside tap |

`Heart3DDialog.kt` is a real `Dialog(onDismissRequest = onDismiss)` (already dismisses) and the Windows
hotspot Add/Clear sub-prompts are **not ported** to Android, so there is nothing to mirror there.

**Therefore this is a VERIFY-AND-GUARD plan, not a feature port.** The expected outcome is "confirmed
already working, with regression guards added" — *not* a large code change. Do **not** add redundant
scrims to dialogs that already dismiss. Only write code for a dialog that verification proves does
*not* dismiss (see Part C for the fix pattern).

> Note: the Windows "doesn't close" bug that triggered this sync was a **WinUI-only** issue (hooking the
> wrong template element). It does not imply an Android bug — but Part A still verifies on-device rather
> than assuming.

---

## 2. Part A — On-device verification (do this first; expect all ✅)

Run the app on an emulator/device. For each dialog below: open it, tap the **dimmed area outside the
card**, confirm it closes and lands on the same state as its Cancel/Close/back action (nothing saved or
deleted). Record ✅/❌ per row; only ❌ rows need Part C.

**`ui/dialogs/` (real `Dialog{}`):**
- `ElectrodesDialog.kt` · `ComparisonTargetDialog.kt` · `ComparisonPresetsDialog.kt` ·
  `SaveComparisonPresetDialog.kt` · `SettingsDialog.kt` · `Heart3DDialog.kt`

**Components / panels with dialogs:**
- `components/HtmlBlockEditor.kt` · `components/SynthesizerDialog.kt` · `components/UnsavedChangesDialog.kt`
- `panels/ConstructorControlPanel.kt` · `panels/CourseConstructorControlPanel.kt` ·
  `panels/CourseConstructorTopPanel.kt` · `panels/MonitorControlPanel.kt` · `panels/TeachingControlPanel.kt`
  (two `androidx.compose.ui.window.Dialog(` at ~L113/L152) · `panels/TipsPanel.kt` · `panels/TopControlPanel.kt`

**Screens (mostly `AlertDialog`):**
- `ConstructorScreen.kt` · `CourseConstructorScreen.kt` · `DataSourceScreen.kt` · `ExaminationScreen.kt`
  · `LearningScaleScreen.kt` · `OSKEScreen.kt` · `QuickTestScreen.kt` · `TestComponents.kt` ·
  `TestConstructorScreen.kt`

---

## 3. Part B — Edge cases to confirm explicitly

1. **`QuickTestScreen.kt` (~L91):** the only `DialogProperties` user — `usePlatformDefaultWidth = false`
   with content `fillMaxWidth(0.9f).fillMaxHeight(0.85f)`. That still leaves a scrim margin, and
   `dismissOnClickOutside` defaults to `true` even when you pass a custom `DialogProperties` (you only
   overrode width). → should dismiss; confirm.
2. **Full-bleed guard:** a Compose `Dialog` whose content is `Modifier.fillMaxSize()` **with**
   `usePlatformDefaultWidth = false` covers the whole window, leaving **no** tappable "outside" — so the
   default dismiss becomes unreachable. Today none of the 10 `Dialog(` sites are full-bleed (they use
   `fillMaxWidth(0.95f)`, `0.9f × 0.85f`, or `width(400.dp).fillMaxHeight(0.8f)`). If one ever is, either
   shrink the content so a scrim shows, or keep it as an intentional full-screen surface with an explicit
   close button (this mirrors Windows excluding `MonitorViewerOverlay` / `WelcomeOverlay`).
3. **`components/MonitorOverlays.kt` (~L60):** a `fillMaxSize()` overlay with an opaque brand background —
   this is a full-screen takeover, the Android analog of Windows's excluded full-window overlays. It is
   **not** a click-outside candidate; leave its explicit close affordance.

---

## 4. Part C — Fix pattern (apply ONLY to dialogs that fail Part A)

- **A real `Dialog`/`AlertDialog` that doesn't dismiss:** ensure it isn't passing
  `DialogProperties(dismissOnClickOutside = false)` and that `onDismissRequest` actually flips the
  state boolean that hides it (`showX = false`). That is the whole fix — do not add a manual scrim.
- **A custom modal built as a `Box` scrim (not a `Dialog`):** give the scrim a **no-ripple**
  `clickable { onDismiss() }` and have the inner card consume its own clicks so only the backdrop
  dismisses — the Compose analog of Windows's `ReferenceEquals(e.OriginalSource, overlay)` guard:
  ```kotlin
  Box(
      Modifier.fillMaxSize()
          .background(Color.Black.copy(alpha = 0.32f))
          .clickable(
              interactionSource = remember { MutableInteractionSource() },
              indication = null
          ) { onDismiss() },
      contentAlignment = Alignment.Center
  ) {
      Surface(/* card */, Modifier.clickable(
          interactionSource = remember { MutableInteractionSource() }, indication = null
      ) { /* swallow: no-op */ }) { /* content */ }
  }
  ```
  (Prefer converting such a modal to a real `Dialog{}` instead — it gets scrim, dismiss, back-button, and
  focus handling for free.)

---

## 5. Part D — Regression guard

Since Android's default is already correct, the guard is simply "don't disable it":
- Code-review / CI grep: a new `Dialog(`/`AlertDialog(` must **not** set `dismissOnClickOutside = false`
  and must wire a non-empty `onDismissRequest`.
- Any new full-screen `Dialog` (`usePlatformDefaultWidth = false` + `fillMaxSize`) must ship an explicit
  close control (there is no backdrop to tap).

---

## 6. Windows ↔ Android dialog map (1:1 by name)

| Windows (`Win/src/CardioSimulator.App`) | Android (`ui/…`) | Notes |
| --- | --- | --- |
| `Controls/ElectrodesDialog.cs` | `dialogs/ElectrodesDialog.kt` | Win: ContentDialog (now light-dismiss); Android: `Dialog` default |
| `Controls/ComparisonTargetDialog.cs` | `dialogs/ComparisonTargetDialog.kt` | same |
| `Controls/Heart3DDialog.cs` | `dialogs/Heart3DDialog.kt` | Win custom overlay (backdrop tap); Android real `Dialog`. Hotspot Add/Clear sub-prompts are Win-only |
| `Controls/UnsavedChangesDialog.cs` | `components/UnsavedChangesDialog.kt` | confirm dialog; both dismiss = Cancel |
| `Screens/SettingsContent.cs` (hosted in a ContentDialog) | `dialogs/SettingsDialog.kt` | live-applied settings; outside-tap = close |
| ~55 inline `ContentDialog`s across `Screens/*` | `AlertDialog`s across `ui/screens/*` | Win added global light-dismiss; Android default |

---

## 7. Verification checklist (definition of done)

- [ ] Part A table filled in; every standard dialog closes on outside-tap and lands on Cancel/None state.
- [ ] Part B: QuickTest dialog dismisses; no full-bleed `Dialog` exists (or is handled); `MonitorOverlays`
      full-screen takeover intentionally keeps its close button.
- [ ] Part C applied only to any ❌ rows (expected: none).
- [ ] Part D guard noted in the review checklist / CI.
- [ ] Read-only viewers (e.g. Teaching course view) unaffected.
