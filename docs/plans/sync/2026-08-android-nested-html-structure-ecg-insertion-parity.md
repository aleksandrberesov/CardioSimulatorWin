# Plan: Port Nested-HTML Structure Navigation & ECG Insertion to Android

**Created:** 2026-08-05
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goals

In the CourseConstructor's visual ("block") editor, pasting arbitrary HTML from an unknown source
(e.g. `Docs/…/3.1. Нарушения автоматизма синоатриального узла.html`) produces only **top-level** blocks.
Any element the editor doesn't recognize (a `<div class="section-block">` wrapper holding `<svg>`/`<path>`,
etc.) collapses into **one opaque block** showing raw markup, with no way to drill into the nested tree.
So an author **cannot target a deeply nested element** — e.g. replace a hand-drawn ECG sketch
`<path d="M280,85 L290,65 …"/>` (nested `<div> › <div> › <svg>`) with a real `<ecg>` reference from the
rhythm dataset.

**Root cause (shared by both platforms).** `HtmlCompiler.parse` walks only the body's top-level elements;
the `else` branch wraps unrecognized markup in a single `Paragraph(outerHtml)`, and `compile` re-emits it
as `<p id="…">…</p>` — which is invalid around block-level markup and **corrupts the nested structure** on
any visual-mode edit.

**Windows fix (already implemented — mirror it here):**
1. New opaque block type `HtmlBlock.Raw` that round-trips **verbatim** (no `<p>` wrapper).
2. `parse` keeps a full pasted document as a single `Raw` block and turns unknown/nested elements into `Raw`.
3. A new `HtmlStructure` helper reads a `Raw` block's inner DOM as a navigable tree and performs **surgical**
   edits (replace one element, or insert markup before/after it) — leaving the rest of the document intact.
4. The visual editor renders a `Raw` block as a **structure tree**; each node offers **Replace with ECG /
   Insert ECG before / after**, reusing the rhythm/leads/scheme/caption picker.

