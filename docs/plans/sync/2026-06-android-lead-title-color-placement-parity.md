# Plan: Sync the lead-title color + placement to Android

**Created:** 2026-06-26
**Updated:** 2026-06-30 — placement reworked after live tuning; plan **reactivated**.
**Status:** ACTIVE (NOT STARTED on Android)
**Direction:** **Windows → Android** (reverse of the usual). The lead-label presentation was
iterated on the WinUI 3 port first, in response to customer feedback on the live monitor. Android
should apply the same changes so both platforms render the lead label identically.

**Target (Android) source root:** `C:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`

> **Note:** the placement design below **supersedes** the earlier "title in a strip to the LEFT of
> the pulse" version of this plan. If that earlier version was never applied to Android (it was
> NOT STARTED), just implement what's here. If it *was* partially applied, treat this as the new
> target and adjust.

---

## Background — the changes

Each lead cell on the monitor draws, left-to-right: a **left margin** (calibration pulse + lead
title), then the **trace** on the paper grid. Two things changed about the **lead title** (the
short lead name: `I`, `II`, `III`, `aVR`, `aVL`, `aVF`, `V1`..`V6`):

1. **Color** — the title is the **same teal as the waveform line** (`#2C6E8E`) so each lead's
   label reads in the color of its own trace (was dark slate `#1B2430`). *(This may already be on
   Android from the prior version of this plan — verify.)*

2. **Placement** — the title now sits **to the RIGHT of the calibration pulse**, **floating just
   above the isoline** (not on/below the baseline, and no longer in a left strip). Crucially, the
   **trace start is now a function of paper speed**: the calibration pulse (0.2 s wide) and the
   gap after the title both scale with speed, so the title never collides with the trace at high
   speed nor floats far from it at low speed. The title is **drawn wider than the space reserved
   before the trace**, so for short labels the trace stays close to the pulse and the lifted title
   simply floats over the trace's leading edge. Title **font 14**.

Neither change touches the waveform data, the grid, the calibration-pulse geometry, or the
amplitude scale — they are presentation-only.

### Per-cell left-margin layout (final)

```
cellX
 │  LeadIn        pulse                 TitleGap   title (drawn TitleArea wide, lifted up)
 │ ├──────┤ ├──wing|0.2s plateau|wing──┤ ├────┤ ├───────────────┐
 │                                                 ▲ floats TitleLift above the isoline
 │ ── isoline ───────────────────────────────────────────────────────────────────────
 │                                          └ only TitleClearance reserved ┘   gap   │ trace →
 │                                            (title overlaps the trace's leading edge)
```

---

## Reference: exact Windows state to mirror

### Change 1 — title color = trace color
| Windows file | Change |
|---|---|
| `CardioSimulator.App/Rendering/EcgColors.cs` | `Label` constant **equals `Trace`** (teal `#2C6E8E`), not the old dark slate `#1B2430`. Recolors every lead-title draw in `EcgRenderer` (main monitor, compare-mode pane, editable lead). |
| `CardioSimulator.App/Rendering/EcgSvgRenderer.cs` | Static course/lecture ECG figures: lead-label `fill` = that renderer's trace color (`#111111`). |

```csharp
public static readonly Color Trace = Color.FromArgb(0xFF, 0x2C, 0x6E, 0x8E); // teal
public static readonly Color Label = Trace;                                  // was #1B2430
```

### Change 2 — title right of the pulse, lifted, with a speed-aware trace start
Single source of truth in `CardioSimulator.App/Rendering/EcgRenderer.cs`. The old fixed
`CalAreaWidth = 80` constant was **replaced by a method** `TraceLeft(PixelScale scale)`:

