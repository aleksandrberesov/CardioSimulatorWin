# General Prompt — Build a [DOMAIN] App on [PLATFORM / LANGUAGE]

> **How to use this prompt.**
> Fill in every `[PLACEHOLDER]` before handing it to an AI assistant or
> using it as a project specification. Section headings marked **[KEEP AS-IS]**
> describe structural patterns that should be preserved verbatim across
> target platforms; sections marked **[ADAPT]** need platform-specific
> translation.

---

## 0. Project brief

Build **[APP NAME]**, a [TARGET PLATFORM] application for [DOMAIN / PURPOSE].

| Item | Value |
|---|---|
| Target platform | `[iOS · Android · Web · Desktop · Flutter · …]` |
| Primary language | `[Swift · TypeScript · Dart · Kotlin · …]` |
| UI framework | `[SwiftUI · React · Flutter · Jetpack Compose · …]` |
| Reactive layer | `[Combine · Zustand/Jotai · Streams · StateFlow · …]` |
| Persistence | `[UserDefaults/CoreData · localStorage/IndexedDB · SharedPreferences · DataStore · …]` |
| Domain | `[Medical / Educational / Industrial / …]` |

The architecture, UI/UX patterns, data-format conventions, and localization
strategy are modelled on the CardioSimulator Android reference app
(`docs/architecture.md`). Adapt every implementation detail to
the target platform while preserving the structural decisions listed below.

---

## 1. Layered architecture [KEEP AS-IS]

Enforce a strict four-layer separation. No layer may import from a layer
above it.

```
┌──────────────────────────────────────┐
│  UI  (screens / panels / components) │
└──────────────┬───────────────────────┘
               │  observable state / events
┌──────────────▼───────────────────────┐
│  ViewModels  (one per logical concern)│
└──┬─────────────┬──────────────────────┘
   │             │
┌──▼──────┐  ┌──▼──────────────────────┐
│ Domain  │  │  Data / Network         │
│ (pure)  │◄─│  repositories · sources │
└─────────┘  │  prefs · ZIP · socket   │
             └─────────────────────────┘
```

- **Domain** — pure data classes and business logic with zero platform
  imports. This is the only layer shared 1:1 between unit tests and the
  app binary.
- **Data** — file I/O, persistence, ZIP handling, network clients. Depends
  only on domain.
- **ViewModels** — one per logical concern (not per screen). Hold reactive
  state; translate user events into repository calls; never reference UI
  widgets directly.
- **UI** — declarative; all state is observed, never stored locally (except
  transient gesture state). Calls ViewModel actions; never calls data/domain
  directly.

---

## 2. Dual-pipeline pattern [KEEP AS-IS]

The app manages **two independent data pipelines** that are structurally
identical. Implement them as parallel sibling trees; do not merge them into
a single god-repository.

| Pipeline | Purpose | Bundle |
|---|---|---|
| **[Primary]** e.g. Pathology/Content | Core domain objects the app displays and edits | `[Primary].zip` |
| **[Secondary]** e.g. Course/Lesson  | Structured educational / organisational content | `[Secondary].zip` |

Each pipeline exposes the same interface shape:

```
[PipelineSource]   interface       read-only storage contract
  ├── [AssetSource]               bundled read-only defaults
  └── [FileSource]                user-picked writable directory
                                  (+ atomic write via .tmp + rename)
[PipelineRepository]              caches manifest; routes reads/writes;
                                  exposes StateFlow<Manifest?> (manifestFlow)
[ZipExtractor]     object         SAF zip → target directory, flattening paths
```

Both pipelines share the same `DataState` lifecycle:

```
NotConfigured → Loading → Ready(count) | Error(reason)
```

---

## 3. ViewModel responsibilities [KEEP AS-IS]

Create exactly these ViewModel roles. Do not consolidate them.

| ViewModel | Key state emitted | Key actions |
|---|---|---|
| **AppViewModel** (central hub) | selectedOperatingMode, selectedLanguage, dataState, courseDataState, tcpConnectionState, courses | updateLanguage, updateOperatingMode, setDataFolder, setCourseDataFolder, exportZip, exportCoursesZip, sendStart/Stop, sendUploadArchive, uploadCourses |
| **MonitorViewModel** (display config) | monitorMode (speed, scale, gridScheme, seriesScheme, isRunning, isCompareMode, comparisonTargets, comparisonPresets) | setSpeed, setScale, setGridScheme, setSeriesScheme, toggleCompareMode, setComparisonTarget, saveCurrentAsPreset, applyPreset |
| **RhythmViewModel** (selection) | rhythms (course-filtered in Teaching), selectedRhythm, waveforms, comparisonWaveforms, significantPoints | loadManifest, selectRhythm, loadComparisonWaveform |
| **ConstructorViewModel** (editor) | targetFile (markers), focusedLead, selectedIndex, dirtyLeads, isMetadataDirty, toolMode, editingAlgorithm, editingRadius, imageUri + transform (offset, scale, rot, alpha, locked), ghostTrace | selectPathology, setSample (weighted kernel), toggleSignificantPoint, selectSignificantPoint, startStroke, undo/redo (per-lead depth 20), save, calculateDerivedLeads, duplicate/delete |
| **CourseConstructorViewModel** | selectedCourseId, lectures, selectedLectureId, draft (HTML), answers, previewLecture, isDirty | selectCourse, selectLecture, setHtml, insertSnippet, setTableCell, save, revert, createCourse/Lecture, renameLecture, deleteLecture |
| **CourseViewerViewModel** | selectedCourseId, lectures, selectedLectureId, lecture | setLanguage, selectCourse, selectLecture, closeLecture, restore |

