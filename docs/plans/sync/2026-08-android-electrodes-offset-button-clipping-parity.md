# Plan: Port Electrodes Dialog "Смещение" Button Clipping Fix to Android

**Created:** 2026-08-10
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goals

**Reported bug (Windows):** In the **«Электроды»** (Electrodes) window, the third state button
**«Смещение»** (Displacement) is clipped on its right edge — it does not fully fit on screen.

- **Expected:** the «Смещение» button fits on screen and is not clipped.
- **Actual:** the «Смещение» button is clipped.

### Root cause on Windows

`ElectrodesDialog.cs` lays the dialog body out as a 3-column `Grid` (left images `Auto` 240 / middle
legend+buttons `Star` / right body figure `Auto` 240) inside a `StackPanel` of `Width = 960`,
`Padding = 16`. The star (middle) column therefore has ≈ **408 px** available
(`960 − 32 padding − 240 − 240 − 40 column-spacing`).

The three state buttons («Все ок» | «Перепутаны» | «Смещение») sit in a **horizontal `StackPanel`**,
each button `MinWidth = 110`, `Spacing = 10`. The row's intrinsic minimum width is therefore
`3 × 110 + 2 × 10 = 350 px`. But the middle column was capped at **`MaxWidth = 340`** — *narrower than
the button row* — so the last button was clipped by ≈ 10 px. A horizontal `StackPanel` does not shrink
its children; it overflows and the container clip cuts the tail.

### Fix applied on Windows

Single change in `Win/src/CardioSimulator.App/Controls/ElectrodesDialog.cs`
(`BuildContent`): the middle `StackPanel`'s `MaxWidth` was raised **340 → 380**, which clears the
350 px button row (30 px headroom) while staying comfortably inside the ≈ 408 px star column, so no
other element shifts. A comment documents the constraint so it is not re-tightened.

```csharp
// MaxWidth must clear the state-button row's intrinsic width (3 × MinWidth 110 + 2 × Spacing
// 10 = 350) or its last button ("Смещение") gets clipped. The star column has ~408px here
// (960 content − 32 padding − 240 left − 240 right − 40 column spacing), so 380 fits with room.
var middle = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Top, MaxWidth = 380 };
```

**Goal for Android:** guarantee the third state button («Смещение» / "Displacement" /
"Desplazamiento") shows its full label and is never clipped, at every supported dialog width, font
scale, and locale.

---

## 2. Android layout — how it differs (read before editing)

Target file: `Android/app/src/main/java/com/example/cardiosimulator/ui/dialogs/ElectrodesDialog.kt`

The Compose layout is **not** a 1:1 of the Windows layout, so **do not blindly copy a `MaxWidth`
tweak** — it would not apply. Concretely:

- Body is a `Row` of three `Column`s with weights **left `1f` / middle `1.2f` / right `0.8f`**
  (`ElectrodesDialog.kt:79-159`). The middle column (legend + state buttons) gets `1.2 / 3.0 ≈ 40 %`
  of `Surface.fillMaxWidth(0.95f)` minus padding/spacing.
- The state buttons are a `Row(horizontalArrangement = spacedBy(8.dp))` with **three `StateButton`s,
  each `Modifier.weight(1f)`** (`ElectrodesDialog.kt:123-142`).
- `StateButton` has a **fixed `Modifier.height(36.dp)`** (`ElectrodesDialog.kt:229`), text
  `fontSize = 12.sp`, bold, centered, default `softWrap = true`.

**Consequence — the failure mode is different from Windows.** Because the Android buttons use
`weight(1f)`, they *shrink to share* the middle column and never horizontally overflow the way the
Windows fixed-`MinWidth` buttons did. Instead, when the middle column is narrow, each button becomes
too narrow for a long label; the `Text` wraps to a second line and the **fixed `height(36.dp)`
vertically clips it** (same user-visible symptom: the «Смещение» label is cut off).

**So this is a defensive parity fix, and its severity is width-dependent:**

- On a **tablet** (≈ 800 dp+ wide, the likely classroom target) the middle column is ≈ 300 dp, each
  button ≈ 95 dp, and «Смещение» (~55 dp @ 12 sp) fits on one line — likely **no visible bug**.
