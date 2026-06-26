# Plan / Prompt: Port the new green design theme (and a WebView teardown fix) to Android

**Created:** 2026-06-25
**Status:** NOT STARTED
**Direction:** **Windows → Android** (reverse of the usual). The new UI design pattern was built in
the WinUI 3 port first, from the customer's two reference mockups (identical except accent — the
**green** variant was chosen). The Android app must now catch up. The Windows port is the
**reference implementation** for palette/visuals/behaviour — match it, adapting idioms to
Kotlin/Jetpack Compose.

**Target (Android) source root:** `C:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`

> Scope note: this is a **restyle**, not a restructure — every existing button/control and its name
> stays exactly as-is. No controls added/removed/renamed. Mockup-only decorations (per-beat numbers
> above the paper, the `HR 160` readout, time-axis labels, the mode pill's leading icon) are **not**
> added. A second, smaller item rides along: a defensive null-guard in the lecture WebView (see the
> last section).

---

## Use this as the prompt

> Adopt the new **green design theme** app-wide in the Android CardioSimulator, matching the WinUI
> port. The look: a soft **lavender page**, white rounded floating controls, **cream ECG paper** with
> a **teal trace**, flat borderless buttons, white **dropdown pills with chevrons**, and a **green
> accent** on active/selected state. Centralize the palette as design tokens (one Kotlin
> color object / theme), wire it through the shared `Tab` composable, the ECG grid+trace colors, the
> main scaffold, and the top/bottom control panels; then sweep the per-screen inline colors —
> mapping every **blue** brand-accent / selection tint to **green**, leaving semantic colors (red
> errors, pass/fail green-red, EOS red, the welcome splash) untouched. Read the Windows reference
> files first and copy the exact hex values. Also apply the lecture-WebView null-guard described at
> the end.

---

## Goal

Replace the current flat/edge-to-edge, black-bordered, pink-paper look with the mockup's pattern:
lavender shell, white rounded monitor card holding cream paper + teal trace, white dropdown pills
with chevrons, flat buttons, and one green accent on toggled/selected state — consistent across
Teaching, Testing, Examination, OSKE, the constructors, and dialogs.

## Reference files (Windows) — read these first

| Concern | Windows file(s) |
|---|---|
| **Design tokens (single source of truth)** | `App.xaml` (Color/Brush resources + accent overrides) and `Theming/AppTheme.cs` (code-behind accessors) |
| Shared button/pill control (`IsActive`, `ShowChevron`) | `Controls/Tab.xaml` + `Tab.xaml.cs` |
| ECG paper + trace colors | `Rendering/EcgColors.cs` |
| App shell (lavender page, rounded card, hairlines) | `Screens/MainScreen.xaml` |
| Top bar (compact mode pill + breadcrumb pills) | `Controls/TopControlPanel.xaml`, `Controls/TeachingControlPanel.cs` |
| Bottom / monitor panel (chevron dropdowns, pQRSt active) | `Controls/BottomControlPanel.xaml`, `Controls/MonitorControlPanel.xaml` + `.xaml.cs` |
| Rhythm list (green header band, accent) | `Controls/RhythmChoosingPanel.xaml` |
| Inline-color sweep examples (blue→green) | `Controls/ToolModePanelControl.cs`, `Controls/TestQuestionPanel.cs`, `Controls/ExamQuestionPanel.cs`, `Controls/SignificantPointsDrawer.cs`, `Screens/TestConstructorScreen.cs` |
| Lecture WebView fix | `Controls/LectureWebView.cs` |

---

## Design tokens — copy these hexes exactly

Define once (e.g. a `CardioColors` object or a Compose `ColorScheme` + a small custom-token holder)
and reference everywhere. Same values as the Windows `App.xaml` / `AppTheme.cs`.

| Token | Hex | Use |
|---|---|---|
| `accent` | `#33A06A` | active/selected fill, primary buttons, accent icons |
| `accentTint` | `#DCF1E6` | light selected wash (toggle-on chips, list selection, header bands) |
| `onAccent` | `#FFFFFF` | text/icon on accent fill |
| `pageBackground` | `#E8EAF4` | lavender app/page background |
| `panelBackground` | `#FFFFFF` | white cards / pills |
| `controlFill` | `#EFF1F7` | inactive control fill / pill hover |
| `controlBorder` | `#E0E4EC` | soft control & pill borders |
| `hairline` | `#E2E5EE` | section separators (replaces black dividers) |
| `hoverFill` | `#14808080` | subtle grey hover/press wash (ARGB; alpha 0x14) |
| `textPrimary` | `#1B2430` | primary text (replaces pure black) |
| `textSecondary` | `#5A6B82` | secondary / sub text / chevrons |
| `ecgTrace` | `#2C6E8E` | teal ECG trace (replaces black) |
| `paperBackground` | `#FCFCEC` | cream ECG paper |
| `gridMinor` | `#E6E4CE` | faint grid lines |
| `gridMajor` | `#D2CEA6` | every-5th grid lines |

On Windows, standard controls were re-accented by overriding the framework accent
(`SystemAccentColor`, `AccentFillColor*`) + `ControlCornerRadius`. The Compose equivalent: set
`primary` (and the accent-derived roles) of the `MaterialTheme` `ColorScheme` to `accent`, and use a
~6–8dp default `RoundedCornerShape` for buttons — so stock Material controls go green/rounded without
per-widget edits.

---

## Component specs (match the Windows source)

### 1. Shared `Tab` composable — ref `Controls/Tab.xaml(.cs)`
- **Default**: flat, transparent, rounded 8dp, `textPrimary` text, `hoverFill` on press/hover.
- **`isActive`** flag → `accent` fill + `onAccent` foreground (text + icon). This replaces every
  ad-hoc per-control active color (e.g. the old blue pQRSt fill).
- **`showChevron`** flag → a **white dropdown pill**: `panelBackground` fill (hover `controlFill`),
  `controlBorder` 1dp border, a trailing `⌄` chevron (`textSecondary`). Lay the chevron out in its
  **own end slot** (Row with label + chevron, not overlaid) so a content-sized/compact pill never
  overlaps the label. Add ~9dp horizontal padding.

### 2. Shell / scaffold — ref `Screens/MainScreen.xaml`
- Page background → `pageBackground` (lavender).
- Top and bottom control bars float **transparent** on the lavender (drop the black/grey dividers;
  use `hairline` where a separator is still needed).
- The center (monitor / mode content) sits in a **white rounded card**: `panelBackground`, corner
  radius ~16dp, small margin (~12dp), `controlBorder` hairline. Clip so the cream paper rounds to the
  card corners.

### 3. ECG paper + trace — ref `Rendering/EcgColors.cs`
- Trace → `ecgTrace` teal; lead labels → `textPrimary`.
- Default grid scheme → **cream** `paperBackground` + `gridMinor`/`gridMajor`. Keep the alternate
  blue-gray scheme. (On Android these are the `ekgGrid` / `ChartCanvas` colors the Windows
  `EcgRenderer` was ported from — update them at the source.)

### 4. Top bar — ref `Controls/TopControlPanel.xaml`, `Controls/TeachingControlPanel.cs`
- The **mode selector** becomes a **compact white pill** with a chevron (not a wide grey box).
- The Teaching breadcrumb is two `Tab`s (course selector + lecture/rhythm selector): give **both**
  `showChevron = true` so they render as **white dropdown pills** (mockup's "Training Program | …"
  style), not flat text.

### 5. Bottom / monitor panel — ref `Controls/MonitorControlPanel.xaml(.cs)`
- Flyout-opening tabs (lead count, scheme, sweep speed, scale, Artifacts) → `showChevron = true`
  (white dropdown pills).
- Genuine toggles (pQRSt; and any "running" indicator) → `Tab.isActive` (green) instead of inline
  blue fills. The custom 3D / pQRSt / EOS buttons → flat rounded to match `Tab`; **EOS stays red**.
- Group dividers → `hairline` (no black bars).

### 6. Rhythm list — ref `Controls/RhythmChoosingPanel.xaml`
- Group header band → `accentTint`; header icon → `accent`; titles → `textPrimary`; counts →
  `textSecondary`. List text → `textPrimary` (the currently-selected/active rhythm keeps its red
  highlight — that's semantic, leave it).

### 7. App-wide inline-color sweep (repeat one mapping)
Across the per-screen Compose code: panel fills → `panelBackground`/`controlFill`; hairlines →
`hairline`; **every blue brand-accent / selection tint → green** (`accent` / `accentTint`); pure-black
text → `textPrimary`. The recurring blue literal on Windows was `#2176FF` (e.g. quiz/exam accent,
stimulus chips, the ToolMode panel's selected `#BBD8F5`) → map all to `accent`/`accentTint`.
**Leave semantic colors:** red errors, pass/fail green-red, EOS red, the welcome splash.

---

## Phases

1. **Tokens** — add the centralized palette; set the Material `ColorScheme` accent + default rounded
   shape so stock controls adopt green.
2. **`Tab`** — add `isActive` + `showChevron` (white pill) and route all colors through the tokens.
3. **ECG colors** — teal trace + cream/grid in the grid/chart source.
4. **Shell + top/bottom panels** — lavender page, rounded monitor card, hairlines, chevron pills,
   compact mode pill, breadcrumb pills, pQRSt active=green.
5. **Sweep** — replace per-screen inline colors with tokens; blue→green; keep semantics.
6. **Lecture WebView guard** (below).

---

## Lecture WebView teardown guard — ref `Controls/LectureWebView.cs`

A pre-existing crash was fixed on Windows alongside the theme: the lecture renderer dereferenced the
web view after an `await`/teardown race, throwing a null-reference. The render method passes a
"ready" gate, then `await`s an off-thread HTML build + a file write; during those awaits the view can
be torn down (closed), nulling the native web object, and the subsequent `navigate(...)` NPE'd.

**Check the Android `LectureWebView` (the upstream of this control) for the analogous lifecycle
race** and apply the same defensive shape:
- After the async work, **re-read the web view / its core and null-check before navigating**; if it's
  been detached/destroyed, **re-stash the pending lecture and return** instead of crashing.
- Commit the "current HTML" cache **only right before** a successful navigate — never before the
  guard — so a bailed render doesn't poison the `html == currentHtml` short-circuit into a blank page.
- Apply the same null/destroyed guard right after web-view initialization completes (the
  init-during-teardown case).

If the Android implementation already handles WebView lifecycle (Android `WebView` differs from
WinUI's WebView2), confirm there's no equivalent null/destroyed-after-await path and note it.

---

## Verification

- **Teaching**: lavender page; cream rounded paper card; teal trace; flat buttons; **compact white
  mode pill + breadcrumb pills with chevrons**; chevron dropdowns on count/scheme/speed/scale/
  Artifacts; green pQRSt when toggled; green list-group bands + pin/selection.
- **Testing / Examination / Constructors / dialogs**: green accents and selection; no leftover blue
  brand-accent or black-bordered chrome; semantic red/green pass-fail intact.
- All four languages render unchanged (restyle only).
- Open a **course lecture** in Teaching, switch modes/lectures rapidly: it renders (text, KaTeX, the
  `<ecg>` figure) and never crashes from the WebView teardown race.

## Out of scope (kept as-is, matching Windows)

No controls added/removed/renamed. Mockup-only decorations are **not** added: per-beat numbers above
the paper, the `HR 160` readout, time-axis labels, the mode pill's leading document icon, the second
pill's internal `|` divider. The accent is one token, so switching to the mockup's blue variant later
is a one-value change.

---

> Companion theme reference on the Windows side is recorded in the port's memory
> (`theme-system-2026-06`). Related sync plans: `2026-06-android-monitor-panel-parity.md`,
> `2026-06-android-tips-window-parity.md`.