Each ViewModel is keyed by `mode.name` so multiple modes can hold
independent instances. ViewModels persist their last selection to
`Preferences` so the app resumes where the user left off.

---

## 4. Operating modes [ADAPT]

Define an enum of operating modes. Route the entire UI from a single
`selectedMode` observable. Each mode gets its own screen, bottom-panel
slot, and independently keyed ViewModels.

```
enum OperatingMode {
    Teaching           // read-only playback / display
    Testing            // assessment / evaluation
    Examination        // placeholder — reserved
    OSKE               // placeholder — reserved
    Constructor        // edit raw domain objects
    CourseConstructor  // author structured content
}
```

Rules:
- `MainScreen` is the **only** place that reads `selectedMode` and routes
  to screens. All other components are unaware of the active mode.
- Placeholder modes render an empty layout; do not crash.
- The bottom control panel slot is `null` for modes that have no controls.

---

## 5. UI structure [KEEP AS-IS]

```
RootTheme(darkTheme)
└── MainScreen
    ├── [guard] DataSourceScreen    (shown until data confirmed)
    ├── [overlay] SettingsDialog
    ├── TopBar   weight 2           (mode dropdown + per-mode controls)
    ├── ContentArea weight 15       (one of N mode screens)
    └── BottomBar weight 2          (settings gear + mode-specific slot)
```

### Side drawers

All list-based selectors (rhythm list, lecture list, course list, significant
points list) live in `SideDrawer` components that slide in from an edge.
The drawer handle is always visible; the panel slides open on tap. Never
put a persistent list alongside the main canvas — it steals space on
landscape/tablet layouts.

### Control panels

Cluster related controls into named panel components. Each panel takes
only the ViewModel(s) it needs — never `AppViewModel` if a narrower
ViewModel suffices.

### Canvas rendering

Waveform / domain-object rendering happens in a Canvas (or equivalent
low-level drawing API). The rendering layer reads only a `PixelScale`
value object (derived from `pxPerMm` anchor + calibration constants) and
a `List<Float>` of baseline-zeroed values. It knows nothing about files,
repositories, or ViewModels.

---

## 6. Data formats [KEEP AS-IS]

### Bundle zip layout

```
[Bundle].zip
├── manifest.txt          ← dataset header + index
├── [item-id].[ext]       ← one file per domain object
└── …
```

### File format (key:value text)

All files are **UTF-8, LF line endings, no BOM**. Use `key:value` for
headers and semicolon-delimited rows for index entries. Blank lines
separate blocks.

```
# Header block
version:1.0
[key]:[value]
…

# Index rows (one per item)
[type]:[id];[field]:[value];…
```

### Manifest invariants

- `version` is the first key. Readers must reject unknown major versions
  with a typed `FormatException`; never silently ignore bad data.
- Each index row carries enough fields for the UI to render a list item
  without opening the item file (id, display name, count).
- Include a `[count]` summary key so consumers can validate completeness
  without counting entries.

### Domain object file

```
# Single header block
pathology:[id]
title:[english display name]
name:[localized display name]   ← optional for non-en content
leads:[count]
markers:[index]:[TYPE],[index]:[TYPE]... ← optional ECG landmarks

# Data blocks (one per lead, blank-line separated)
lead:[name]
count:[int]
points:[comma-separated values]
```

### Content files (lecture / article)

```
---
id: [lecture-id]
order: [int]
title: [display title]
schemaVersion: 1
---
[HTML body — verbatim, not parsed into blocks]
```

- Store the body verbatim (`rawHtml` / `rawContent`). Parse only on save
  and on live-preview render. Never round-trip the HTML through a DOM
  serializer — it corrupts author formatting.
- Per-lecture editable-table answers live in a sibling
  `[id].[lang].answers.json` file so the HTML stays pristine for diffs.

---

## 7. Localization [KEEP AS-IS]

### UI strings

