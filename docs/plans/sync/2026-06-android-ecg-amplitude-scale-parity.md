# Plan: Sync the ECG amplitude scale (ADC counts/mV) to Android

**Created:** 2026-06-26
**Status:** NOT STARTED
**Direction:** **Windows → Android** (reverse of the usual). The fix was made in the WinUI 3
port first, in response to the customer's screenshot showing waveforms too tall against the
paper grid. The Android app must apply the same calibration change so both platforms render the
shared dataset identically.

**Target (Android) source root:** `C:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`

---

## Background — what was wrong and why

The bundled pathology dataset (`Assets/Pathologies.zip`) stores each lead as **raw ADC samples**,
baseline-centered on **1024**, in the range **0..2048**. To draw a sample at standard ECG paper
scale the renderer needs one calibration number: **ADC counts per millivolt**. Standard paper is
**10 mm/mV** and **1 large cell = 5 mm = 0.5 mV**, so:

```
large cells spanned by a peak = (sample - baseline) / AdcCountsPerMv * GainMmPerMv / 5
```

**Key insight:** this ratio depends ONLY on `AdcCountsPerMv` and `GainMmPerMv` (10). It is
**independent of pixels-per-mm / display scale** — those scale the grid and the trace together and
never change how many *cells* a wave spans. So the bug is in the amplitude constant, **not** the
grid density. Do not "fix" this by touching the px-per-mm anchor.

The Windows port shipped with `AdcCountsPerMv = 256`, which made every waveform **too tall**:
- Sinus Arrhythmia (`sinar`) lead II R peak ≈ raw 1720, baseline 1024 → 696 counts above baseline.
- At 256 counts/mV that renders as **2.72 mV ≈ 5.4 large cells** (what the customer saw — clinically implausible).

The dataset was investigated and the constant was corrected. The **agreed canonical value is
`1024` counts/mV** (full scale ±1 mV over the 0..2048 / baseline-1024 range). Rationale:
- Real WFDB records use gain **1000 counts/mV**; at domain 1024 a true **1 mV** maps to domain
  **2048** (top of range) and renders at its true physiological amplitude.
- The 0..2048 band with baseline 1024 becomes exactly **±1 mV** nominal full scale.

> Note the history: the value moved 256 → 512 → **1024** during the session. 512 makes the
> hand-authored base data look clinically "ideal" (sinar R ≈ 1.36 mV), but **1024 is the chosen
> domain scale** because it lines real WFDB imports up to true mV. Implement **1024**.

The **raw `.dat` samples never change** — only the interpretation constant. Android needs only the
constant change (plus the WFDB/import-path consistency edits below) to render the same files
correctly.

---

## Reference: exact Windows changes to mirror

| Concern | Windows file | Change |
|---|---|---|
| **Core calibration constant** | `CardioSimulator.Core/Data/EcgCalibration.cs` | `AdcCountsPerMv` default **256 → 1024** |
| WFDB ↔ domain converter | `CardioSimulator.Core/Data/Wfdb/WfdbConverter.cs` | `DomainCountsPerMv` const **256 → 1024** (+ doc comment) |
| Offline bulk importer | `tools/wfdb-import/add_wfdb.py` | `DOMAIN_COUNTS_PER_MV` **256.0 → 1024.0** (+ header comment) |
| Importer README | `tools/wfdb-import/README.md` | prose 256 → 1024 |
| Unit tests | `tests/CardioSimulator.Core.Tests/{EcgElementGeneratorTests,PixelScaleTests,WfdbConverterTests,WfdbImportTests}.cs` | expectations recomputed at 1024 |

The current Windows `EcgCalibration` (the source of truth for the number):

```csharp
public sealed record EcgCalibration(
    float GainMmPerMv = 10f,
    float SampleRateHz = 500f,
    float AdcCountsPerMv = 1024f);   // was 256f
```

---

## Steps (Android)

### 1. Locate the Android calibration constant
Find the Android analog of `EcgCalibration` / `PixelScale` — the single place that maps a domain
ADC count to a paper/pixel distance for the waveform. Likely a constant such as `ADC_COUNTS_PER_MV`,
`countsPerMv`, `adcPerMv`, or a value baked into the Compose `ChartCanvas` / `ekgGrid` Y-mapping
(`y = baseline - sample * pxPerAdcCount`, where `pxPerAdcCount = pxPerMv / countsPerMv`).

