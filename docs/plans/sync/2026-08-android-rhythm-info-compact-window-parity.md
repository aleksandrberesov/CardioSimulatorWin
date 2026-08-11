# Plan: Port Rhythm-Info ("О ритме") Full-Page → Compact Window Fix to Android

**Created:** 2026-08-10
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goals

**Reported bug (Windows):** Opening **«О ритме»** (rhythm info) from the standalone "All rhythms"
monitor view is too bulky.

- **Expected:** a **compact information window**.
- **Actual:** a **whole page** opens (a full-monitor takeover).

### Root cause on Windows

`MonitorViewerOverlay.cs` showed the rhythm details in `_infoScreen` — an opaque `Grid` added to the
content grid with `Grid.SetColumnSpan(_infoScreen, 2)` and a solid `#FAFAFA` background, so when the
graduation-cap button was tapped it **covered the entire monitor** (monitor + drawer + floating
cards). The class comment itself called it *"a full-monitor takeover (not a small corner card)"*. Its
content used page-scale fonts (title 32, body 18/16) with `Padding(40, 32, 40, 32)`, reinforcing the
"whole page" feel.

### Fix applied on Windows

Single file: `Win/src/CardioSimulator.App/Controls/MonitorViewerOverlay.cs`. The full-screen takeover
was replaced with a **compact floating card**, reusing the same pattern the overlay already uses for
`_gridScaleCard` (and `MonitorView._measurementsCard`):

- `_infoScreen` (`Grid`, full-bleed, opaque) → `_infoCard` (`Border`): rounded (`CornerRadius 10`),
  semi-transparent white (`Argb(245,255,255,255)`), thin border, `MinWidth 240`, `MaxWidth 360`,
  anchored **top-right** (`HorizontalAlignment.Right`, `VerticalAlignment.Top`) with
  `Margin(12, 56, 12, 12)` so it floats **just under the info button** instead of filling the pane.
- The details scroll inside the card via a `ScrollViewer` with **`MaxHeight = 360`**, so long
  descriptions scroll rather than growing the card.
- Header is a compact title (font 15) + a small borderless close button; a 1-px divider separates it
  from the body.
- `BuildInfoContent` font sizes were reduced to card scale: primary title **32 → 18**, everything else
  **18/16 → 13**; top margins **16 → 10**.
- `card.PointerPressed` is marked handled so taps on the card don't fall through to the monitor's
  pan/zoom behind it.
- Renames for clarity: `_infoScreen`→`_infoCard`, `BuildInfoScreen`→`BuildInfoCard`,
  `ShowInfoScreen`→`ShowInfoCard` (call sites in the `_info.Click` handler, `SetCloseButtonVisible`,
  and `OnAppChanged` updated). Behaviour (what data is shown, language re-render, mode-switch cleanup)
  is otherwise unchanged.

**Goal for Android:** the same «О ритме» details open as a **compact card anchored at the monitor's
top-right, under the graduation-cap button**, not as a full-screen overlay — content, close button,
and language handling unchanged.

---

## 2. Android layout — how it maps (read before editing)

Target file: `Android/app/src/main/java/com/example/cardiosimulator/ui/screens/TeachingScreen.kt`

The Android code is an almost 1:1 port and has the **same bug**. Two relevant sites:

- **Trigger + host** (`TeachingScreen.kt` ~L559-584): inside the monitor `Box`, the graduation-cap
  `IconButton` is `Modifier.align(Alignment.TopEnd).padding(top = 8.dp, end = 8.dp)`, and directly
  below it:

  ```kotlin
  // Full-monitor rhythm-info screen — opaque overlay filling the whole monitor Box. […]
  if (showRhythmInfo && onClose == null) {
      RhythmInfoScreen(
          pathology = selectedRhythm,
          significantPoints = significantPoints,
          language = selectedLanguage,
          description = description,
          onClose = { showRhythmInfo = false },
          modifier = Modifier.fillMaxSize()   // <-- this is the "whole page" takeover
      )
  }
  ```

- **The composable** `RhythmInfoScreen` (`TeachingScreen.kt` ~L680-764): a `Surface` with the passed
  `modifier` (`fillMaxSize`), opaque `background` color + `tonalElevation = 8.dp`, a 56.dp header row
  (title + close), then a `Column(...).verticalScroll(...).padding(40.dp)` of page-scale typography
  (`headlineMedium` title, `bodyLarge` body). This is the direct analog of the Windows `_infoScreen`.

Because it's called with `Modifier.fillMaxSize()` inside the monitor `Box`, it covers the whole
monitor — identical symptom to Windows.

---

## 3. Android fix

Convert `RhythmInfoScreen` from a full-size opaque `Surface` into a compact card, and anchor it at the
monitor `Box`'s top-right under the graduation-cap button. Two coordinated edits:

### 3.1 Call site — anchor + size the card (`TeachingScreen.kt` ~L575-584)

Replace `modifier = Modifier.fillMaxSize()` with a top-right-anchored, width-capped modifier. It's
inside a `BoxScope`, so `Modifier.align` is available:

