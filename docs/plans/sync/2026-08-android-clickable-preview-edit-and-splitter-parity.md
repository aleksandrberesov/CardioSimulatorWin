# Plan: Port Clickable-Preview Edit + Resizable Editor/Preview Splitter to Android

**Created:** 2026-08-11
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goals

Customer feedback on the **CourseConstructor** screen (RU): *«Хотелось бы иметь возможность, чтобы само
окно отображения можно было корректировать. … нужно или чтобы было кликабельным либо ссылку на полное
ЭКГ.»* — the author wants the **preview** to be adjustable and its elements (an ECG in particular)
**clickable to jump straight into editing them**.

Today the constructor's live preview (`LectureWebView`) is **read-only**: to change a block the author
must hunt for it in the editor pane. The Windows fix makes the preview **click-to-edit** and adds a
**draggable splitter** between the editor and preview panes.

**Windows behavior (already implemented — mirror it):**
1. Clicking a rendered element in the preview opens **that element's editor**:
   - a **top-level ECG / ECG-segment** → its rich rhythm/leads (or range+tips) dialog;
   - a **nested** element (e.g. an ECG inside a card/section) → that element's in-place editor via the
     owning block's structure node (reusing the structure tree's "Edit…" flow);
   - any **other** block → scroll to + highlight + focus that block's editor card.
2. A **draggable splitter** between the editor and preview lets the author widen either pane.

**Key enabler:** the click must resolve a DOM element back to a block/element id. Two gaps had to be
closed: (a) rendered `<ecg>` **figures dropped their block id** during SVG substitution, and (b) the
click had to resolve the **nearest ancestor with an id at any depth** (nested content), then map that id
to a top-level block **or** a nested structure node.

**Windows commits/files for reference:**
- `src/CardioSimulator.App/Rendering/EcgSvgRenderer.cs` — propagate the source `<ecg id>` / `<ecgsegment id>` onto the rendered `<figure>` (and the "missing" figure).
- `src/CardioSimulator.Core/Domain/HtmlStructure.cs` — **new** `NodeById(html, id)` (find a nested element by id → its outline node/path).
- `src/CardioSimulator.App/Controls/LectureWebView.cs` — `EnableEditClicks` flag + `EditElementRequested` event + click-bridge JS/CSS.
- `src/CardioSimulator.App/Controls/HtmlBlockEditor.cs` — `EditElementById(elementId)` (top-level or nested dispatch) + card flash/focus.
- `src/CardioSimulator.App/Screens/CourseConstructorScreen.cs` — wire the click, ensure visual mode, add the splitter.

> **Android specifics:** DOM edits use **Jsoup** (easier than AngleSharp); the preview is an Android
> `WebView` inside an `AndroidView`, with a `@JavascriptInterface` bridge (`window.Android.*`); the UI is
> **Compose (stateless)**, so Windows's imperative `EditElementById(...)` becomes a **state-driven
> "auto-open" signal** threaded down to the relevant block composable, which opens its *existing* dialog.

---

## 2. Part A: Renderer — carry the block id onto ECG figures

**File:** `app/src/main/java/com/example/cardiosimulator/data/EcgSvgRenderer.kt`

Right now `substituteEcgTags` / `substituteEcgSegmentTags` read the `<ecg>` attrs but `figureHtml`
emits `<figure class="ecg-figure">` with **no id**, so the block id is lost and a click cannot resolve
to it. Mirror the Windows fix:

1. In `substituteEcgTags` read `val id = attrs["id"]` and pass it through to `figureHtml(...)` and to
   `missingFigure(...)`. Same in `substituteEcgSegmentTags` (its `<ecgsegment>` also has an id).
2. `figureHtml(...)`: add an optional `id: String? = null` param and prepend an id attribute:
   ```kotlin
   val idAttr = if (id.isNullOrEmpty()) "" else " id=\"${escape(id)}\""
   // both branches:
   return "<figure$idAttr class=\"ecg-figure\">\n$svg$monitorBtn$cap\n</figure>"
   ```
   Do the same for the segment figure (`<figure$idAttr class="ecg-figure ecg-segment-figure">`).
3. `missingFigure(pathologyId, leadToken, blockId: String? = null)`: add `id="…"` when `blockId` is set,
   so a **missing** embed is still clickable (the author can pick a valid rhythm).

> This also fixes editor→preview scroll-sync for ECGs on Android as a side benefit (`getElementById`
> now finds the figure).