```csharp
// Per-cell left-margin layout constants (px at the reference scale)
private const float LeadIn         = 8f;     // cell-left → pulse
private const float PulseWing      = 4f;     // each pulse foot
private const float PulseSeconds   = 0.2f;   // pulse plateau width, in PAPER TIME (× PxPerSec)
private const float TitleGap       = 4f;     // pulse → lead title
private const float TitleArea      = 32f;    // DRAWN lead-title width (fits aVR/aVL/aVF @ 14)
private const float TitleClearance = 18f;    // horizontal room RESERVED before the trace
private const float TitleLift      = 10f;    // px the title floats above the isoline
private const float TraceGapBase   = 3f;     // minimum title → trace gap (fixed)
private const float TraceGapSeconds= 0.05f;  // additional title → trace gap, in PAPER TIME
private const float LabelFontSize  = 14f;

// Trace-start origin (offset from a cell's left edge). NOTE the two PxPerSec terms:
public static float TraceLeft(PixelScale scale) =>
    LeadIn + 2f * PulseWing + PulseSeconds * scale.PxPerSec        // pulse (scales with speed)
    + TitleGap + TitleClearance                                    // fixed
    + TraceGapBase + TraceGapSeconds * scale.PxPerSec;             // gap (scales with speed)
//  = 41 + 0.25 * PxPerSec
```

- **Calibration pulse**: `startX = cellX + LeadIn` (far left, was offset by the old left strip);
  `pulseWidth = PulseSeconds * PxPerSec`; shape unchanged. `DrawCalibrationPulse` **returns its
  right-edge x** (`pulseRight = startX + wing + pulseWidth + wing`).
- **Lead title**: drawn at `x = pulseRight + TitleGap`, in a box of width `TitleArea`, **left-** and
  **bottom-aligned, no wrap**, with the box bottom at `baselineY - TitleLift` (so the text bottom
  floats `TitleLift` px above the isoline). Drawn `TitleArea` (32) wide but only `TitleClearance`
  (18) is reserved before the trace → the title overlaps the trace's leading edge, kept clear by
  the lift.
- **Trace start / width**: everywhere the trace is laid out uses `TraceLeft(scale)` (main monitor,
  compare panes, editable lead). `traceWidth = cellW - TraceLeft(scale)`.

`PxPerSec = PaperSpeedMmPerSec * PxPerMm` (paper speed × pixel density). So the time-based terms
are literally "this many seconds of paper": `0.2 s` pulse, `0.05 s` extra gap.

> **Important coupling:** `TraceLeft(scale)` is the **only** trace-start origin. On Windows it
> feeds **(a)** the draw path, **(b)** the editor's pixel→sample hit-testing
> (`EditableLeadControl`, 3 sites: `(x - EcgRenderer.TraceLeft(scale)) / PxPerSample`), and **(c)**
> the image `TraceExtractor` (now takes a `traceLeft` param; caller passes `TraceLeft(scale)`).
> Because it's a function of `scale`, draw and hit-test stay consistent at **every speed**
> automatically. On Android, the analog "trace left / cal-area width" used by **both** the draw
> path and any editor tap→sample mapping must become the **same speed-dependent value**.

### Static figures (`EcgSvgRenderer.cs`)
Static course figures render at a **single fixed paper speed**, so they keep a constant trace
start (no speed term needed). They were updated only to put the title to the **right of the pulse**
just above the baseline (`text-anchor="start"`, `y = baselineY - 4`). The lift/clearance/speed
tuning is **not** applied there. Mirror this on Android's static-figure path only if it exists.

---

## Steps (Android)

### 1. Recolor the lead title *(skip if already done)*
Find the Android lead-label draw — the Compose `Lead` composable / `LeadsGrid`, or a `drawText`
in the `ChartCanvas` / monitor `Canvas`. Set the label color to the **trace color** (the same
value used to stroke the waveform — reference the existing trace-color token, not a hardcoded hex).
Recolor the static/course-figure label too if Android has a separate path.

### 2. Make the trace-start (cal-area) a function of paper speed
Find Android's "trace left" / "cal-area width" — the x where the trace begins, which today is
almost certainly a **constant** dp. Replace it with a computed value, the analog of `TraceLeft`:

```
traceLeft(speed) = LeadIn + 2*PulseWing + PulseSeconds*pxPerSec(speed)
                 + TitleGap + TitleClearance
                 + TraceGapBase + TraceGapSeconds*pxPerSec(speed)
```

