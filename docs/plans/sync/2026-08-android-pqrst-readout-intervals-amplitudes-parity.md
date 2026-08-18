# Plan: pQRSt readout — PQ / QTc / 6-second HR / P·Q amplitudes (Android parity)

**Status:** active
**Owner:** Android agent
**Started:** 2026-08-18
**Related:** Windows source-of-truth change (this PR). Windows files:
`src/CardioSimulator.Core/Domain/EcgMeasurements.cs`,
`src/CardioSimulator.App/Controls/MonitorView.cs`,
`src/CardioSimulator.App/Localization/AppStrings.cs`,
`tests/CardioSimulator.Core.Tests/EcgMeasurementsTests.cs`.

## Goal

The customer asked for four additions to the pQRSt measurement function:

> «В функции PQRS добавить интервалы: PQ — от начала P до начала Q, QTc — по Базету,
> амплитуды P и Q, добавить ЧСС или среднее значение ЧСС за 6 сек.»

1. **PQ interval** — from the start of P to the start of Q (`P_START → Q_PEAK`).
2. **QTc (Bazett)** — corrected QT: `QTc = QT / √RR` (seconds).
3. **P and Q amplitudes** — in mV, per lead.
4. **Heart rate** — add the classic **6-second-method** rate alongside the existing mean-R-R rate.

Windows shipped these first (this is a Windows-led change). This plan brings the Android app to
content parity. **Note the presentation difference:** Windows renders the pQRSt values in a
translucent "Measurements" card (values column); Android has no such card — it draws interval
measurements directly on the trace in `SignificantPointOverlay`. Parity here means the same
*measurement content*, rendered in Android's existing on-trace style.

## Current state (Android)

- `ui/components/SignificantPointOverlay.kt` — single source of truth for on-trace measurements. It
  receives one lead's `points: Points`, builds `pointsMap = significantPoints.associateBy { it.type }`
  (last-marker-wins, matching Windows), and draws brackets for **P, PR (P_END→QRS_START segment),
  QRS, ST, T, PR (P_START→QRS_START, drawn "below"), QT, and R-R** (lines ~112–212). It shows R-R
  *durations* but **no bpm heart rate**, **no PQ (to Q peak)**, **no QTc**, **no amplitudes**.
  - Line 28 doc-comment already says "PQ interval" but the code labels `P_START→QRS_START` with
    `ecg_interval_pr` ("PR") — that is the PR/PQ synonym, **not** the new `P_START→Q_PEAK` interval.
  - The overlay is drawn **per lead cell**, each with that lead's own `points` → per-lead amplitudes
    fall out naturally (no lead-selection problem like Windows had).
- `domain/SignificantPoint.kt` — `EcgPointType` enum (has `Q_PEAK`, `P_PEAK`) + `SignificantPoint`.
  No measurement/derivation logic here today.
- `data/*` — `Points(values: List<Float>)`, and the pixel/calibration scale (`LocalPixelScale.current`,
  `scale.cal.sampleRateHz`, `scale.pxPerAdcCount`). The ADC-counts-per-mV constant lives on the
  calibration (Windows: `EcgCalibration.AdcCountsPerMv`, default **1024**). Waveforms are
  **baseline-zeroed** (drawn as `baselineY - value * stepY`, no baseline offset) → amplitude in mV is
  simply `value / adcCountsPerMv`.
- Strings: `app/src/main/res/values/strings.xml` (+ `values-ru/-zh/-es/-hi`). ECG interval keys at
  lines ~604–611 (`ecg_interval_pr`, `ecg_interval_qt`, `ecg_rr_value_format`, …). Formats are
  **printf-style** (`%1$.3f`, `%1$d`), not .NET `{0:0.000}`. `monitor_hr_format` = "HR %1$d" exists
  (line ~69). There is **no** `ecg_seconds_value_format` and **no** `monitor_measurements_*` card keys.

## Source of truth (Windows) — exact formulas

From `EcgMeasurements.Compute` (Core). Reproduce the math verbatim:

- **PQ** = `(Q_PEAK.index − P_START.index) / fs`, only when both marked and `Q_PEAK > P_START`.
  (Distinct from the existing PR = `P_START→QRS_START`; keep PR too.)
- **QT** = `(T_END.index − QRS_START.index) / fs` (already present).
- **QTc (Bazett)** = `QT / sqrt(RR)` seconds, only when QT present and `RR > 0`.
- **RR** = mean of consecutive R-peak spacings (≥2 R peaks), in seconds (already present).
- **Mean HR** = `60 / RR` bpm.
- **6-second HR** = count R peaks whose index `< window`, scaled to a minute:
  `window = min(6·fs, stripSamples)`; `windowSec = window / fs`; `hr6 = count · 60 / windowSec`.
  `stripSamples` = the lead's `points.values.size` (Android has the lead in hand); falls back to
  `lastRpeakIndex + 1` if unavailable. Requires ≥1 R peak. For a full 6-s strip this is `count · 10`;
  the scaling keeps short rhythms honest.
