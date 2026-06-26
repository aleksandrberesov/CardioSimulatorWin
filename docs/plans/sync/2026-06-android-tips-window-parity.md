# Plan / Prompt: Port the "Подсказки" (Tips) overlay window to Android

**Created:** 2026-06-24
**Status:** NOT STARTED
**Direction:** **Windows → Android** (reverse of the usual). The Tips window was built in the WinUI 3
port first (from the customer's annotated "Типы подсказок на ЭКГ" slide). The Android app must now
catch up. The Windows port is the **reference implementation** for behaviour/visuals — match it,
adapting idioms to Kotlin/Compose.

**Target (Android) source root:** `C:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`

> Companion plan: this extends `docs/plans/sync/2026-06-android-monitor-panel-parity.md`. That plan
> lists `Подсказки` as "Unchanged" — it is **no longer** unchanged: the Tips button now opens a
> window. Do that monitor-panel parity plan first (or at least its Phase 1 + the ЭОС window in
> Phase 7), because this Tips window is the EOS window's twin and reuses the same overlay scaffolding.

---

## Use this as the prompt

> Implement the **Tips ("Подсказки") overlay window** in the Android CardioSimulator, matching the
> Windows port. It is a translucent, right-docked panel over the Teaching monitor — the same visual
> idiom as the ЭОС (EOS) window — but where ЭОС *reads* the trace, Tips is a palette for *placing
> annotation overlays* on it. Wire the existing `Подсказки` tab to toggle it. Build a faithful
> **scaffold**: the overlay-kind palette selects (single-choice) and previews, but the actual
> placement gesture and rendered annotations on the trace are a later increment (same status as ЭОС
> vectors). Read the Windows reference files below first; copy the exact string values from
> `AppStrings.cs`.

---

## Goal

Make the `Подсказки` tab in the Teaching-mode monitor bottom panel open a **Tips window**: a
semi-transparent blue panel docked to the right edge of the monitor, overlaying the live trace so the
ECG shows through. It offers the customer's seven "tip kinds" as a single-select palette, previews
the resulting on-monitor tip list ("Видим:"), and explains where tips get added.

## Reference files (Windows) — read these first

| Concern | Windows file |
|---|---|
| **Tips window (the thing to port)** | `CardioSimulator.App/Controls/TipsWindow.cs` |
| Its twin (same right-dock idiom) | `CardioSimulator.App/Controls/EosWindow.cs` |
| Panel layout + the `TipsClick` event | `CardioSimulator.App/Controls/MonitorControlPanel.xaml` + `.xaml.cs` |
| Wiring (toggle + auto-close) | `CardioSimulator.App/Screens/MainScreen.xaml.cs` (Teaching block) |
| Strings (EN/RU/ZH/ES) — **source of truth for exact text** | `CardioSimulator.App/Localization/AppStrings.cs` (search `monitor_tips_`) |

On Windows it is a `Popup` (needed to clear the native Win2D surface). On Android a plain Compose
overlay works: a `Box` filling the monitor area with the panel `align = TopEnd`, same as the ЭОС
window from the companion plan. Reuse that scaffolding.

---

## The overlay kinds (the `TipOverlayKind` enum)

Mirror the Windows enum exactly. Seven kinds, in this order, each a numbered chip:

| # | Enum | String key | RU text |
|---|---|---|---|
| 1 | `Arrow` | `monitor_tips_type_arrow` | Стрелка с надписью |
| 2 | `LeadArea` | `monitor_tips_type_lead_area` | Вся область отведения |
| 3 | `GraphArea` | `monitor_tips_type_graph_area` | Область на графике |
| 4 | `EcgPart` | `monitor_tips_type_ecg_part` | Часть графика ЭКГ |
| 5 | `VerticalLines` | `monitor_tips_type_vertical_lines` | Линии вертикальные |
| 6 | `HorizontalLines` | `monitor_tips_type_horizontal_lines` | Линии горизонтальные |
| 7 | `Label` | `monitor_tips_type_label` | Надпись / подпись |

Each chip = a small bordered, rounded row containing: a **number badge** (`1.`…`7.`), a **mini white
pictogram** of the kind, and the **label**. Tapping selects it; **single-select** (one highlighted at
a time). Selection only updates the highlight for now — placing the overlay is future work.

**Pictograms** (tiny ~26×20 Canvas drawings, white strokes ~1.6px on the blue panel):
- `Arrow` — a diagonal line from bottom-left to top-right with a small arrowhead.
- `LeadArea` — a large rectangle nearly filling the icon, faint white fill (~30% alpha).
- `GraphArea` — a small rectangle centered in the icon, faint white fill.
- `EcgPart` — a tiny ECG waveform: flat baseline, one upward spike, back to baseline.
- `VerticalLines` — two vertical lines.
- `HorizontalLines` — two horizontal lines.
- `Label` — a rectangle (faint fill) with two short horizontal "text" lines inside.

---

## Visual spec (match the Windows `TipsWindow`)

Panel: width ~300dp, docked top-right of the monitor area, clearing the top mode bar (~72dp) and the
bottom control panel (~72dp), with a small right margin (~16dp). Rounded corners (16dp). Inner padding
~16/12/16/16. A vertical scroll for small monitors. Contents top → bottom:

1. **Title** `monitor_tips_window_title` ("Окно подсказок") — centered, white, ~18sp, semibold.
2. **Section header** `monitor_tips_types_header` ("Типы подсказок на ЭКГ:") — white, ~14sp, semibold, wraps.
3. **The 7 chips** (above).
4. **"Видим:" preview card** — a near-opaque white rounded card (≈95% white, radius 8) standing in
   for the tip pop-up that appears on the monitor. Header `monitor_tips_preview_header` ("Видим:") in
   ink blue, then four muted placeholder lines `1.  ……` … `4.  ……`.
5. **Note** `monitor_tips_note` ("Подсказки добавляются и редактируются в ключевых точках") — white,
   ~13sp, centered, wraps.
6. **✕ close** affordance pinned top-right.

**Colors** (from the Windows source — use the same hexes):
- Panel fill: `#5B9BD5` at ~80% alpha (`0xCC`).
- Text on panel: white.
- Chip fill (unselected): white at ~15% (`0x26`); chip fill (selected): white at ~35% (`0x59`);
  chip border: white at ~50% (`0x80`).
- Preview card bg: white at ~95% (`0xF2`); preview header ink: `#1E5FA5`; preview muted lines: `#999999`.

---

## Phase 1 — `TipOverlayKind` enum + Tips composable

- Add the `TipOverlayKind` enum (7 values above).
- Build a `TipsWindow` composable (or extend the EOS overlay host) rendering the spec above, with
  single-select chip state hoisted/remembered. Selecting a chip updates the highlight only.

## Phase 2 — Wire the `Подсказки` tab

- The companion plan keeps `Подсказки` in the bottom row. Give it an `onTips` callback (mirror the
  `onEos` pattern). Hoist it to the screen hosting the panel.
- Toggle behaviour: tapping `Подсказки` opens the window; tapping again (or ✕) closes it.
- **Auto-close when the monitor is hidden** (e.g. switching to a course), exactly like the ЭОС window,
  so it doesn't float over other content. On Windows, `MainScreen` closes EosWindow **and** TipsWindow
  together when the monitor visibility goes false — do the same.

## Phase 3 — Localization

Add these keys to the Android `strings.xml` for all four languages (ru/en/zh/es). The **exact values
are in the Windows `AppStrings.cs`** — copy them across (search each key there). The button key
`monitor_tips` already exists.

- `monitor_tips_window_title`
- `monitor_tips_types_header`
- `monitor_tips_type_arrow`, `monitor_tips_type_lead_area`, `monitor_tips_type_graph_area`,
  `monitor_tips_type_ecg_part`, `monitor_tips_type_vertical_lines`,
  `monitor_tips_type_horizontal_lines`, `monitor_tips_type_label`
- `monitor_tips_preview_header`
- `monitor_tips_note`

For reference (RU / EN), copy verbatim from `AppStrings.cs`:

| Key | RU | EN |
|---|---|---|
| `monitor_tips_window_title` | Окно подсказок | Tips window |
| `monitor_tips_types_header` | Типы подсказок на ЭКГ: | Types of ECG tips: |
| `monitor_tips_type_arrow` | Стрелка с надписью | Arrow with caption |
| `monitor_tips_type_lead_area` | Вся область отведения | Whole lead area |
| `monitor_tips_type_graph_area` | Область на графике | Area on the graph |
| `monitor_tips_type_ecg_part` | Часть графика ЭКГ | Part of the ECG graph |
| `monitor_tips_type_vertical_lines` | Линии вертикальные | Vertical lines |
| `monitor_tips_type_horizontal_lines` | Линии горизонтальные | Horizontal lines |
| `monitor_tips_type_label` | Надпись / подпись | Caption / label |
| `monitor_tips_preview_header` | Видим: | We see: |
| `monitor_tips_note` | Подсказки добавляются и редактируются в ключевых точках | Tips are added and edited at key points of the trace |

(ZH/ES values are also present in `AppStrings.cs` — copy those too.)

## Verification

- Tapping `Подсказки` on the Teaching monitor opens a translucent right-side panel with the trace
  visible behind it; tapping again or ✕ closes it; it closes when leaving the monitor.
- The seven kinds render with number badges, pictograms, and labels; tapping highlights one at a time.
- The "Видим:" preview card and the note render; the panel scrolls on a short monitor.
- All four languages render the new strings (no missing-key fallbacks).

## Out of scope (scaffolds, future increments — match Windows)

The placement gesture (dragging/tapping a chosen overlay onto the trace) and the **rendered
annotations** on the live ECG (arrows, lead/graph/segment highlights, guide lines, labels, and the
real on-monitor "Видим:" tip pop-up) are deferred — same status as the computed ЭОС vectors. This
phase ships the palette + preview only.