**Desired Android outcome:** identical behavior — expand the nested DOM of a pasted block, pick any element
(e.g. the decorative `<svg>` or its `<path>`), and replace it with / insert an `<ecg>` tag, with the rest of
the markup (including a full document's `<head>`/CSS) preserved.

**Windows commits/files for reference:**
- `src/CardioSimulator.Core/Domain/HtmlBlock.cs` — added `Raw` record.
- `src/CardioSimulator.Core/Domain/HtmlCompiler.cs` — full-doc detection, `Raw` parse/compile, `BuildEcgTag`, `EnsureRootId`.
- `src/CardioSimulator.Core/Domain/HtmlStructure.cs` — **new** outline + surgical edit helper (AngleSharp).
- `src/CardioSimulator.App/Controls/HtmlBlockEditor.cs` — `BuildRawEditor` (structure tree) + `PickEcgAsync`.
- `tests/CardioSimulator.Core.Tests/HtmlCompilerTests.cs`, `HtmlStructureTests.cs` — tests.

> **Android uses Jsoup**, which makes the DOM edits *easier* than AngleSharp: `Element.replaceWith`,
> `Element.before(html)`, `Element.after(html)`, `Element.children()`, `Document.outerHtml()`.

---

## 2. Part A: Domain — `HtmlBlock.Raw` + `HtmlCompiler`

**File:** `app/src/main/java/com/example/cardiosimulator/domain/HtmlBlock.kt`

Add a verbatim opaque block to the sealed interface (mirror the other `data class` members, including the
`id` with a generated default):
```kotlin
data class Raw(
    val id: String = java.util.UUID.randomUUID().toString().replace("-", ""),
    val html: String,
) : HtmlBlock
```

**File:** `app/src/main/java/com/example/cardiosimulator/domain/HtmlCompiler.kt`

1. **`parse`** — before `Jsoup.parseBodyFragment`, short-circuit a full document to a single `Raw` block:
   ```kotlin
   fun isFullDocument(html: String): Boolean {
       val t = html.trimStart()
       return t.startsWith("<!doctype", true) || t.startsWith("<html", true)
   }
   // at top of parse():
   if (isFullDocument(html)) return listOf(HtmlBlock.Raw(html = html))
   ```
   Then change **both** `else -> … Paragraph(element.outerHtml())` branches (the `<figure>`-fallback around
   line 59 and the terminal `else` around line 101) to build `HtmlBlock.Raw(html = element.outerHtml())`
   (carry `elementId` into `Raw(id = …)` when present, same as the existing paragraph branches).

2. **`compile`** — the `when (block)` is exhaustive over the sealed interface, so the Kotlin compiler will
   force you to add the branch:
   ```kotlin
   is HtmlBlock.Raw -> append(ensureRootId(block.html, block.id)).append("\n")
   ```
   Extract the existing `is HtmlBlock.Ecg` body into a reusable `buildEcgTag(block)` (identical string) so the
   editor's insert action and `compile` emit the same markup.

3. **`ensureRootId`** — stamp the block id onto the root element's opening tag only when it has none; return a
   full document untouched (so standalone docs stay byte-verbatim). String-only (no reparse) to preserve the
   author's exact nested markup:
   ```kotlin
   private fun ensureRootId(html: String, id: String): String {
       if (isFullDocument(html)) return html
       val open = Regex("""<\s*[A-Za-z][\w:-]*""").find(html) ?: return html
       val gt = html.indexOf('>', open.range.first); if (gt < 0) return html
       if (Regex("""\sid\s*=""", RegexOption.IGNORE_CASE).containsMatchIn(html.substring(open.range.first, gt))) return html
       return StringBuilder(html).insert(open.range.last + 1, " id=\"$id\"").toString()
   }
   ```

---

## 3. Part B: Domain — new `HtmlStructure` helper (Jsoup)

**New file:** `app/src/main/java/com/example/cardiosimulator/domain/HtmlStructure.kt`

Mirror `HtmlStructure.cs`. This is a **component-oriented** outline, not a raw tag dump: each node is
classified into a recognizable kind (Heading / Text / Math / Image / Ecg / Table / List / Diagram /
Container) with a friendly label + short preview, and **leaf components are not expanded** (an `<svg>` shows
as one "Diagram" node; its shapes are hidden). Parse is document-aware; outline roots are the body's element
children (a full document's `<head>` is preserved on serialize but not shown). `path` is the sequence of
child-element indices from the root; the same traversal is used for outline and edits so paths stay consistent.

```kotlin
object HtmlStructure {
    enum class Kind { Container, Heading, Text, Math, Image, Ecg, Table, List, Diagram, Other }

    data class Node(
        val tag: String, val id: String?, val className: String?,
        val kind: Kind, val label: String, val preview: String?,
        val path: List<Int>, val children: List<Node>,
    )

    fun outline(html: String): List<Node> { /* roots = body.children(); recurse (see build) */ }

    fun replaceElement(html: String, path: List<Int>, replacement: String): String {
        val doc = parseAny(html)
        val target = navigate(doc.body().children(), path) ?: return html
        target.after(replacement); target.remove()          // Jsoup: swap in place
        return serialize(doc, html)
    }

    fun insertAdjacent(html: String, path: List<Int>, fragment: String, after: Boolean): String {
        val doc = parseAny(html)
        val target = navigate(doc.body().children(), path) ?: return html
        if (after) target.after(fragment) else target.before(fragment)
        return serialize(doc, html)
    }

    fun appendChild(html: String, path: List<Int>, fragment: String): String {   // add as last child
        val doc = parseAny(html)
        val target = navigate(doc.body().children(), path) ?: return html
        target.append(fragment)
        return serialize(doc, html)
    }

    private fun parseAny(html: String) =
        if (HtmlCompiler.isFullDocument(html)) Jsoup.parse(html) else Jsoup.parseBodyFragment(html)

    // full document → doc.outerHtml() (head intact); fragment → doc.body().html()
    private fun serialize(doc: Document, original: String) =
        if (HtmlCompiler.isFullDocument(original)) doc.outerHtml() else doc.body().html()
}
```
- **Classify** by tag: `h1..6`→Heading, `p`→(Math if `$$…$$` else Text), `img`/`figure`→Image, `ecg`→Ecg,
  `table`→Table, `ul`/`ol`→List, `svg`→Diagram; a `div`/`section`/… → **Container** iff it has a block-level
  element child (see `BlockTags`), else Text (if it has text) else Other.
- **Build children only for Containers**, and only for **block-level** children (inline tags like `<strong>`,
  `<span>`, `<br>` fold into the parent's text preview) — but keep the *real* child index in `path` so edits
  stay aligned with the DOM. Leaf kinds (svg, table, list, heading, text, image, ecg) get **no** child nodes.
- **Labels** are friendly and class-inferred: heading → "Heading N"; container → Card / Section / Figure /
  Header (by class keyword) else "Group"; text → Title / Subtitle / Caption / Note / Badge / Breadcrumb (by
  class keyword) else "Text"; table → "Table R×C"; list → "List · N items"; svg → "Diagram (SVG)".
- **Preview**: heading/text → truncated text; image → alt/src; ecg → pathology; diagram → viewBox or W×H.
- `navigate(children, path)` walks `Element.children()` by index (null on stale path).
- Set `doc.outputSettings().prettyPrint(false)` in `parseAny` so serialize doesn't reflow the document.

---

## 4. Part C: UI — structure tree + ECG actions in `HtmlBlockEditor`

**File:** `app/src/main/java/com/example/cardiosimulator/ui/components/HtmlBlockEditor.kt`

1. Add the exhaustive `when (block)` branch (line ~76):
   ```kotlin
   is HtmlBlock.Raw -> RawEditor(appViewModel, rhythms, block) { onUpdateBlock(block.id, it) }
   ```

2. **`RawEditor`** composable: an expandable **component tree** from `HtmlStructure.outline(block.html)` — an
   indented `Column` where each row shows a **kind colour dot + friendly label + dimmed preview** (e.g.
   🟧 *Diagram (SVG) · 700×180*, 🟥 *ECG · sinus*, ⬜ *Section*, 🟦 *Heading 2 · «…»*) with per-node
   expand/collapse (auto-expand the top ~2 levels). Rows are **clickable and highlighted**: hover + a
   click-to-select background (one selected row at a time), e.g. `Modifier.combinedClickable(onClick = { select },
   onLongClick = { openMenu })` with a `background()` driven by hover/selected state. The actions are a
   **long-press / right-click context menu** on the row, **each submenu listing every app component**
   (Heading / Text / Math / ECG / Image / Table), **not** a control on every row. **Order matters (data-loss
   guard):** Insert is the safe primary action and comes first — *Insert inside ▸* (containers only, appends as
   last child), *Insert before ▸*, *Insert after ▸* — then a divider, then *Replace with ▸* last. Replacing a
   **container** must **confirm first** (it discards everything inside; a mis-clicked "Replace with ▸ Text" on
   the page root would otherwise erase the whole lecture). Also include a collapsed raw-HTML `OutlinedTextField`
   for power users (updates via `onUpdate(block.copy(html = it))`).

3. **Insert any component into the HTML block.** Each menu leaf configures a component and applies its markup
   at the node. Parity with Win's `HtmlBlockEditor.ApplyComponentAsync` / `BuildComponentMarkupAsync`:
   - ECG reuses the inline rhythm picker (embed the rhythm list; don't stack modals) → `HtmlCompiler.buildEcgTag`.
   - Heading / Text / Math / Image / Table use small dialogs that build the corresponding `HtmlBlock` and
     compile it with **`HtmlCompiler.compile(listOf(block))`** — so an inserted component is byte-identical to
     the same top-level block.
   Then apply:
   ```kotlin
   val markup = buildComponentMarkup(kind) ?: return  // dialog cancelled / empty
   val newHtml = when (placement) {
       Replace -> HtmlStructure.replaceElement(block.html, node.path, markup)
       Inside  -> HtmlStructure.appendChild(block.html, node.path, markup)   // add as last child of a container
       Before  -> HtmlStructure.insertAdjacent(block.html, node.path, markup, after = false)
       After   -> HtmlStructure.insertAdjacent(block.html, node.path, markup, after = true)
   }
   if (newHtml != block.html) onUpdate(block.copy(html = newHtml))
   ```
   Because `Raw` compiles verbatim (Part A), `compile(blocks)` reproduces the whole body/document with only the
   targeted node changed — no `<head>`/CSS loss, no `<p>` re-wrapping.

4. **Preview scroll-to-element on select.** When a tree row is selected, scroll the lecture `WebView` to the
   corresponding element by **child-element index path**, walked from `document.body` (nested elements have no
   id). Since every block compiles to exactly one top-level element, a fragment block's root is
   `body.children[blockIndex]`; drop the node's local root index (always 0) and prepend `blockIndex`. A
   standalone document is a single block whose paths already index `body.children`, so use the path as-is.
   Run via `webView.evaluateJavascript`:
   ```js
   (function(){var el=document.body;if(!el)return;var idx=/*indices*/;
     for(var k=0;k<idx.length;k++){if(!el.children||idx[k]>=el.children.length){el=null;break;}el=el.children[idx[k]];}
     if(el)el.scrollIntoView({behavior:'smooth',block:'center'});})();
   ```
   (Best-effort: `.children` skips text/comment nodes on both sides, and `<ecg>`→`<figure>` is 1:1, so indices
   line up with the source DOM.)

5. **Renderer: decide standalone vs template by CONTENT, not the flag.** For the element scroll (Part 4) to
   line up, the preview DOM must match `HtmlStructure.outline`. If a full `<!doctype…>` page is wrapped in
   another `<body>` (the fragment path), the browser **hoists the inner `<head>` (meta/title/style) into the
   body**, shifting every top-level element (e.g. `card` moves from index 0 to 3) and breaking the walk. So in
   the Android lecture renderer (parity with Win's `LectureWebView.RenderAsync`), serve the page verbatim when
   `HtmlCompiler.isFullDocument(lecture.rawHtml)` (content-driven — a still-whole page renders standalone; a
   decomposed page, see §6, is a fragment and renders inside the app template). (The inserted `<ecg>` needs no
   renderer change — the existing ECG-substitution handles it.)

---

## 4b. Part E: Combine an all-in-one page with app components (decompose)

**Problem.** A lecture has two shapes that don't compose: an all-in-one page is a **whole document**
(`<!doctype…></html>` with its own `<head>`/CSS), while app components (`HtmlCompiler.compile`) are a **body
fragment**. Appending a component to a full-page block puts its markup *after* `</html>` — it renders unstyled,
outside the page. Fix: when the author combines the two, **decompose the page into a composable fragment** so
everything is one block sequence rendered by the app template.

**`HtmlCompiler.embedDocument(fullDoc): String`** (mirror Win's `HtmlCompiler.EmbedDocument`, Jsoup):
- Collect every `<style>` (head + body); **scope** its rules under `.lecture-embed` — `html`/`body`/`:root`
  selectors become `.lecture-embed`, `*` becomes `.lecture-embed *`, everything else is prefixed
  `.lecture-embed `; recurse into `@media`/`@supports`/`@container`, leave `@keyframes`/`@font-face` intact;
  drop viewport-height (`height/min-height: …vh`) so the embed doesn't reserve a full screen.
- Remove `<script>`/`<style>` from the body (don't run page scripts in the app document).
- Return `<div class="lecture-embed"><style>{scoped}</style>{body.html()}</div>` — a fragment
  (`isFullDocument` = false).
- Write a small brace-aware CSS scoper (`scopeCss`) with a top-level comma split that ignores `()`/`[]`
  (for `:not(a,b)`, attribute selectors).

**Wire-up:**
- `CourseConstructor` "Add component" (parity with Win's `HtmlBlockEditor.EnsureComposable`): **before** appending
  a component, replace any full-document `HtmlBlock.Raw` with `Raw(embedDocument(html))`. So the first component
  the author adds converts the page to an embedded-page block; components become proper siblings. A pure page
  (no components added) stays whole and renders standalone — decomposition is lazy.
- `HtmlStructure` label: a `.lecture-embed` container reads as **"Embedded page"** in the tree.
- **Keep the `layout: standalone` flag truthful.** Since rendering is content-driven (§5), the persisted flag is
  just metadata — but it must not go stale. Add `Lecture.withReconciledLayout()` (Core): set the flag while
  `rawHtml` is a full document, clear it once it's a fragment; return `this` when already consistent. The
  authoring view-model applies it wherever `rawHtml` changes (parity with Win's `SetHtml`/`ImportFullPage`,
  both routed through `(lecture.copy(rawHtml=…)).withReconciledLayout()`), so a decomposed page no longer keeps
  a stale `standalone` flag.

---

## 4c. Part F: Structural components palette (Card / Section / List / Note / Quote / Figure / Divider)

Extend the insertable palette beyond the typed blocks with the **structural elements the pages are made of**.

**`HtmlComponents` (new, Core — pure/testable; mirror Win's `HtmlComponents.kt`):** static builders returning
HTML fragments (text fields accept "simple HTML", not escaped, matching the typed-block editors):
- `list(items, numbered)` → `<ul|ol class="lecture-list"><li>…`
- `card(title?, body)` → `<div class="lecture-card">[<div class="lecture-card-title">…]<div class="lecture-card-body">…`
- `section(title?, body)` → `<section class="lecture-section">[<h3 class="lecture-section-title">…]…`
- `note(variant, body)` → `<div class="lecture-note lecture-note-{info|tip|warning|important}">…`
- `quote(body, cite?)` → `<blockquote class="lecture-quote">…[<cite>…]`
- `figure(body, caption?)` → `<figure class="lecture-figure"><div class="lecture-figure-body">…[<figcaption>…]`
- `divider()` → `<hr class="lecture-divider">`
- `Css` const: the styles for all of the above.

**Renderer:** inject `HtmlComponents.Css` into the lecture document in **both** paths (fragment template AND
standalone page head), so components render right whether inside an embedded page or a still-whole page.

**Editor:** add these to the component palette (`ComponentKind` + the ordered list) with small config dialogs
(list items one-per-line + numbered toggle; card/section title+body; note style dropdown; quote body+cite;
figure body+caption; divider takes no input). **Build the context menu lazily** (on right-click / long-press,
e.g. Compose: build the dropdown only when the menu opens) — the palette now has 13 kinds × 4 placements, too
many to materialize for every tree row.

---

## 4d. Part G: Structural components as first-class BLOCKS (Raw only for unrecognized tags)

Make the structural components first-class `HtmlBlock` types (not just insertable snippets), add them to the top
**Add** palette, and let `Parse` recognize them — so `Raw` is used **only for genuinely unrecognized markup**.

**`HtmlBlock`:** add `List(items, numbered)`, `Quote(html)`, `Note(variant, html)`, `Card(title, html)`,
`Section(title, html)`, `Figure(html, caption)`, `Divider`.

**`HtmlCompiler.parse`** — recognize into these blocks (keep `Raw` as the fallback):
- Semantic leaf tags generically: `ul`/`ol` → List, `blockquote` → Quote, `hr` → Divider.
- App-authored containers **by their `lecture-*` class only** (so a page's own `div.card`/`div.section-block`
  stay `Raw`, preserving their exact markup, custom classes/styles, and the structure tree): `div.lecture-card`
  → Card (read `.lecture-card-title`/`.lecture-card-body`), `div.lecture-note` → Note (variant from the
  `lecture-note-*` class), `section.lecture-section` → Section, `figure.lecture-figure` → Figure. (Existing
  `figure.img-figure` → Image stays.)

**`HtmlCompiler.compile`** — emit each via `HtmlComponents.*`, stamped with the block id (`ensureRootId`), so it
round-trips back to the same typed block.

**Editor:** a `when(block)` branch + a small editor card per type (list: items one-per-line + numbered; quote:
body; note: variant + body; card/section: title + body; figure: body + caption; divider: none), and **Add-bar
buttons** for all of them (make the Add bar horizontally scrollable — it's now ~13 buttons).

**Why app-class-only for containers:** typing a foreign `<div class="section-block" style="…">` into a `Card`
block and re-emitting it as `<div class="lecture-card">` would drop its class/inline styles and its navigable
tree. So only the app's own `lecture-*` markup (which round-trips exactly) is recognized; everything else is
`Raw`.

---

## 4e. Part H: Nesting via the palette — generalized structure tree + a Container block

Make the structure tree work on **any** block that owns an HTML body, and add a neutral **Container** block so
an author can build nested structure from scratch (palette-only, no pasting).

- **`HtmlBlock.Container(html)`** (+ `HtmlComponents.container` → `<div class="lecture-container">…</div>`,
  no styling of its own); `parse` recognizes `div.lecture-container`, `compile` round-trips it. Add-bar button
  **Container**.
- **Generalize the tree editor:** the code that renders the structure tree + right-click insert/replace was
  hard-wired to the `Raw` block. Extract a shared `bodyHtml` accessor + setter that covers `Raw`, `Container`,
  `Card`, `Section`, `Note`, `Figure`, and drive the tree through it. So the Card/Section/Note/Figure editors
  now show their **title/variant/caption field(s) + the structure tree** instead of a plain body text box —
  you can right-click inside a Card and **Insert inside ▸ List / ECG / …**.
- **Top-level "＋ Insert"** above the tree appends a component to the body (works when empty). Back it with
  `HtmlStructure.appendToRoot(html, fragment)` — append inside `<body>` for a full document, or at the
  fragment's top level otherwise (so an empty Container/Card starts nesting cleanly).
- **Scroll-to-element** stays exact for `Raw` (its body *is* the rendered top element); for a typed container
  block (nested body) scroll to the block itself.
- **Delete node:** add `HtmlStructure.removeElement(html, path)` (Jsoup: `target.remove()`) and a **Delete**
  item at the end of the node context menu (confirm first when the node is a container — it discards everything
  inside). Stale path → input unchanged.
- **Edit node:** add `HtmlStructure.getOuterHtml(html, path)` and an **Edit…** item at the top of the context
  menu. It re-opens the component's rich picker **pre-filled** for `<ecg>`/`<ecgsegment>` (parse the outerHTML
  back into the block, edit, re-emit — keeping the element id), and falls back to a **raw-HTML text editor** for
  any other element. On confirm, `replaceElement` swaps the node's markup.

---

## 4f. Part I: "ECG segment" component (a real strip that replaces sketches)

A distinct component that renders a **windowed slice of one lead** of a real pathology — for swapping a
decorative/hand-drawn ECG SVG for a real snippet from the dataset.

- **`HtmlBlock.EcgSegment(pathology, lead, startSec, durationSec, caption)`** → element
  `<ecgsegment pathology="…" lead="II" start="…" duration="…" caption="…">`; `parse`/`compile` +
  `buildEcgSegmentTag` (start/duration serialized invariant-culture; default duration ~2.5 s). In
  `HtmlStructure`: add `ecgsegment` to the block-tag allowlist **and** classify it as the ECG kind (label
  "ECG segment", preview = pathology) — otherwise it renders but is invisible in the structure tree (a node
  inside an HTML block is only surfaced if its tag is in the allowlist).
- **Render (parity with Win's `EcgSvgRenderer.SubstituteEcgSegmentTags`):** resolve the **one** lead's full
  waveform, clamp `start`/`duration` (seconds × `SampleRateHz`=500) to a sample window, slice it, and reuse the
  existing single-lead figure renderer over the windowed samples — but render a **bare** strip (no 1 mV
  calibration pulse and no lead label, minimal left margin) since it's a compact snippet — and give segment
  figures a distinct SVG-pattern uid prefix (e.g. `ecgseg`) so they don't collide with full-ECG figures. Run this pass
  **in addition to** the `<ecg>` pass in the lecture renderer. (The `<ecg>` regex uses a word boundary so it
  does **not** match `<ecgsegment>`.)
- **Editor:** palette entry **"ECG segment"** (Add-bar button + insert menu) with a picker = rhythm (dataset)
  + single **lead** + **start(s)** + **duration(s)** + caption; a block editor card with the same fields.

---

## 4g. Part J: Visual range picker + tips on the ECG segment

Blind start/duration numbers are unusable; let the author **see the waveform, drag the window, and drop tips**
(guide lines / text labels / points) — reusing the Constructor's `TipOverlay` model — with the tips rendered
in the published segment.

- **Model + codec (Core):** extract the tips codec from `PathologyParser` into a public `TipOverlaySerializer`
  (`serialize`/`parse`, unchanged wire format `kind|cap|lead|text|s:a;s:a…`, `~`-joined) + `encodeAttribute`/
  `decodeAttribute` (**Base64**, so it's safe inside `<ecgsegment tips="…">`). `HtmlBlock.EcgSegment` gets
  `tips: List<TipOverlay>` (default empty); tip **sample indices are absolute** in the full lead. `buildEcgSegmentTag`
  emits `tips="<base64>"` when non-empty; parse decodes it.
- **Render tips (App/SVG):** in `EcgSvgRenderer`, thread `tips` + `tipSampleOffset` (= window start sample) into the
  figure and add `drawTipsSvg` — port the **VerticalLines / HorizontalLines / Label / Points** cases of
  `EcgRenderer.DrawTips` to SVG (`x = xLeft + (sample − offset)·PxPerSample`, `y = baselineY − adc·PxPerAdcCount`;
  `<line>`/`<text>`/`<circle>`; clip to the cell). Drawn after the trace.
- **Interactive picker (App):** new `SegmentRangeCanvas` (Compose Canvas / a `<canvas>`-style view on Android):
  draws the lead's full waveform on a grid + a draggable **selection band** (move + resize handles) + placed tips;
  tools **Range / V-line / H-line / Text / Point / Delete** map pointer clicks to data-space `TipPoint`s
  (sample = x→sample, adc = y→amplitude); raises range/tips changes. The segment dialog embeds it (rhythm picker
  + lead + tool row + label-text box + caption). The block card becomes a summary + **"Edit range & tips…"**.
  Resolve the waveform via `PathologyRepository.LeadWaveform`.

---

## 5. Part D: Verification

### 5.1 Unit tests (mirror the Win tests)
- Nested `<div><svg><path/></svg></div>` → a `Raw` block; `compile(parse(x))` has **no `<p>`** and preserves
  `<svg>`/`<path>` (regression for the corruption).
- A full `<!doctype html>…` document → a single `Raw` block that round-trips including `<head>`/`<style>`, and
  stays a full document after an edit (`isFullDocument(result)` true).
- `outline` classifies components (Heading/Text/List/Table/Ecg/Diagram/Container) with friendly labels and
  **does not expand `<svg>` internals** (svg is one Diagram leaf).
- `replaceElement` swaps exactly the addressed node (siblings/ancestors intact) — including replacing a whole
  `<svg>` — and still works for an explicit inner-shape path; `insertAdjacent` places before/after; a stale
  path returns input unchanged.
- `buildEcgTag` output re-parses back into an equivalent `Ecg`.
- `embedDocument(fullDoc)` returns a fragment (`isFullDocument` false) starting with `<div class="lecture-embed">`,
  scopes `body`→`.lecture-embed` and `.card`→`.lecture-embed .card`, recurses `@media`, keeps `@keyframes`,
  drops `100vh` and `<script>`, and keeps the body content; it round-trips as a single `Raw` block.
- `HtmlComponents.*` builders emit the expected classes (`list`→`<ul|ol class="lecture-list">`, `note("warning",…)`
  →`lecture-note-warning`, `card(null,…)` omits the title, `quote(…,null)` omits `<cite>`, `divider()`→`<hr…>`),
  and a component appended into a page keeps everything else intact.
- Structural **blocks** round-trip through `compile`→`parse` as their own type with data + id intact (List items
  & numbered, Quote/Note/Card/Section/Figure content, Divider); a **foreign** `<div class="section-block" style=…>`
  stays a `Raw` block (its class/style preserved), not mis-typed as a Card.

### 5.2 Manual verification flow (emulator + the real file)
1. Launch the app → CourseConstructor → create a topic → paste the `3.1. …` document ("All in one"/full-page).
2. Switch to **Visual** mode — the block shows a readable component tree (Card → Sections → Headings, Text,
   Lists, **Diagram (SVG)** nodes, …).
3. Expand to the «брадикардии-тахикардии» **Diagram (SVG)** node.
4. **Long-press / right-click** it → **Replace with ECG…**, choose a rhythm/leads, confirm.
5. Expected: the raw HTML now has an `<ecg …>` in that exact spot; the live preview renders the ECG figure;
   the rest of the page (cards, other SVGs, CSS) is unchanged.

### 5.3 Combine (decompose) flow
1. Open the same all-in-one page in **Visual** mode; add an app component (e.g. **Header**) from the Add bar.
2. Expected: the page becomes an **"Embedded page"** block (its styling preserved, scoped) and the new Header
   renders as a normal styled sibling **below** it — not stray, unstyled markup after `</html>`. Saving and
   reopening keeps them as one coherent lecture.

### 5.4 Insert-into-block flow (and data-loss guard)
1. In Visual mode, right-click a nested element → **Insert after ▸ Text**, type text, OK.
2. Expected: the text appears at that spot; **nothing else in the lecture is lost** (regression test:
   `InsertParagraph_*KeepsEverything`, `AppendChild_AddsAsLastChild_KeepingExisting`).
3. Right-click the **root** ("Embedded page"/"Card") → **Replace with ▸ Text**: a **confirmation** must appear;
   cancelling leaves the page intact. (Replacing a container without confirming previously erased the lecture.)

### 5.5 Structural components palette
1. Right-click a section → **Insert inside ▸ Note / Card / List / Quote / Figure / Section / Divider**, fill the
   small dialog, confirm.
2. Expected: each renders with its intended styling (from `HtmlComponents.Css`) — a bordered card, a coloured
   note box, a bulleted/numbered list, etc. — both inside a still-whole page and inside a decomposed lecture.