```
enum Language(tag: String, displayName: String) {
    EN("en", "English"),
    RU("ru", "Русский"),
    ZH("zh", "中文"),
    ES("es", "Español"),
    // add more as needed
}
```

- Detect the system language on first launch; persist the user override.
- All hardcoded display strings live in platform string resource files
  (`.strings`, `strings.xml`, `l10n/…`). Never inline display text.
- Language switching is immediate and in-process: update a single
  `selectedLanguage` observable; all observing UI rebuilds reactively.

### Content files

- One file per (lecture × language): `[id].en.html`, `[id].ru.html`, …
- The reader tries the requested language first, then falls back to `"en"`.
  Never crash on a missing translation — degrade gracefully.
- The `language` field on a parsed lecture object carries the actual
  language resolved (may differ from requested when fallback triggers).

### Domain object names

- Domain objects carry **both** `titleEn: String` and `nameRu: String?`
  (or the equivalent pair for your language set).
- The selector list displays the field matching `selectedLanguage`; falls
  back to `titleEn` when the localised field is null.

---

## 8. Persistence [KEEP AS-IS]

Use a single typed key-value store (DataStore, UserDefaults, SharedPrefs,
localStorage, …). All keys are string constants; never use magic strings
inline.

### Key taxonomy

| Prefix | Scope | Examples |
|---|---|---|
| *(none)* | Global | `tree_uri`, `language_tag`, `tcp_ip`, `dark_theme` |
| `${mode}_` | Per operating mode | `${mode}_grid_scheme`, `${mode}_last_rhythm_id` |
| `${mode}_monitor_` | Per-mode monitor config | `${mode}_monitor_speed`, `${mode}_monitor_scale` |
| `${mode}_comparison_` | Per-mode compare config | `${mode}_comparison_presets` |
| `courses_` | Course bundle global | `courses_tree_uri`, `last_course_id` |
| `${mode}_last_` | Per-mode last selection | `${mode}_last_lecture_id` |

Rules:
- Per-mode reads fall back to the legacy global key when no per-mode value
  exists (backwards compatibility with prefs written before per-mode
  keying was added).
- Writes always go to the per-mode key.
- Never store raw URIs as paths; store the full URI string and parse it on
  read.

---

## 9. Network protocol [KEEP AS-IS]

Line-delimited JSON over a raw TCP socket (or WebSocket for web targets).

```
// Common envelope
{ "type": "start" | "stop" | "points" | "upload" | "ack", "id": "…" }

// start — begin streaming
{ "type": "start", "sampleRate": 500, "params": { "key": "value" } }

// upload — binary payload immediately follows the \n
{ "type": "upload", "filename": "Data.zip", "size": 204800 }
<204800 raw bytes, no delimiter>

// ack — server response to upload
{ "type": "ack", "filename": "Data.zip", "bytes": 204800 }
```

Rules:
- Reconnect automatically every 5 s on drop; expose `ConnectionState`
  (`Disconnected | Connecting | Connected | Error`).
- Auto-upload the current dataset on every successful connect.
- Writes are serialised through a `Mutex` (or equivalent) to prevent
  interleaved frames.
- Provide both a strict `decode()` (throws on bad input) and a lenient
  `decodeOrNull()` (returns null) variant.
- Optional fields are **omitted** on the wire when absent, never `null`.

---

## 10. Editor patterns [KEEP AS-IS]

### Dirty-state tracking

Track changes at the **sub-resource level** (per lead, per lecture). This
lets the UI show which specific items are dirty and offer targeted
Revert-Lead operations, not just a blanket "unsaved changes" flag.

```
dirtyLeads: Set<LeadId>   // for waveform edits
isMetadataDirty: Boolean  // title / name / markers / global annotations
isSaving: Boolean         // guard against concurrent saves
```

### Significant ECG landmarks (markers)

The app supports marking key points (P_START, R_PEAK, etc.) globally for
a pathology. These are stored in the `markers:` header field as
`index:TYPE` pairs. The editor provides a dedicated panel for toggling
these points at the `selectedIndex`.

### Weighted editing kernel

When the user nudges a value, spread the change over a configurable
neighbourhood using a weight function (cosine, spline, LOESS, etc.). Store
sub-integer contributions in a parallel `Float[]` accumulator so repeated
nudges produce a smooth curve rather than a step function. Clamp to the
physical valid range on every write.

### Undo / redo

Per-lead, snapshot-based for waveform edits. Snapshot before each discrete
stroke / gesture begins (`startStroke`). Cap the stack at 20 entries per
lead. Redo stack clears on any new edit.

### Derived leads calculation

Automatically fill III, aVR, aVL, aVF from leads I and II (Einthoven/Goldberger),
and V1, V3, V4, V5 from V2 and V6 (V-projection). Derivable leads are
read-only in the editor.

### Live preview

