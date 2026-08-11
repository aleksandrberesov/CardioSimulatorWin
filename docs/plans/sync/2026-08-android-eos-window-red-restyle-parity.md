# Plan — ЭОС window restyle: app-consistent alert red, no red text (Android parity)

**Created:** 2026-08-11
**Status:** NOT STARTED
**Owner:** a.beresov
**Direction:** **Windows → Android** (Windows is the reference implementation).

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

**Related / supersedes:** directly extends `docs/plans/completed/2026-07-android-eos-variant-rows-white-text-parity.md`
(which already made the inactive variant rows white). This plan finishes the job by fixing the **two red
cues that plan deliberately kept** — the α readout and the active-deviation pill — replacing the
bright standalone red with the app's shared alert red, and removing red as a **text** colour entirely.

---

## 1. Background & Goal

Customer feedback on the ЭОС ("electrical axis") window (RU):

> «Окно ЭОС не соответствует стилю приложения. … Красный текст плохо читается, стиль выбивается.»
> «общий стиль — синие цвета, всё ок. а красным выделяем, когда есть отклонения от нормы.»

I.e. **keep the blue panel** (the customer explicitly endorses it), but the **red text is unreadable**
and the window "clashes" with the rest of the app. Red should appear **only** to flag a deviation from
the norm — and as a *highlight*, not as coloured body text.

The Windows fix (already implemented, built 0 warn / 0 err) does exactly that:

1. **No red text anywhere.** The α readout (`α = N° — <band>`) is now **always white**; when the axis is
   a deviation it is highlighted by wrapping it in a **solid red pill** (white-on-red is readable; red
   text on the blue panel was not).
2. **One app-consistent red.** The bright standalone `#FF5A5A` / translucent `#77E53935` were replaced by
   the app's existing **electrode-fault alert red `#D33A2F`** at high opacity — so the EOS window shares
   the app's single "out of range" signal instead of inventing its own.
3. **Red = deviations only.** Normal / Horizontal / Vertical axes render fully in the blue style, white
   text, no red. Left / Right / Extreme deviation are the only place red appears (the active-row pill and
   the α-readout pill).
4. **Naming** aligned to the domain: `Warning`/`warning`/`IsWarning` → `Deviation`/`deviation`/`IsDeviation`.

Presentation-only. **No** domain / `EosAnalyzer` / `EosAxis` / string / unit-test changes.

### What Windows changed (`src/CardioSimulator.App/Controls/EosWindow.cs`) — the diff to mirror

**Colour constants** — dropped the red text brush, repointed the pill to the shared alert red:
```csharp
// before
private static readonly SolidColorBrush Warning     = new(...{ A=255,  R=0xFF, G=0x5A, B=0x5A }); // #FF5A5A red TEXT
private static readonly SolidColorBrush WarningFill  = new(...{ A=0x77, R=0xE5, G=0x39, B=0x35 }); // #77E53935 translucent pill
// after — single highlight brush, matches MonitorControlPanel's ElectrodeFaultFill (#D33A2F)
private static readonly SolidColorBrush DeviationFill = new(...{ A=0xF0, R=0xD3, G=0x3A, B=0x2F });
```

**α readout** (`Measured(...)`) — white text always; wrap in the red pill when it is a deviation:
```csharp
var angle = new TextBlock { /* text = "α = N° — <band>" */ Foreground = White, FontSize = 15,
                            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
if (IsDeviation(result.AxisClass))
    panel.Children.Add(new Border {
        Background = DeviationFill, CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 4, 0, 0), Child = angle });
else { angle.Margin = new Thickness(0, 2, 0, 0); panel.Children.Add(angle); }
```

**Variant row** (`Variant(...)`) — active-deviation pill now uses `DeviationFill` (param renamed `warning`→`deviation`).
**Helper** `IsWarning(...)` → `IsDeviation(...)`.

---

## 2. Current state (Android)

All in `app/src/main/java/com/example/cardiosimulator/ui/components/MonitorOverlays.kt` — this is the
**exact pre-fix state** Windows was in:

| Concern | Location | Current code | Note |
|---|---|---|---|
| Panel blue | `:41`, `:60` | `WindowsBlue = Color(0xFF5B9BD5)`, used `.copy(alpha = 0.85f)` | **Keep** — the endorsed blue. |
| Red text brush | `:42` | `EosWarning = Color(0xFFFF5A5A)` | Remove (no more red text). |
| Red pill brush | `:43` | `EosWarningPill = Color(0x77E53935)` | Replace with the shared alert red. |
| Deviation test | `:45` | `fun isWarning(c: EosAxisClass) = …` | Rename `isDeviation`. |
| **α readout (red text)** | `:186`–`:197` | `color = if (isWarning) EosWarning else Color.White` (Bold, 15.sp) | **The core fix** — white text + red pill. |
| Variant row | `:235`–`:267` | `textColor = Color.White` (already white ✓); `pillColor = if (warning) EosWarningPill else Color.White.copy(alpha = 0.25f)` (`:242`) | Repoint the pill; rename `warning`→`deviation`. |

