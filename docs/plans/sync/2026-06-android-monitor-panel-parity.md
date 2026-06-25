# Plan: Port the monitor bottom control panel & its new windows to Android

**Created:** 2026-06-24
**Status:** NOT STARTED
**Direction:** **Windows → Android** (reverse of the usual). These features were built in the
WinUI 3 port first (from the customer's annotated screenshots); the Android app must now catch up.
The Windows port is the **reference implementation** for behaviour/visuals — match it, adapting
idioms to Kotlin/Compose.

**Target (Android) source root:** `C:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`

## Goal

Rework the Teaching-mode monitor's bottom control row and add the new options it launches, to match
the Windows port. The bottom row changes from the old
`6× | 1ст | 25мм/с | 100% | Электроды | ЭМД/ЭБПА | Мышцы | ЭОС | ЧСС 160 | Подсказки | линейка | ▶ | Сравнить`
to:

```
6× | 1ст | 25мм/с | 100% ‖ Электроды | Артефакты▾ | ♥3D ‖ pQRSt | ЭОС | Подсказки ‖ линейка | Сравнить | ▶   (⚙ stays in the outer bottom bar)
```

Concretely:
- **Removed:** `ЭМД/ЭБПА`, `Мышцы`, and the `ЧСС 160` readout.
- **New:** `Артефакты` (dropdown), `♥ 3D` (opens a window), `pQRSt` (toggle).
- **Changed:** `Электроды` now opens a window; `ЭОС` becomes a clickable button that opens a window.
- **Unchanged:** the count/scheme/speed/scale dropdowns, `Подсказки`, `линейка`, `Сравнить`, `▶`, and
  the Settings `⚙` button (which lives in the outer bottom bar, not this panel).

The five launchable surfaces are: **Электроды window**, **Артефакты dropdown**, **3D heart window**,
**pQRSt graph overlay**, **ЭОС window**.

## Reference files (Windows) — read these first

| Concern | Windows file |
|---|---|
| Panel layout + handlers | `CardioSimulator.App/Controls/MonitorControlPanel.xaml` + `.xaml.cs` |
| Electrodes window | `CardioSimulator.App/Controls/ElectrodesDialog.cs` |
| 3D heart window | `CardioSimulator.App/Controls/Heart3DDialog.cs` |
| ЭОС window (translucent right dock) | `CardioSimulator.App/Controls/EosWindow.cs` |
| pQRSt model flag | `CardioSimulator.Core/Domain/MonitorMode.cs` (`ShowImpulseLabels`) |
| pQRSt setter | `CardioSimulator.App/ViewModels/MonitorViewModel.cs` (`SetShowImpulseLabels`) |
| pQRSt render gating | `CardioSimulator.App/Rendering/EcgRenderer.cs` (gates `DrawSignificantPoints` in `Render`) |
| Wiring | `CardioSimulator.App/Screens/MainScreen.xaml.cs` (Teaching block) |
| Strings (EN/RU/ZH/ES) | `CardioSimulator.App/Localization/AppStrings.cs` (source of truth for exact text) |
| Artifact enum | `EcgArtifact` in `MonitorControlPanel.xaml.cs` |

The Android `MonitorControlPanel` composable is what the Windows one was ported from, so the Android
side already has the bordered `Tab` composable and the four left dropdowns — reuse them.

---

## Shared assets — drop-in for Android

Four PNGs were added to the Windows assets and must be copied into the Android app
(`app/src/main/assets/` or `res/drawable-nodpi/`, whichever the existing image loader uses):

- `heart_3d.png` — anatomical heart, used on the **3D button icon** and inside the 3D window.
- `electrodes_chest.png` — chest electrode placement (V1–V6), left-top of the Electrodes window.
- `electrodes_cross.png` — transverse cross-section with V1–V6 dots, left-bottom of the Electrodes window.
- `electrodes_body.png` — full-body limb-electrode figure (RA/LA/RL/LL), right of the Electrodes window.

All four live at `E:\VLN_Project\CardioSimulatorWin\src\CardioSimulator.App\Assets\`. They are the
customer's original images — copy them verbatim.

---

## Phase 1 — Bottom control panel layout

In the Android `MonitorControlPanel` composable, rebuild the row to the new order above.

- Drop the `ЭМД/ЭБПА`, `Мышцы` tabs and the `ЧСС` readout.
- Add a `Артефакты` tab that opens a dropdown (Phase 3).
- Add a `♥ 3D` tab: an icon+label cell showing `heart_3d.png` + "3D" (Windows uses a small `Image`
  next to a `TextBlock`; in Compose use a `Row { Image(...); Text("3D") }` inside the same bordered
  cell as the other tabs). Tapping opens the 3D window (Phase 5).
- Add a `pQRSt` toggle cell: bordered, label "pQRSt"; **active state = blue fill + white text**,
  inactive = transparent fill + black text. Tapping toggles `MonitorMode.showImpulseLabels` (Phase 4).
- Make `Электроды` open the Electrodes window (Phase 6).
- Make `ЭОС` a clickable button (it was a static red label). Keep the red "ЭОС" text in a bordered
  cell; tapping opens the ЭОС window (Phase 7). Add a light press/hover wash like the other tabs.
- Keep `Подсказки`, `линейка`, `Сравнить`, `▶` and their existing behaviour. `⚙` stays in the outer
  bottom bar.

Hoist callbacks (`onElectrodes`, `onArtifactsSelected`, `on3D`, `onPqrstToggled`, `onEos`) to the
screen that hosts the panel, mirroring the existing `onStartStop` / `onCompare` pattern.

## Phase 2 — pQRSt model flag (do before the toggle is wired)

- Add `showImpulseLabels: Boolean = false` to the monitor mode model (Windows `MonitorModeModel`).
- Add a setter on the monitor view-model (`setShowImpulseLabels`) that copies the mode.
- Android already renders the significant-point overlay (peak labels + intervals from the rhythm's
  markup) — find that draw call (the `SignificantPointOverlay`, the Windows port mirrors it) and
  **gate it behind `showImpulseLabels`**. Default OFF: labels appear only when pQRSt is toggled on.
  (Windows: `EcgRenderer.Render` now does `if (mode.ShowImpulseLabels && significantPoints…)`.)
  Leave the **editor's** lead overlay always-on — only the live Teaching monitor is gated.

## Phase 3 — Артефакты dropdown

A dropdown menu (Compose `DropdownMenu`) of recording-artifact types; selection is single-choice
(radio). Define an enum mirroring Windows `EcgArtifact`: `None, Muscle, Mains, Baseline, Contact,
Motion`. Items (strings below): `Без артефактов / Мышечные / Сетевая наводка / Дрейф изолинии /
Плохой контакт / Движения`. For now selection just raises a callback — the actual artifact noise on
the trace is a later increment (scaffold parity with Windows).

## Phase 4 — pQRSt toggle wiring

Wire the toggle cell to `setShowImpulseLabels`. The blue active visual reflects
`mode.showImpulseLabels`. No separate window — it only flips the overlay flag.

## Phase 5 — 3D heart window

A modal dialog (Compose `Dialog`/`AlertDialog` with a wide custom layout), cream background
(`#F2EFE6`), three columns:
- **Left:** six blue rounded buttons (flat blue `#5B9BD5`, white text): `Схема отведений`,
  `Функция 2`, `Функция 3`, `Инфаркт миокарда`, `Функция 5`, `Функция 6`.
- **Middle:** a blue rounded panel with centered white text: the description line + "Либо окно
  12-канального ЭКГ".
- **Right:** `heart_3d.png` in a white card, with a blue `ЭКГ отведение` button below.
- A Close button.

All buttons are scaffolds (no action yet) except Close. The rotatable 3D viewport is future work.

## Phase 6 — Электроды window

A modal dialog, cream background, with a right-aligned blue title pill `Система отведения :
Стандартная`, then three columns:
- **Left:** `electrodes_chest.png` (caption `Размещение на грудной клетке`) above
  `electrodes_cross.png` (caption `Поперечный срез`), each in a white card.
- **Middle:** the colour-coded legend, then a row of three blue state buttons (`Все ок`,
  `Перепутаны`, `Смещение`). Legend rows = a coloured dot + text:
  - Limb: RA red, LA yellow, RL green, LL black.
  - Chest: V1 red, V2 yellow, V3 green, V4 brown, V5 black, V6 purple.
  - Dot colours: red `#E53935`, yellow `#FDD835`, green `#43A047`, black `#101010`, brown `#8D6E63`,
    purple `#8E24AA`; thin gray dot border for the yellow's contrast.
- **Right:** `electrodes_body.png` (the RA/LA/RL/LL limb figure).

State-button behaviour is a scaffold. **Note:** the customer's source art is internally inconsistent
on RL/LL colours (legend = RL green/ground, LL black; the body image = RL black, LL green "ground").
We followed the **legend text** for the legend dots and used the body image as-is on the right.

## Phase 7 — ЭОС window (translucent, right-docked)

A semi-transparent panel docked to the **right edge of the monitor**, overlaying the live trace so
the ECG shows through. On Windows this is a `Popup` (to clear the native Win2D surface); on Android a
plain Compose overlay works — a `Box` filling the monitor area with the panel `align = TopEnd`, a
translucent blue background (`#5B9BD5` at ~80% alpha), rounded corners. Contents top→bottom:
- Title `Окно ЭОС` (centered, white).
- `1.  Вектор 1`, `2.  Вектор 2`.
- Note: `На ЭКГ выделены участки этих векторов`.
- A small hexaxial reference circle with two coloured vectors (red + blue) — draw with Canvas;
  placeholder for the computed axis.
- `Результат:` line.
- A ✕ close affordance.

Toggle behaviour: tapping `ЭОС` opens it; tapping again (or ✕) closes it. Auto-close when the monitor
is hidden (e.g. switching to a course) so it doesn't float over other content.

## Phase 8 — Localization

Add these keys to the Android `strings.xml` for all four languages (ru/en/zh/es). The **exact values
are in the Windows `AppStrings.cs`** — copy them across (search each key there). Keys:

- Artifacts: `monitor_artifacts`, `monitor_artifact_none`, `monitor_artifact_muscle`,
  `monitor_artifact_mains`, `monitor_artifact_baseline`, `monitor_artifact_contact`,
  `monitor_artifact_motion`.
- 3D window: `monitor_3d`, `monitor_3d_title`, `monitor_3d_placeholder`, `monitor_3d_lead_scheme`,
  `monitor_3d_function_format` (has a `%d`), `monitor_3d_mi`, `monitor_3d_description`,
  `monitor_3d_or_ecg`, `monitor_3d_ecg_lead`.
- ЭОС window: `monitor_eos_window_title`, `monitor_eos_vector_format` (`%d`), `monitor_eos_note`,
  `monitor_eos_result` (plus the existing `monitor_eos` for the button).
- Electrodes window: `electrodes_system_standard`, `electrodes_ra/la/rl/ll`,
  `electrodes_v1`…`electrodes_v6`, `electrodes_state_ok`, `electrodes_state_swapped`,
  `electrodes_state_displacement`, `electrodes_caption_chest`, `electrodes_caption_cross`.

(`monitor_emd_ebpa`, `monitor_muscle`, `monitor_hr_format` become unused on this screen — leave them
or remove per Android conventions.)

## Verification

- Teaching monitor shows the new bottom row; old tabs/readout gone.
- pQRSt off by default; toggling it shows/hides the impulse/interval labels (blue when active).
- Артефакты dropdown lists the six items, single-select.
- Электроды / 3D open their windows with the four images rendering correctly.
- ЭОС toggles a translucent right-side panel with the trace visible behind it; closes on re-tap and
  when leaving the monitor.
- All four languages render the new strings (no missing-key fallbacks).

## Out of scope (scaffolds, future increments)

3D rotatable viewport; 3D/Electrodes button actions; real artifact noise on the trace; computed EOS
vectors + ECG segment highlighting; the 12-lead ECG embed in the 3D middle panel.
