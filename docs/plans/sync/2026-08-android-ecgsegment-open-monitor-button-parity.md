# Plan: Add "Open on Monitor" Button to `<ecgsegment>` Embeds (Android)

**Created:** 2026-08-27
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goal

In lecture rendering, a full `<ecg>` embed already shows an "open on monitor" button in the
course/Teaching viewer (absent in the constructor preview). A `<ecgsegment>` embed — a **single-lead,
windowed slice** of a pathology — did **not** offer that affordance.

**Windows change (already implemented — mirror it here):** `<ecgsegment>` figures now also render the
monitor affordance, gated identically to `<ecg>` (present only when a monitor button label is supplied by
the host, i.e. course/Teaching view; absent in the constructor preview). Because a segment is a single lead,
the Win control pre-selects **that one lead** with a one-column scheme when opening the monitor.

> **⭐ Design decision — NOT the full-width button.** A full-width filled button (what `<ecg>` uses) dwarfs a
> compact single-lead slice. So the segment gets a **small ~26px icon button anchored to the strip's
> top-right corner** (a pulse/ECG glyph, always visible), *not* the big button. On Win this is done by (a) a
> compact `CornerMonitorButtonHtml`, and (b) a `cornerAction` flag on `FigureHtml` that wraps the svg in a
> shrink-to-fit `position:relative` inline box so the absolutely-positioned button anchors to the **trace**
> (not the full-width figure), correct under any alignment. **Mirror this treatment on Android**, not a
> full-width button.