**Note:** `<ecg>`/`<ecgsegment>` compiled by `HtmlCompiler.buildEcgTag`/`buildEcgSegmentTag` already
stamp `id="…"`, and a **nested** ECG inserted via the structure tree carries its own id, so its figure
becomes independently addressable.

---

## 3. Part B: Domain — resolve a nested element by id

**File:** `app/src/main/java/com/example/cardiosimulator/domain/HtmlStructure.kt`

Add a by-id lookup that descends through **non-container** elements too (so a nested `<ecg>` deep in a
card is reachable), returning the outline `Node` whose `path` addresses it for the existing surgical
edit methods. Mirror Windows `HtmlStructure.NodeById` / `FindById`:

```kotlin
fun nodeById(html: String, id: String): Node? {
    if (html.isBlank() || id.isEmpty()) return null
    val roots = parseAny(html).body().children()
    for (i in roots.indices) findById(roots[i], listOf(i), id)?.let { return it }
    return null
}

private fun findById(el: Element, path: List<Int>, id: String): Node? {
    if (el.id() == id) return buildNode(el, path)
    val kids = el.children()
    for (i in kids.indices) findById(kids[i], path + i, id)?.let { return it }
    return null
}
```

Because the path is computed against the **same body html** the edit methods consume, there is no
DOM-vs-source path mismatch — everything is done in the source.

---

## 4. Part C: Preview — the click-to-edit bridge

**File:** `app/src/main/java/com/example/cardiosimulator/ui/components/LectureWebView.kt`

1. Add a composable param `onEditElement: ((elementId: String) -> Unit)? = null`. Non-null turns on
   click-to-edit (the constructor passes it; read-only viewers leave it null). Compute
   `val editClicks = onEditElement != null` and thread it into `buildDocument` / `buildStandaloneDocument`
   (like `interactive`).
2. Extend the JS bridge. Add an `onEdit` callback to `LectureBridge`:
   ```kotlin
   @JavascriptInterface fun onEdit(id: String) { main.post { onEdit?.invoke(id) } }
   ```
   and pass `onEditElement` into the bridge constructor (`addJavascriptInterface(LectureBridge(onCellEdit, onMonitorClick, onEditElement), "Android")`).
3. Add the click JS (injected only when `editClicks`), resolving the **nearest ancestor with an id at
   any depth** and skipping interactive controls:
   ```js
   document.body.addEventListener('click', function(e){
     if (e.target.closest('input,textarea,select,button,a,label')) return;
     var el = e.target.closest('[id]');
     if (el && el.id && window.Android && Android.onEdit) Android.onEdit(el.id);
   }, true);
   ```
   Include it in **both** `buildDocument` and `buildStandaloneDocument` (Android chooses these by
   `lecture.isStandalone`).
4. Add a hover affordance CSS (only when `editClicks`) appended to `themeCss` output:
   ```css
   body>*{cursor:pointer}
   body>*:hover{outline:2px solid rgba(0,122,255,.35);outline-offset:2px;border-radius:4px}
   input,textarea,select,button,a{cursor:auto}
   ```

---

## 5. Part D: CourseConstructor screen — wiring + splitter

**File:** `app/src/main/java/com/example/cardiosimulator/ui/screens/CourseConstructorScreen.kt`

### 5.1 Wire the click
- Hold `var editRequestId by remember { mutableStateOf<String?>(null) }`.
- Pass to the preview: `onEditElement = { id -> editRequestId = id; if (viewMode == ConstructorViewMode.PREVIEW) viewMode = ConstructorViewMode.BOTH }` (click-to-edit needs the block editor visible; auto-reveal it).
- Pass into `HtmlBlockEditor` two new params: `editElementId = editRequestId` and `onEditHandled = { editRequestId = null }` (see Part E).

### 5.2 Draggable splitter
Replace the fixed `VerticalDivider()` (only shown in `BOTH`) with a draggable one that adjusts the split.
Compose parity of Windows's pixel-frozen column:
```kotlin
var splitFraction by remember { mutableStateOf(0.5f) }
var rowWidthPx by remember { mutableStateOf(0f) }
Row(Modifier.fillMaxWidth().weight(1f).onGloballyPositioned { rowWidthPx = it.size.width.toFloat() }) {
    if (showEditor) HtmlBlockEditor(/*…*/, modifier = Modifier.weight(splitFraction).fillMaxHeight())
    if (viewMode == ConstructorViewMode.BOTH) {
        Box(Modifier.fillMaxHeight().width(10.dp)
            .pointerHoverIcon(PointerIcon(/* horizontal-resize */))
            .pointerInput(Unit) {
                detectHorizontalDragGestures { _, dx ->
                    if (rowWidthPx > 0) splitFraction = (splitFraction + dx / rowWidthPx).coerceIn(0.2f, 0.8f)
                }
            }) { VerticalDivider(Modifier.align(Alignment.Center)) }
    }
    if (showPreview) Box(Modifier.weight(if (viewMode == ConstructorViewMode.BOTH) 1f - splitFraction else 1f).fillMaxHeight()) { LectureWebView(/*…*/) }
}
```
(Only apply the fractional weights in `BOTH` mode; single-pane modes keep `weight(1f)`.)