**Shared alert red already exists on Android:** `ui/theme/Color.kt:7` → `val ElectrodeFaultRed = Color(0xFFD33A2F)`,
already imported and used by the electrode-fault tab in `ui/panels/MonitorControlPanel.kt:376`
(`activeColor = if (electrodeFault) ElectrodeFaultRed else AccentGreen`). Reuse it — the 1:1 analog of
the Windows `ElectrodeFaultFill` the Windows change reused.

---

## 3. Plan

### Phase 1 — Colours (`MonitorOverlays.kt` top, `:41`–`:46`)
- Add `import com.example.cardiosimulator.ui.theme.ElectrodeFaultRed`.
- Replace the two red constants with one deviation-highlight brush derived from the shared token:
  ```kotlin
  // Alert red for the abnormal (deviation) axes — reuses the app's electrode-fault red so the EOS
  // window shares the app's single "out of range" signal. Used ONLY as a solid highlight pill behind
  // white text, never as a text colour (saturated red text on the blue panel reads poorly).
  private val EosDeviationPill = ElectrodeFaultRed.copy(alpha = 0.94f)   // ≈ Windows A=0xF0 (#F0D33A2F)
  ```
  Remove `EosWarning` (`0xFFFF5A5A`) and `EosWarningPill` (`0x77E53935`) — both are now unused.
- Rename the helper `isWarning` → `isDeviation` (update its two call sites at `:186` and `:223`).

### Phase 2 — α readout: white text on a red pill (`:185`–`:197`)
Replace the red-text `Text` with an always-white `Text`, wrapped in a red pill when the axis deviates.
Mirror the Windows padding/rounding (pill: rounded 6.dp, padding h8/v4, top margin 4.dp; plain: top 4.dp):
```kotlin
val variantName = getVariantName(result.axisClass)
val angleText = stringResource(
    R.string.monitor_eos_angle_format, "%.0f".format(result.angleDeg), variantName)
if (isDeviation(result.axisClass)) {
    Box(
        modifier = Modifier
            .padding(top = 4.dp)
            .clip(RoundedCornerShape(6.dp))
            .background(EosDeviationPill)
            .padding(horizontal = 8.dp, vertical = 4.dp)
    ) {
        Text(text = angleText, color = Color.White, fontWeight = FontWeight.Bold, fontSize = 15.sp)
    }
} else {
    Text(
        text = angleText, color = Color.White, fontWeight = FontWeight.Bold, fontSize = 15.sp,
        modifier = Modifier.padding(top = 4.dp)
    )
}
```
(Text stays wrapping-friendly inside the measured card at `:135`, `Color.White.copy(alpha = 0.15f)`.)

### Phase 3 — Variant row pill + rename (`:235`–`:242`)
- Rename the param `warning` → `deviation` in `VariantRow(...)` and at the call site `:225`
  (`VariantRow(fullText, isActive, deviation = isDeviation(cls))`; the local at `:223` becomes
  `val deviation = isDeviation(cls)`).
- Repoint the pill to the shared red:
  ```kotlin
  val pillColor = if (deviation) EosDeviationPill else Color.White.copy(alpha = 0.25f)
  ```
- `textColor` stays `Color.White` (unchanged). Refresh the adjacent comment to the "deviation" wording.

### Non-goals (match Windows — do **not** touch)
- The **panel blue** (`WindowsBlue`) and its alpha — the customer approves it.
- The **diagram** vectors: the red vector `a` (lead I) is a teaching-legend colour on the white diagram
  card (red = a on I, green = b on aVF, blue = resultant) — readable, not a norm/deviation signal. Leave it.
- Domain / `EosAxis` / `EosAnalyzer` / strings (`monitor_eos_*`) / unit tests — no changes.
- The method (`(!)` info) flyout, the on-trace overlay, live-recompute, localization — untouched.

---

## 4. Verification

- `./gradlew :app:assembleDebug` (and `:app:testDebugUnitTest` — unchanged, still green).
- Confirm no unused-symbol lint: `EosWarning` / `EosWarningPill` are gone; `EosDeviationPill` +
  `ElectrodeFaultRed` import are referenced.
- Manual (emulator), Teaching → "All rhythms":
  - **Normal-axis rhythm** → EOS: all six variant rows white; the active (Normal/Horizontal/Vertical)
    row keeps the neutral translucent-white pill; the `α = …°` readout is **white, no pill**. No red anywhere.
  - **Deviation rhythm** (α outside the normal band, e.g. left/right axis deviation) → the α readout is
    **white text inside a red pill**, and the active deviation variant row shows the **same red** pill.
    The red is clearly readable and visibly matches the electrode-fault tab's red.
  - Compare the red to the **Electrodes** tab under a hookup fault — they should be the **same** `#D33A2F`.
- Language switch EN/RU/ZH/ES/HI → unchanged (no string edits); the ZH full-width `：` split still bolds
  the active variant name.

## 5. PR breakdown

| # | PR title | Phase | Notes |
|---|----------|-------|-------|
| 1 | EOS: app-consistent alert red, no red text | 1–3 | swap red constants→`ElectrodeFaultRed`; α readout white-text-on-pill; rename warning→deviation |

---

## Outcome

- **Result:** _pending_
- **PRs:** _n/a_
- **Deviations from plan:** _n/a_
- **Follow-ups spawned:** _n/a_