Search hints: `256`, `countsPerMv`, `perMv`, `1024` (baseline), `pxPerAdcCount`, `gainMmPerMv`,
`ChartCanvas`, `CalibrationPulse`.

Set it to **1024**. Confirm the **same constant** feeds both:
- the waveform Y-mapping, and
- the constructor's "place element" mV→ADC generator (Android analog of `EcgElementGenerator`,
  `amplitudeAdc = amplitudeMv * countsPerMv`),

so authored elements and rendered traces stay consistent. (The 1 mV **calibration pulse** is drawn
from `pxPerMv` directly and is unaffected — it stays 2 large cells tall.)

### 2. WFDB import/export consistency (if Android has it)
The Android app exposes an **"Import WFDB file…"** path (the in-app counterpart of `add_wfdb.py`).
Its domain constant — the Android analog of `WfdbConverter.DomainCountsPerMv` — must **also be 1024**
so it never drifts from the renderer. The conversion is a two-step bridge and is **gain-agnostic**:

```
import:  mv     = (raw - fileBaseline) / fileGain         // fileGain = 1000 from the .hea
         domain = round(1024 + mv * 1024)                 // domain counts/mV = 1024
export:  mv     = (domain - 1024) / 1024
         raw    = round(mv * fileGain) + fileBaseline
```

The file's 1000 and the domain's 1024 **never need to be equal** — physical mV is the common unit.
Verify the Android importer reads the gain **per-signal from the header** (with WFDB's "gain 0 =
uncalibrated → default 200" fallback), not a hardcoded constant.

### 3. Update Android tests
Recompute any tests that hardcoded 256-based expectations. Reference recomputations (at 1024):
- Element generator: a `0.15 mV` P-wave peaks at `baseline + round(0.15 * 1024)` = **baseline + 154**
  (Windows test brackets `+122..+180`).
- WFDB import (gain 1000): `+1 mV → 2048`, `0 → 1024`, `-1 mV → 0`; `+0.5 mV → 1536`.
- WFDB convert: `+1 mV → 2048`, `-0.5 mV → 512`.
- `pxPerAdcCount = pxPerMv / 1024` (half the 512 value, a quarter of the old 256 value).

### 4. Do NOT regenerate or re-scale the bundled `.dat` data
Raw samples are unchanged. Both apps consume the **same** `Pathologies.zip`; only the constant
differs. Once Android uses 1024 it renders the existing files correctly. The large prebuilt bundles
(`Pathologies.{100,600,1000,10000,all}.zip`) are produced by the shared `add_wfdb.py` at 1024 and
are platform-neutral — no Android-specific data work.

---

## Caveat to carry over (same as Windows)

At 1024 counts/mV the nominal full scale is **±1 mV**. Real ECG R waves can exceed 1 mV (e.g. LVH,
some chest leads), mapping above domain 2048:
- The **WFDB converter does not clamp** → such signals are stored and rendered at true height
  (the renderer doesn't clamp either). Good.
- The **constructor element generator clamps at `AdcMax = 2048`** → a generated element above 1 mV
  flattens. Match Windows: keep the clamp, but be aware authored elements are capped at ±1 mV.

If Android currently clamps the *render* path or the *import* path at 2048, align it to Windows
(no clamp on import/render; clamp only on element generation).

---

## Verification

After the change, on Android open **Sinus Arrhythmia → lead II** and confirm:

| | counts above baseline | mV @ 1024 | large cells |
|---|---|---|---|
| R peak (raw ~1720) | 696 | 0.68 mV | ~1.4 |
| 1 mV calibration pulse | — | 1.00 mV | 2.0 (unchanged) |

The R wave should sit **below** twice the calibration-pulse height, and a known **1 mV WFDB import**
should render exactly **2 large cells** tall. Run the Android test suite; all amplitude/scale tests
green.

---

## Acceptance checklist
- [ ] Android domain `AdcCountsPerMv` (renderer + element generator) = **1024**.
- [ ] Android WFDB import/export domain constant = **1024**; gain read per-signal from header.
- [ ] No clamp added to render/import; element-generator clamp at 2048 retained.
- [ ] Android tests recomputed and passing.
- [ ] Visual check: sinar lead II R ≈ 1.4 cells; cal pulse = 2 cells; a 1 mV import = 2 cells.
- [ ] Bundled `.dat` data left untouched (interpretation-only change).