For content editors (HTML lecture body), debounce parse calls by 200 ms
after the last keystroke. Render the parsed result in a read-only preview
pane side-by-side with the text editor.

### Save path

1. Serialize in-memory state to the text format.
2. Write to a `.tmp` file.
3. Atomically rename `.tmp` → target filename.
4. Reload manifest (if the change affects the index).
5. Clear dirty flags only on confirmed success.

---

## 11. Comparison mode [KEEP AS-IS]

The monitor supports a **comparison layout** where each pane can display
a different domain object / lead combination rather than the active
rhythm.

```
isCompareMode: Boolean
comparisonTargets: Map<PaneIndex, ComparisonTarget(pathologyId, lead)>
comparisonPresets: List<ComparisonPreset(name, targets)>
```

Rules:
- When `isCompareMode` switches on with no targets, prompt the user to
  pick targets or select a preset.
- Empty panes render a placeholder tap-target that opens a picker dialog.
- Presets are persisted per-mode via the `${mode}_comparison_presets` key
  as a JSON array.
- The rhythm/item list stays filterable by `selectedCourseId` in all
  picker dialogs.

---

## 12. Photo tracing / underlay pattern [KEEP AS-IS]

When the editor needs a visual reference (a photo, scan, or reference
diagram) as a tracing aid:

1. Load the image into a `Uri` / URL held in ViewModel state only
   (not persisted to the saved file).
2. Maintain a set of transform states for the image: `offset`, `scale`,
   `rotationDeg`, `alpha` (transparency), and `isLocked`.
3. Render the image **inside the same coordinate frame as the editable
   canvas** — not in a separate background layer. This guarantees that a
   pointer gesture on the photo pixel maps directly to a data index.
4. Gate all gestures through an explicit `ToolMode` enum
   (`Position | Trace | Select`). Never let image-positioning drags and
   data-editing drags compete over the same gesture recogniser.
5. In `Trace` mode, convert each drag point to `(index, value)` using the
   inverse of the canvas projection, then call `traceSamples(updates)`.
6. Provide an auto-detect path (`ghostTrace`) that proposes a candidate
   array without committing it; the user confirms with `applyGhostTrace()`.

---

## 13. Course viewer integration [KEEP AS-IS]

The read-only course viewer is embedded **inside the primary view screen**
as a full-screen overlay, toggled by a single icon button. It does not
navigate away from the main screen.

```
if (showCourseOverlay) {
    // preserve WebView in composition by translating off-screen when hidden
    // (graphicsLayer translationX = if visible 0f else 10000f)
    CourseViewerOverlay(…)
}
```

Keeping the overlay in composition while hidden preserves the WebView
scroll position, video state, and any quiz answers the user has entered.
Never unmount and remount it on open/close.

---

## 14. What NOT to do

- **Do not** mix platform I/O into the domain layer.
- **Do not** store display state (scroll position, animation phase) in a
  ViewModel — use local Compose/SwiftUI state.
- **Do not** make a ViewModel aware of the ViewModel above it in the tree
  (except `AppViewModel` which is the hub by design).
- **Do not** add error handling for scenarios that cannot happen (e.g.
  validating values already guaranteed by the type system).
- **Do not** add backwards-compatibility shims, feature flags, or
  hypothetical-future abstractions beyond what the current task requires.
- **Do not** inline display strings — every user-visible string is a
  localised resource key.
- **Do not** store HTML/content round-tripped through a DOM serialiser;
  always write back the original source string.
- **Do not** place the reference image in a different coordinate space than
  the editable canvas.
- **Do not** write comments that describe *what* the code does; only write
  comments for non-obvious *why* constraints or invariants.

---

## 15. Verification checklist

Before declaring a feature complete, verify:

- [ ] All user-visible strings use localised resource keys; no hardcoded display text.
- [ ] Language switch is instantaneous and requires no app restart.
- [ ] Content files fall back to English when the requested language file is absent.
- [ ] Dirty-state flags are cleared only on confirmed disk write success.
- [ ] Per-mode ViewModel state survives mode switching (keyed instances).
- [ ] Last selection (rhythm, course, lecture) is restored from prefs on cold start.
- [ ] TCP auto-reconnects after a 5 s drop and re-uploads the dataset.
- [ ] Atomic writes: a crash mid-save leaves the previous file intact.
- [ ] Undo/redo operates per-item and does not cross-contaminate items.
- [ ] Course overlay WebView retains scroll position when toggled.
- [ ] Comparison presets round-trip through persist → restore correctly.
- [ ] Photo underlay: gestures in `Select`/`Trace` mode do not move the image.
- [ ] Manifest version mismatch produces a user-visible `FormatException`, not a crash.
- [ ] Both `decode()` (throws) and `decodeOrNull()` variants exist for the network protocol.