---

## 6. Part E: Block editor — auto-open the clicked element's editor

**File:** `app/src/main/java/com/example/cardiosimulator/ui/components/HtmlBlockEditor.kt`

Windows's imperative `EditElementById(elementId)` maps to a **Compose signal**. Add params
`editElementId: String? = null` and `onEditHandled: () -> Unit = {}`, and a `LaunchedEffect(editElementId)`
that resolves the id and routes it to the relevant block composable's **existing** dialog:

1. **Top-level block** (`blocks.firstOrNull { it.id == editElementId }`):
   - `HtmlBlock.Ecg` / `HtmlBlock.EcgSegment` → auto-open that block's picker. Cleanest: give `EcgEditor`
     / `EcgSegmentEditor` an `autoOpen: Boolean` (or an `openToken`) that flips their existing
     `showDialog` state on when set; call `onEditHandled()` once consumed.
   - Otherwise → `scrollToBlockId = editElementId` (already supported) + optional highlight; `onEditHandled()`.
2. **Nested element** (scan `blocks` for one whose body html contains the id):
   ```kotlin
   val owner = blocks.firstOrNull { HtmlStructure.nodeById(bodyHtmlOf(it) ?: "", editElementId) != null }
   ```
   where `bodyHtmlOf` returns the editable body for `Raw`/`Container`/`Card`/`Section`/`Note`/`Figure`
   (mirror Windows `BodyHtmlOf`). Then drive that block's `StructureEditor` to open its **Edit…** flow
   for `HtmlStructure.nodeById(body, id)` — i.e. set its `editTarget = node` (and `selectedPath`,
   scrolling to it). Thread an `autoEditNodeId: String?` into `RawEditor`/`StructureEditor` so it opens
   the same pre-filled ECG dialog (or raw-HTML editor) the long-press "Edit…" already produces. Call
   `onEditHandled()`.

> Because everything is resolved from the **source body html** (not the rendered DOM), the nested ECG's
> `<ecg id>` in source matches the clicked `<figure id>` in the preview (Part A), and the existing
> Jsoup path ops apply cleanly.

---

## 7. Part F: Verification

### 7.1 Unit tests (`app/src/test/java/.../`)
- `EcgSvgRenderer.substituteEcgTags` on `<ecg id="X" pathology="…">` → the rendered `<figure>` contains
  `id="X"`; a missing pathology still yields `<figure id="X" class="ecg-figure ecg-missing">`. Same for
  `<ecgsegment id="Y" …>`.
- `HtmlStructure.nodeById(html, id)` finds a **nested** `<ecg id="X">` inside a `<div class="lecture-card">`
  and returns a node whose `path` round-trips through `getOuterHtml`/`replaceElement` (edit the nested
  node without disturbing siblings); returns null for an absent id.

### 7.2 Manual (emulator)
1. CourseConstructor → open a lecture with an ECG (top-level **and** one nested inside a card/section).
2. **Preview pane:** hovering a block shows the outline affordance; **clicking a top-level ECG** opens the
   rhythm/leads dialog; editing + confirming re-renders the ECG in place (id preserved).
3. **Clicking a nested ECG** opens its editor via the structure tree (owning block scrolls/expands);
   **clicking a paragraph/heading** jumps to + focuses that block's editor card.
4. **Splitter:** drag the divider between panes — the editor/preview widths change and stay put; neither
   pane collapses past its minimum.
5. Read-only viewers (Teaching course view) are **unaffected** (no `onEditElement` passed → no click JS,
   no hover affordance).

### 7.3 Known limitation (parity with Windows)
A lecture authored as a **standalone full HTML document** whose elements carry **no ids** cannot resolve
clicks (nothing to address). App-authored `<ecg>`/components always have ids and work. If needed later,
add path-based resolution for the single standalone `Raw` block (its served DOM equals the source, so a
child-index path aligns with `HtmlStructure` paths).