- **P amplitude** = `points.values[P_PEAK.index] / adcCountsPerMv` (mV; per lead).
- **Q amplitude** = `points.values[Q_PEAK.index] / adcCountsPerMv` (mV; typically negative).

## Non-goals

- **Do not** build a Windows-style translucent "Measurements" values card on Android. Keep the
  on-trace presentation. (A future card is a separate redesign.)
- No change to the on-disk `markers:` format, the auto-detect pipeline, or the editor panels.
- No renaming of the existing "PR" bracket. PQ is an *additional* measurement.

## Plan

### Phase 1 — Strings (shared resources, 5 locales)
Add to `values`, `values-ru`, `values-zh`, `values-es`, `values-hi` (printf style, mirror the
existing `ecg_*`/`monitor_hr_*` entries):
- `ecg_interval_pq` → "PQ" (all locales)
- `ecg_interval_qtc` → "QTc" (all locales)
- `ecg_mv_value_format` → en/zh/es/hi "%1$.2f mV", ru "%1$.2f мВ"
- `ecg_qtc_value_format` → "QTc %1$.3fs" (localize the unit like `ecg_rr_value_format`: ru "…с")
- `ecg_hr_value_format` → "HR %1$d" (or reuse `monitor_hr_format`); ru "ЧСС %1$d"
- `ecg_hr6_value_format` → en "HR 6s %1$d", ru "ЧСС 6с %1$d", zh "心率 6秒 %1$d", es "FC 6s %1$d",
  hi "HR 6s %1$d"

### Phase 2 — Measurement math (recommended: a small tested util)
Mirror Windows' extraction for testability:
- New `domain/EcgMeasurements.kt`: `EcgMeasurementSet` (add `pqSeconds`, `qtcSeconds`,
  `heartRateBpm`, `heartRate6SecBpm`, plus existing durations) + a `compute(points:
  List<SignificantPoint>, sampleRateHz: Float, values: List<Float>? = null, adcCountsPerMv: Float =
  1024f)` that returns PQ/QTc/HR/HR6 and (when `values` is supplied) `pAmplitudeMv`/`qAmplitudeMv` for
  **that lead**. (Android's overlay is single-lead, so a per-lead `Float?` is enough — no multi-lead
  `LeadAmplitude` list is needed, unlike Windows.)
- Unit tests `EcgMeasurementsTest.kt` mirroring `EcgMeasurementsTests.cs`: PQ, Bazett QTc,
  QTc-null-without-RR, 6-second window count, amplitude = value/adc, amplitude-null-without-values.

*Alternative (lower effort):* inline the four formulas directly in `SignificantPointOverlay.kt`
next to the existing `associateBy`/`rPeaks.windowed(2)` code. Prefer the util for parity + tests.

### Phase 3 — On-trace rendering in `SignificantPointOverlay.kt`
- **PQ bracket:** add a `drawInterval(P_START, Q_PEAK, pqLabel, …)` (needs `Q_PEAK` marked). Give it
  its own Y lane so it doesn't collide with the existing PR bracket (e.g. just below the PR "below"
  lane at `baselineY + 80f`).
- **QTc label:** draw `ecg_qtc_value_format` near the QT bracket (append under the QT text at
  `baselineY + 120f`, or as a second line). Only when RR exists.
- **P/Q amplitude labels:** at the P_PEAK / Q_PEAK markers, draw `ecg_mv_value_format` (small, offset
  from the peak dot so it doesn't overlap the P/Q letter). Uses `points.values[idx] / adcCountsPerMv`.
- **Heart rate text:** near the top R-R lane (or a corner), draw mean HR (`ecg_hr_value_format`,
  `60/RR`) and the 6-second HR (`ecg_hr6_value_format`) when R peaks exist.
- Pre-resolve all new strings with `stringResource(...)` **outside** the `Canvas` block (the file
  already does this for the existing labels — line ~50–56).

### Phase 4 — Verify
- Build the Android app; run the new unit tests.
- Load a pathology with full P/QRS/T markup (multiple R peaks) and confirm on the monitor: PQ bracket,
  QTc value, P & Q amplitudes on each lead cell, and both HR readouts. Screenshot vs. the Windows
  Measurements card for a content cross-check.

## Risks & open questions
- **Presentation divergence is intended** — Android stays on-trace; Windows uses the card. Flag if the
  reviewer instead wants a parity card on Android (bigger scope).
- **Q amplitude sign:** report signed mV (Q is usually negative). Match Windows (no `abs`).
- **QTc needs RR** (≥2 R peaks) and a marked QT; otherwise omit the QTc label (don't draw "QTc —").
- **6-second scaling on short strips:** a <6 s rhythm reports the average rate over its actual length
  (window = whole strip). This matches Windows; note it if it looks surprising on ~1 s demo rhythms.
- **On-trace crowding:** the overlay is already dense. If PQ/QTc/amplitude/HR text collides, prefer
  nudging Y lanes over dropping measurements; escalate if legibility suffers at 1× zoom.