```kotlin
if (showRhythmInfo && onClose == null) {
    RhythmInfoScreen(
        pathology = selectedRhythm,
        significantPoints = significantPoints,
        language = selectedLanguage,
        description = description,
        onClose = { showRhythmInfo = false },
        modifier = Modifier
            .align(Alignment.TopEnd)
            // Sit under the graduation-cap button (top ≈ 56.dp) at the top-right corner, mirroring
            // the Windows _infoCard Margin(12, 56, 12, 12).
            .padding(top = 56.dp, end = 12.dp, bottom = 12.dp)
            .widthIn(min = 240.dp, max = 360.dp)
    )
}
```

### 3.2 The composable — make it a compact card (`RhythmInfoScreen`, ~L680-764)

1. **Container:** change the outer `Surface` from a full-bleed background to a **card**:
   `shape = RoundedCornerShape(10.dp)`, `tonalElevation = 8.dp` (keep) + a small `shadowElevation`
   (e.g. `6.dp`), `color = MaterialTheme.colorScheme.surface` (opaque card surface is fine — the point
   is it's *small*, not translucent). Remove `Modifier.fillMaxSize()` on the inner `Column`; let it
   `wrapContentHeight()`/`width` within the `widthIn` from §3.1.
2. **Cap the scroll height** so the card stays compact: the details `Column` gets
   `Modifier.heightIn(max = 360.dp).verticalScroll(rememberScrollState())` and reduced padding
   (`padding(16.dp)` instead of `40.dp`), `verticalArrangement = spacedBy(6.dp)`.
3. **Compact typography** (mirror the Windows font reductions):
   - Header title: `titleMedium` → keep, but the 56.dp header row can shrink to a lighter header
     (title + close `IconButton`) with `padding(start = 16.dp, end = 8.dp)`.
   - Primary name: `headlineMedium` → **`titleLarge`** (or `titleMedium`).
   - Secondary name: `titleMedium` → **`bodyMedium`**.
   - Leads / markers: `bodyLarge` → **`bodyMedium`**.
   - Description label: keep `titleMedium` SemiBold; description text `bodyLarge` → **`bodyMedium`**.
   - Reduce the `Spacer(16.dp)` before leads/description to `~10.dp`.
4. **Swallow taps** on the card so a tap doesn't reach the monitor's pinch/pan gesture layer behind
   it — put a no-op `clickable` (no ripple) or `pointerInput { detectTapGestures {} }` on the card
   `Surface`, matching the Windows `card.PointerPressed = Handled`.

Keep everything else in `RhythmInfoScreen` (the pathology/marker/description logic, the null-pathology
fallback, string resources) exactly as-is — only its container size, padding, and font styles change.

---

## 4. Verification

### 4.1 Manual flow

1. Teaching mode → standalone **"All rhythms"** monitor view (no course open, not compare mode).
2. Tap the **graduation-cap** button (top-right).
3. Confirm a **compact card** appears anchored at the top-right, under the button — showing the rhythm
   name(s), leads count, markers, and description — **not** a full-screen page. The monitor trace,
   rhythm drawer, and any floating cards remain visible around it.
4. Tap **close (×)** in the card header → card dismisses, monitor unaffected.
5. Select a different rhythm, reopen → content reflects the new rhythm. Switch language (RU/EN) with
   the card open → title and fields re-render localized.

### 4.2 Sizes / locales / content lengths to check

- **Widths:** phone portrait (~360 dp) **and** tablet (~800 dp+). The card must stay ≤ 360 dp wide and
  not cover the whole monitor on either.
- **Long description:** pick a rhythm with a long description — confirm the card **scrolls internally**
  (capped ~360 dp tall) instead of growing off-screen.
- **Locales:** ru / en at minimum; confirm the header title («Информация о ритме») and labels fit.
- **Overlap:** confirm the card doesn't collide with any other top-right floating element (e.g. the
  values/measurements readout or tips preview) — the `top = 56.dp` offset keeps it below the cap
  button, matching Windows.

### 4.3 Definition of done

Opening «О ритме» on Android shows a compact, dismissable, internally-scrolling info card at the
monitor's top-right — never a full-screen takeover — across the widths, locales, and description
lengths in §4.2, matching the fixed Windows behavior.

---

## 5. Notes

- Windows change touches only `Win/src/CardioSimulator.App/Controls/MonitorViewerOverlay.cs`
  (`_infoScreen`/`BuildInfoScreen`/`ShowInfoScreen` → `_infoCard`/`BuildInfoCard`/`ShowInfoCard`, plus
  the `BuildInfoContent` font reductions). No view-model, data, or string changes.
- **No new string resources needed** — `rhythm_info_title`, `rhythm_info_tooltip`, `cd_close`,
  `pathology_leads_label`, `pathology_markers_label`, `pathology_description_label`, `mode_teaching`
  all already exist on Android and are unchanged.
- Only the **container/size/typography** change; the details logic (name, leads, distinct markers in
  complex/ordinal order, description, null fallback) stays identical, so no behavioral parity risk.
- The Android composable keeps the name `RhythmInfoScreen`; consider renaming to `RhythmInfoCard` for
  parity with the Windows `_infoCard` rename (optional, cosmetic).