where `pxPerSec(speed) = paperSpeedMmPerSec * pxPerMm` (Android already has both factors to draw
the pulse and grid). Express the fixed terms in **dp** (`LeadIn 8, PulseWing 4, TitleGap 4,
TitleClearance 18, TitleLift 10, TraceGapBase 3`) — tune to Android text metrics so `aVR` fits the
`TitleArea` (~32 dp drawn) — and the `PulseSeconds 0.2` / `TraceGapSeconds 0.05` terms as
**seconds × pxPerSec**.

### 3. Move the pulse to the far left and the title to its right, lifted
- Pulse `startX = cellX + LeadIn` (no left strip); pulse shape unchanged.
- Compute `pulseRight = startX + wing + pulseWidth + wing` (`pulseWidth = 0.2s * pxPerSec`).
- Draw the title at `x = pulseRight + TitleGap`, **left-aligned, single line**, with its **bottom**
  at `baselineY - TitleLift` (≈10 dp above the isoline) — i.e. it floats up, not centered on the
  baseline. Draw it `TitleArea` (~32 dp) wide even though only `TitleClearance` (18 dp) is reserved
  before the trace; the title overlapping the trace's leading edge is intentional (the lift keeps
  it clear of the waveform).
- Font ≈ **14 sp**.

If Android lays out the label with a Compose `Text` in a `Row`/`Box` rather than `drawText`, the
equivalent: pulse first, then an `offset { y = -TitleLift }` (or top-aligned) `Text` of the trace
color placed right after the pulse, then the chart starting at `traceLeft(speed)`.

### 4. Keep the editor/tap mapping consistent
If Android's lead **editor** maps a tap x to a sample index using the cal-area/trace-left offset
(analog of `(x - traceLeft) / pxPerSample`), it must use the **same speed-dependent**
`traceLeft(speed)` as the draw path. A mismatch shifts every edited sample. Verify by tapping a
known sample at two different paper speeds and confirming the handle lands correctly both times.

### 5. Static figures (if present)
If Android renders course/lecture ECG figures separately from the live monitor, put the label to
the **right of the pulse, just above the baseline**, trace color, font ~14 — using the figures'
**fixed** speed (no speed term). Don't apply the lift/clearance.

---

## Verification
Open the monitor with a 12-lead layout and check at **several paper speeds (e.g. 12.5, 25, 100
mm/s)**, for `III`, `aVR`, `aVL`, `aVF`:

| Check | Expected |
|---|---|
| Title color | Same teal as that lead's waveform line |
| Title position | **Right of** the calibration pulse, **floating above** the isoline |
| 3-letter leads | `aVR`/`aVL`/`aVF` fully visible, not clipped |
| Trace start vs speed | Sits **close to the pulse** and the title→trace gap **grows with speed** (small at 12.5, larger at 100); never overlaps the pulse, never pushes the trace off-cell |
| Title vs trace | Title floats above the waveform's leading edge without obscuring it |
| Editor (if any) | Tapping a sample selects the correct sample **at every speed** |

Run the Android test suite; nothing data/scale-related should change.

---

## Acceptance checklist
- [ ] Lead title drawn in the **trace color** (referenced from the trace-color token).
- [ ] Calibration pulse at the **far left**; pulse shape unchanged.
- [ ] Lead title sits **right of the pulse**, **floating ~`TitleLift` above the isoline**, font ~14.
- [ ] Title drawn `TitleArea` wide so `aVR/aVL/aVF` aren't clipped; only `TitleClearance` reserved.
- [ ] **Trace-start is a function of paper speed** (pulse + title→trace gap scale with `pxPerSec`).
- [ ] Editor tap→sample mapping (if present) uses the **same speed-dependent** trace-left.
- [ ] Static course figures (if separate) updated for parity (fixed speed, no lift/clearance).
- [ ] No change to waveform data, grid, calibration-pulse geometry, or amplitude scale.