- On a **phone portrait** (~360 dp) the middle column is ≈ 110 dp and each button ≈ 30–35 dp —
  «Смещение», "Displacement", and especially Spanish **"Desplazamiento"** / **"Intercambiados"**
  cannot fit on one line and get wrapped-and-clipped.

**First step for the implementer:** reproduce on the target device/emulator width **before** changing
code — confirm whether and where the clip actually occurs (see §4). If it does not reproduce on the
real target, apply the minimal defensive fix (§3.1) anyway for locale/font-scale robustness and note
that it was not visibly reproducing.

---

## 3. Android fix

### 3.1 Primary (minimal, guarantees no clipping)

In `StateButton` (`ElectrodesDialog.kt:221-244`):

1. Replace the fixed `modifier.height(36.dp)` (line 229) with a **minimum** height:
   `modifier.heightIn(min = 36.dp)`. The button then grows to fit a wrapped label instead of clipping
   the second line.
2. Add inner vertical/horizontal padding to the centered content so wrapped text has breathing room,
   e.g. wrap the `Box`/`Text` content with `Modifier.padding(horizontal = 4.dp, vertical = 6.dp)`.

This alone makes the «Смещение» label **impossible to clip** at any width/locale (worst case it wraps
to two lines and the button grows). It is the must-have change and mirrors the Windows intent ("the
button always fully fits").

### 3.2 Recommended enhancement (keep one-line readability, match Windows appearance)

On the narrowest phone widths §3.1 lets long labels wrap to two lines, which is uglier than the
single-row segmented control on Windows. To keep labels on one line and full-width like Windows,
**also** give the button row more room. Pick one:

- **Option A — widen the button area (closest analog to the Windows `MaxWidth` bump):** increase the
  middle column weight (e.g. `weight(1.2f)` → `weight(1.6f)` at `ElectrodesDialog.kt:95`) and/or trim
  the side columns, so the three buttons are wider. Re-check the images still look right.
- **Option B — full-width button row (most robust; recommended if labels must never wrap):** move the
  three-button `Row` out of the narrow middle column so it spans the **full dialog width beneath** the
  three image/legend columns. Each button then gets ≈ 1/3 of the full dialog width — comfortably wide
  for every locale including Spanish. This departs slightly from the Windows column placement but best
  satisfies "fits on screen, not clipped" and is future-proof for long localizations.

**Recommendation:** ship §3.1 always; add §3.2 Option A if the tablet target still wraps, or Option B
if one-line labels are a hard requirement across phones and locales.

Do **not** solve this by hard-truncating with ellipsis — the requirement is that «Смещение» is fully
*visible*, so truncation would fail the acceptance criterion.

---

## 4. Verification

### 4.1 Manual flow

1. Open the monitor / teaching screen → control panel → **«Электроды»** (Electrodes) window.
2. Confirm the state-button row shows all three buttons **«Все ок» | «Перепутаны» | «Смещение»**
   with **full, un-clipped labels** and no clipped bottom edge.
3. Tap **«Смещение»** — it highlights blue, the V-lead legend group dims, and the caption updates.
   (Behavioral parity is unchanged by this fix; just confirm nothing regressed.)

### 4.2 Widths / locales to check (the whole point of the fix)

- **Widths:** phone portrait (~360 dp) **and** tablet (~800 dp+). Also enable large **font scale**
  (Settings → Display → Font size → Largest) on the phone width.
- **Locales:** at minimum **ru** («Смещение»), **en** ("Displacement"), and **es**
  ("Desplazamiento" / "Intercambiados" — the widest). Every state button label must be fully visible
  and un-clipped in all of them.

### 4.3 Definition of done

The «Смещение» (and every state) button label is fully visible — never clipped horizontally or
vertically — across the widths, font scales, and locales in §4.2, matching the fixed Windows behavior.

---

## 5. Notes

- Windows commit touches only `Win/src/CardioSimulator.App/Controls/ElectrodesDialog.cs`
  (`MaxWidth 340 → 380`). Nothing else changed; behavior/state logic is untouched.
- The Android string resources already exist and match (`electrodes_state_ok/swapped/displacement`
  in `values`, `values-ru`, `values-es`, `values-hi`, `values-zh`) — **no new strings needed**.