**Windows files changed (for reference):**
- `src/CardioSimulator.App/Rendering/EcgSvgRenderer.cs`
  - `SubstituteEcgSegmentTags(html, resolve, monitorButtonLabel = null)` — new optional label param.
  - `CornerMonitorButtonHtml(label, pathologyId, leads, scheme)` — **new** compact icon button
    (class `ecg-open-monitor ecg-open-monitor--corner`, inline pulse-glyph svg, `position:absolute` corner
    style, same `data-pathology`/`data-leads`/`data-scheme` as the big button so the bridge reads it identically).
  - `FigureHtml(..., bool cornerAction = false)` — when set (and there's a trace + action), wraps
    `<svg>` + button in `<span class="ecg-figure-overlay" style="position:relative;display:inline-block;max-width:100%">`
    so the corner button anchors to the trace. `<ecg>` keeps `cornerAction:false` → unchanged big button.
  - Segment path now builds `CornerMonitorButtonHtml(...)` and calls `FigureHtml(..., cornerAction: true)`.
- `src/CardioSimulator.App/Controls/LectureWebView.cs`
  - Passes `_monitorButtonLabel` into `SubstituteEcgSegmentTags` (previously called without it).

---

## 2. ⚠️ Platform divergence — read before implementing

The Windows and Android monitor buttons are **not** built the same way, and Android is currently the
*less* capable of the two even for `<ecg>`:

| Concern | Windows (`EcgSvgRenderer` / `LectureWebView.cs`) | Android (`EcgSvgRenderer.kt` / `LectureWebView.kt`) |
|---|---|---|
| Button markup | `<button class="ecg-open-monitor" data-pathology=… data-leads=… data-scheme=…>` | `<button class="monitor-btn" onclick="if(window.Android)Android.onMonitor()">Monitor</button>` |
| Bridge payload | JS posts `{type:"openMonitor", pathology, leads, scheme}` → host loads **that** pathology + lead layout | `Android.onMonitor()` takes **no arguments** |
| Host handler | `EcgOpenMonitorRequested(EcgMonitorRequest(pathology, leads, scheme))` | `onMonitorClick = { appViewModel.setShowMonitorOverlay(true) }` — generic overlay, no per-embed target |
| Gating | present when `monitorButtonLabel` is non-null (course view), empty in constructor | present when `showMonitorButton == (onMonitorClick != null)` |

**Consequence:** on Android, the `<ecg>` monitor button is already a *generic* "show the monitor overlay"
action — it does **not** pre-select a pathology or lead. So the honest, in-model parity for
`<ecgsegment>` is the **same generic button**, gated the same way as the existing Android `<ecg>` button.
Reproducing Win's single-lead pre-selection is a **separate, larger** change (§4) that would also have to
upgrade the `<ecg>` path and the `Android.onMonitor` bridge, and is **out of scope** for this plan unless
you decide to close the whole divergence at once.

---

## 3. Scope of this plan (minimal, in-model parity)

Add a **compact corner icon** monitor button to `<ecgsegment>` figures, gated exactly like the existing
Android `<ecg>` button. It calls the same generic `Android.onMonitor()` bridge (no bridge or host changes) —
only the *presentation* differs from the `<ecg>` big button, per the ⭐ design decision in §1.

### 3.1 `data/EcgSvgRenderer.kt`

**`substituteEcgSegmentTags`** — add a `showMonitorButton` flag and forward it (mirror `substituteEcgTags`,
which already has `showMonitorButton: Boolean = false` at line 61):

```kotlin
fun substituteEcgSegmentTags(
    html: String,
    showMonitorButton: Boolean = false,
    resolve: (pathologyId: String, lead: Lead, startSec: Float, durationSec: Float) -> EcgTrace?,
): String {
    var figureIndex = 0
    return ecgSegmentTag.replace(html) { match ->
        // …unchanged attribute parsing…
        if (trace == null) missingFigure(pathologyId, leadToken, id)
        else segmentFigureHtml(
            trace = trace,
            caption = caption,
            figureIndex = figureIndex++,
            startSec = start,
            tips = tips,
            showMonitorButton = showMonitorButton,   // NEW
            id = id
        )
    }
}
```

> Keep `showMonitorButton` **before** the trailing `resolve` lambda so call sites can still use Kotlin's
> trailing-lambda syntax — this matches how `substituteEcgTags` is ordered.

**`segmentFigureHtml`** — add the parameter and emit a **compact corner icon** (NOT the `.monitor-btn`
full-width button). The button is absolutely positioned, so wrap the existing `$svg` in a shrink-to-fit
relative box (`ecg-figure-overlay`) so it anchors to the trace, not the full-width figure — mirroring Win's
`FigureHtml(cornerAction:true)`. It still calls the same generic `Android.onMonitor()` bridge:

```kotlin
fun segmentFigureHtml(
    trace: EcgTrace,
    caption: String?,
    figureIndex: Int,
    startSec: Float,
    tips: List<com.example.cardiosimulator.domain.TipOverlay> = emptyList(),
    showMonitorButton: Boolean = false,   // NEW
    id: String? = null
): String {
    // …unchanged SVG build → `svg` …
    val cap = caption?.let { "\n  <figcaption>${escape(it)}</figcaption>" }.orEmpty()
    val idAttr = if (id.isNullOrEmpty()) "" else " id=\"${escape(id)}\""
    val body = if (showMonitorButton) {
        val btn = "<button type=\"button\" class=\"ecg-open-monitor--corner\" " +
            "title=\"Monitor\" aria-label=\"Monitor\" " +
            "onclick=\"if(window.Android)Android.onMonitor()\" " +
            "style=\"position:absolute;top:6px;right:6px;width:26px;height:26px;padding:0;line-height:0;" +
            "display:inline-flex;align-items:center;justify-content:center;border:1px solid #1976D2;" +
            "border-radius:6px;background:rgba(255,255,255,0.92);color:#1976D2;cursor:pointer\">" +
            "<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" " +
            "stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">" +
            "<path d=\"M3 12h4l2 5 4-10 2 5h6\"/></svg></button>"
        "\n  <span class=\"ecg-figure-overlay\" style=\"position:relative;display:inline-block;max-width:100%\">$svg$btn</span>"
    } else {
        "\n$svg"
    }
    return "<figure$idAttr class=\"ecg-figure ecg-segment-figure\">$body$cap\n</figure>"
}
```

> - The corner `<svg>` is an **inline pulse glyph** — Android's lecture WebView has no icon webfont, same as Win.
> - `#1976D2` is the shared monitor-blue used by Win's button; if Android already has a monitor accent token,
>   use it instead for theme consistency.
> - No `.monitor-btn` reuse here (that class is the full-width look). No new CSS file needed — all styling is inline.
> - When `showMonitorButton == false` the output is byte-for-byte the current figure (no wrapper), so the
>   constructor preview and existing golden tests are unaffected.

### 3.2 `ui/components/LectureWebView.kt`

At the `substituteEcgSegmentTags` call (currently lines 103–106), pass the same gate the `<ecg>` call uses
(`showMonitorButton = onMonitorClick != null`, line 100):

```kotlin
val body = EcgSvgRenderer.substituteEcgSegmentTags(
    withEcg,
    showMonitorButton = onMonitorClick != null,   // NEW — matches the <ecg> call above
    resolve = resolveEcgSegment
)
```

No change to `LectureBridge`, `onMonitor()`, `TeachingScreen`, or `CourseConstructorScreen`:
- Teaching view already passes `onMonitorClick = { appViewModel.setShowMonitorOverlay(true) }` → button shows.
- Constructor preview passes no `onMonitorClick` → `showMonitorButton == false` → button absent. ✔ parity.

### 3.3 Tests — `data/EcgSvgRendererTest.kt`

Add two cases:
- `substituteEcgSegmentTags(..., showMonitorButton = true)` output **contains** `ecg-open-monitor--corner`,
  `ecg-figure-overlay`, and `Android.onMonitor()` on a segment figure.
- With `showMonitorButton = false` (default) the segment figure **does not** contain `ecg-open-monitor--corner`
  or `ecg-figure-overlay` (output unchanged from before this plan).
- Resolve stub returns a non-empty single-lead `EcgTrace` so the real `segmentFigureHtml` path runs.

---

## 4. Optional follow-up (out of scope) — close the full divergence

Only if you want Android's monitor button (both `<ecg>` and `<ecgsegment>`) to pre-select the pathology/lead
like Windows does:

1. Change the emitted buttons to carry `data-pathology` / `data-leads` / `data-scheme` and call a new
   parameterized bridge method, e.g. `Android.onMonitorFor(pathology, leads, scheme)`.
2. Extend `LectureBridge` with `@JavascriptInterface fun onMonitorFor(...)` and widen `onMonitorClick`
   to `((pathology: String, leads: String, scheme: String) -> Unit)?`.
3. Host (`TeachingScreen`/`AppViewModel`) loads the specified pathology + lead layout onto the monitor
   overlay instead of just toggling it on.
4. For `<ecgsegment>`, pass the segment's single lead + one-column scheme (Win parity).

This is a behavioral upgrade to the whole monitor-button feature, not segment-only, so track it separately.

---

## 5. Acceptance criteria (this plan)

- [ ] A `<ecgsegment>` in a Teaching/course lecture renders a **small pulse-icon button in the strip's
      top-right corner** (not the full-width `<ecg>` button); clicking it opens the monitor overlay via
      `Android.onMonitor()`.
- [ ] The icon button stays anchored to the trace under left/center/right segment alignment (the
      `ecg-figure-overlay` relative wrapper), and does not float to the far edge of the text column.
- [ ] The same segment in the CourseConstructor **preview** shows **no** monitor button.
- [ ] No new CSS file is required (all button styling is inline); `<ecg>`'s existing big button is unchanged.
- [ ] Renderer unit tests cover button present/absent for segments.
- [ ] `./gradlew :app:assembleDebug` and `:app:testDebugUnitTest` pass.
