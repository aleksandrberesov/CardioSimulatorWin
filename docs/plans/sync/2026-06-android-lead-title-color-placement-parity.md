# Plan: Sync the lead-title color + placement to Android

**Created:** 2026-06-26
**Status:** NOT STARTED
**Direction:** **Windows → Android** (reverse of the usual). Two small ECG-monitor
presentation tweaks were made in the WinUI 3 port first, in response to customer feedback on
the live monitor. Android should apply the same two changes so both platforms render the lead
label identically.

**Target (Android) source root:** `C:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`

---

## Background — the two changes

Each lead cell on the monitor draws, left-to-right: a **left margin** (calibration pulse + lead
title), then the **trace** on the paper grid. Two things changed about the **lead title** (the
short lead name: `I`, `II`, `III`, `aVR`, `aVL`, `aVF`, `V1`..`V6`):

1. **Color** — the title was a dark slate (`#1B2430`); it is now the **same teal as the waveform
   line** so each lead's label reads in the color of its own trace.
2. **Placement** — the title used to sit **centered in the calibration area, below the baseline**
   (horizontally overlapping the pulse's column). It now sits in a **dedicated strip at the far
   left of the cell, to the LEFT of the calibration pulse**, vertically centered on the baseline.
   The calibration pulse and the trace start were shifted right to make room. The title **font was
   reduced 16 → 14** so the 3-letter augmented leads (`aVR`/`aVL`/`aVF`) fit the strip.

Neither change touches the waveform data, the grid, the calibration-pulse geometry, or the
amplitude scale — they are presentation-only.

---

## Reference: exact Windows changes to mirror

### Change 1 — title color = trace color

| Windows file | Change |
|---|---|
| `CardioSimulator.App/Rendering/EcgColors.cs` | `Label` constant now **equals `Trace`** (teal `#2C6E8E`), instead of the old dark slate `#1B2430`. This one edit recolors every lead-title draw in `EcgRenderer` (main monitor lead title, compare-mode pane title, editable-lead title). |
| `CardioSimulator.App/Rendering/EcgSvgRenderer.cs` | Static course/lecture ECG figures: lead-label `fill` **`#000` → `TraceColor`** (that renderer's own trace color `#111111`), so label and line match there too. |

```csharp
// EcgColors.cs (source of truth for the number)
public static readonly Color Trace = Color.FromArgb(0xFF, 0x2C, 0x6E, 0x8E); // teal
public static readonly Color Label = Trace;                                  // was #1B2430
```

### Change 2 — title to the left of the pulse, in its own strip

| Windows file | Change |
|---|---|
| `CardioSimulator.App/Rendering/EcgRenderer.cs` | New `LabelAreaWidth = 32f` strip at the very left of each cell. `CalAreaWidth` (total left margin = label strip + pulse) **48 → 80**. Calibration-pulse `startX` **`cellX + 8` → `cellX + LabelAreaWidth + 8`**. Lead title drawn **vertically centered on the baseline** in `[cellX, cellX+LabelAreaWidth]` (was centered in the whole cal area, below the baseline). Title font **16 → 14** (`LabelFontSize`). Applies to both the live monitor and the editable-lead renderer. |
| `CardioSimulator.App/Rendering/EcgSvgRenderer.cs` | Mirror for static figures: `CalAreaWidth` **48 → 80**, add `LabelAreaWidth = 32f`, pulse `startX` shifted by the strip, label moved to the left strip with `dominant-baseline="central"` at `y = baselineY`, `font-size` **16 → 14**. |

Key Windows constants after the change (the numbers to carry over, in **px at the reference
scale**; Android should express them in **dp** the same way the rest of its monitor metrics are):

```
LabelAreaWidth = 32   // lead-title strip, left of the pulse
CalAreaWidth   = 80   // total left margin (label strip + calibration pulse) = where the trace starts
pulse startX   = cellX + LabelAreaWidth + 8   // 8 px lead-in, unchanged, just offset by the strip
title font     = 14   // was 16; shrunk so aVR/aVL/aVF fit the 32 px strip
title position = horizontally centered in [cellX, cellX+LabelAreaWidth], vertically centered on baseline
```

> **Important coupling:** `CalAreaWidth` is the **trace-start origin**. On Windows the same
> constant feeds the editor's pixel→sample hit-testing (`EditableLeadControl`) and the
> `TraceExtractor` (image→sample), so widening it keeps draw and hit-test consistent
> automatically. On Android, find the analog "trace left / cal-area width" used by **both** the
> draw path and any editor tap→sample mapping, and move them together. If they are separate
> constants, update both.

---

## Steps (Android)

### 1. Recolor the lead title
Find the Android lead-label draw — the Compose `Lead` composable / `LeadsGrid`, or a `drawText`
in the `ChartCanvas` / monitor `Canvas`. Search hints: `Lead`, `LeadsGrid`, `ChartCanvas`,
`drawText`, `lead.name`, `Text(` near the calibration pulse, `labelColor`, `Color(0xFF1B2430)`
or whatever dark color it currently uses.

Set the label's color to the **trace color** (the same value used to stroke the waveform —
search `traceColor`, `lineColor`, the `Color` passed to `drawLine`/`drawPath` for the ECG). Use
the existing trace-color reference rather than a hardcoded hex so the two never drift. If Android
also has a static/exported SVG or bitmap figure path (course content), recolor that label to its
trace color too.

### 2. Move the lead title left of the calibration pulse
In the same lead layout:
- Introduce a **label strip** at the far left of each cell (Android analog of `LabelAreaWidth`,
  ~**32 dp** — tune to the Android text metrics so `aVR` fits).
- Increase the **cal-area / trace-left** offset by the strip width (analog of `CalAreaWidth`
  48 → 80; i.e. **+32 dp**), so the trace starts after `strip + pulse`.
- Shift the **calibration pulse** right by the strip width (its internal `startX` gains
  `LabelAreaWidth`). The pulse's own shape/size is unchanged.
- Draw the title **horizontally centered in the strip** and **vertically centered on the
  baseline** (previously it was below the baseline / centered in the cal area).
- Reduce the title **font ~16 → ~14 sp** so the 3-letter leads fit.

If Android lays out the lead label with a Compose `Text` + `Row`/`Box` (rather than `drawText`),
the equivalent is: put the label `Text` in a fixed-width box at the start of the row, then the
calibration pulse, then the chart — and color the `Text` with the trace color.

### 3. Keep the editor/tap mapping consistent
If Android's lead **editor** maps a tap x-position to a sample index using the cal-area/trace-left
offset (analog of `(x - CalAreaWidth) / pxPerSample`), make sure it uses the **same, now-wider**
offset as the draw path. A mismatch here shifts every edited sample by 32 dp. Verify by tapping a
known sample and confirming the selected handle lands on it.

### 4. Static figures (if present)
If Android renders course/lecture ECG figures separately from the live monitor (Android analog of
`EcgSvgRenderer`), apply the same two changes there for parity: label = trace color, label in a
left strip vertically centered on the baseline, font reduced.

---

## Verification

On Android, open the monitor with a 12-lead layout and confirm, for `III`, `aVR`, `aVL`, `aVF`:

| Check | Expected |
|---|---|
| Title color | Same teal as that lead's waveform line (not dark slate) |
| Title position | In a strip at the **left edge** of the cell, **left of** the calibration pulse |
| Title baseline | Vertically centered on the trace baseline (not below it) |
| 3-letter leads | `aVR`/`aVL`/`aVF` fully visible, **not clipped** by the strip |
| Trace start | Begins just right of the pulse; waveform not overlapping the pulse/label |
| Editor (if any) | Tapping a sample still selects the correct sample (offset stayed consistent) |

Run the Android test suite; nothing data/scale-related should change.

---

## Acceptance checklist
- [ ] Lead title drawn in the **trace color** (referenced from the trace-color token, not hardcoded).
- [ ] Lead title sits in a **left strip, left of the calibration pulse**, vertically centered on the baseline.
- [ ] Calibration pulse and trace start **shifted right** by the strip width; pulse shape unchanged.
- [ ] Title **font reduced** so `aVR`/`aVL`/`aVF` fit the strip without clipping.
- [ ] Editor tap→sample mapping (if present) uses the same widened trace-left offset.
- [ ] Static course figures (if separate) updated for parity.
- [ ] No change to waveform data, grid, calibration-pulse geometry, or amplitude scale.
