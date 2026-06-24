# Plan: Port rhythm-groups, welcome screen & Teaching UX to Android

**Created:** 2026-06-23
**Status:** NOT STARTED
**Direction:** **Windows → Android** (reverse of the usual). These features were built in the
WinUI 3 port first; the Android app must now catch up. The Windows port is the **reference
implementation** for behavior/visuals — match it, adapting idioms to Kotlin/Compose.

**Target (Android) source root:** `C:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`

## Goal

Bring the Android app to parity with the Windows port's 2026-06 work:
1. Rhythm **groups** — a data-driven group taxonomy, a grouped "all rhythms" list with collapse/expand,
   a group filter, and per-pathology group editing in the constructor.
2. A first-launch **welcome screen**.
3. The **Teaching screen UX realignment** (from the customer's annotated screenshots).

The customer's original annotations were drawn on **Android** screenshots, so these are the intended
Android changes; Windows just implemented them first.

## Shared data — already done, drop-in for Android

The dataset format is shared between the two apps, so **no data work is needed for Android** beyond
copying files:

- The regenerated **`Pathologies.zip`** (at `E:\VLN_Project\CardioSimulatorWin\src\CardioSimulator.App\Assets\Pathologies.zip`)
  already carries, per pathology, a `;group:<key>` field in `manifest.txt` and a `group:` line in each
  `<id>.dat` header, plus an independent **`groups.txt`** catalog entry (14 groups incl. `clinical`).
  Copy this same zip into the Android assets — it is byte-for-byte valid there.
- The reusable generator lives at `E:\VLN_Project\CardioSimulatorWin\tools\pathology-groups\`:
  `add_groups.py` (script), `pathology_groups.tsv` (id→group map), `groups.txt` (catalog). To
  re-assign groups for either platform, edit the TSV/catalog and re-run:
  `python add_groups.py --in <Pathologies.zip> --out <new.zip>`. Output is UTF-8 **no-BOM, LF**.

`groups.txt` format (one line per group, display order = file order; lang tags ru/en/zh/es):
```
group:<key>;ru:<name>;en:<name>;zh:<name>;es:<name>
```
`manifest.txt` entry fields are `;`-joined `key:value`: `pathology:<id>;leads:..;title:..;name:<ru>;group:<key>`.

---

## Phase 1 — Data model: read the group

Android already parses `manifest.txt` and `<id>.dat` (see `parseManifest` / `parsePathology` and the
`PathologyEntry` / `PathologyFile` data classes). Add the group field, mirroring Windows
`Pathology.cs` + `PathologyParser.cs` + `FilePathologySource.cs`:

- `PathologyEntry`: add nullable `group: String?` (default null). Parse manifest field `group`.
- `PathologyFile`: add nullable `group: String?`. Parse `.dat` header line `group:` (header keys are
  order-independent). Serialize it (write `group:<key>` after `name:`) so the editor can persist it.
- The file-source `writePathology` must mirror the group into the manifest entry (like it already does
  for title/name): on an existing entry `copy(group = file.group)`, and include `group` when appending
  a new entry.
- **Migration guard (important):** existing `.dat` files have no `group:` line. When the editor loads a
  pathology, seed `file.group` from the **manifest** entry if the `.dat` lacks it — otherwise a save
  would wipe the group. (Windows: `ConstructorViewModel.SelectPathology` seeds from
  `repository.Manifest().Entries`.)

Acceptance: round-trip a manifest + `.dat` with `group:` (add a unit test mirroring
`PathologyParserTests`).

## Phase 2 — Group catalog from `groups.txt` (data-driven)

Reference: Windows `App/Localization/PathologyGroups.cs`.

- Add a loader that reads `groups.txt` from the extracted dataset dir (Android: `filesDir/pathologies`)
  and parses lines `group:<key>;<tag>:<name>;…` into an ordered list of `{ key, names: Map<tag,name> }`.
- Expose: `orderedKeys`, `isKnown(key)`, `displayName(key)` (current-language name → `en` fallback →
  built-in fallback → key). Reserve a synthetic `OTHER` key for unknown/missing groups (trailing bucket).
- **Reload on manifest (re)load** — extraction writes `groups.txt` next to `manifest.txt`. (Windows
  hooks `Repository.ManifestChanged`.)
- Keep a built-in fallback list (the 13 medical keys + `clinical`) with localized names for datasets
  that ship no `groups.txt`. Names live in the Android string resources; add `group_<key>` strings
  for: sinus, arrhythmia, conduction, hypertrophy, ischemia, infarction, electrolyte, syndromes,
  pacemaker, special, pediatric, newborn, pregnant, clinical, plus `group_other`. Russian source of
  truth = `groups.txt` in the tool dir.

## Phase 3 — Teaching rhythm list: grouping, toggle, collapse, pin

Reference: Windows `Controls/RhythmChoosingPanel.xaml(.cs)` + `RhythmChoosingDrawer.cs`.
This is the panel from the customer's screenshots (header "ЭКГ ритмы", search, list).

- **Group vs A–Z toggle:** a header icon button that switches the list between grouped-by-category
  (default) and flat alphabetical. Grouped mode renders section headers; A–Z renders a flat sorted list.
- **Grouping logic:** group filtered rhythms by `entry.group` (unknown/null → OTHER), iterate
  `orderedKeys + OTHER`, skip empty groups, sort items within a group by display title. A search query
  filters items first; empty groups then disappear.
- **Collapse/expand:** each section header is tappable and toggles its group; collapsed groups render
  the header only. Track collapsed keys in a set that survives search/selection/toggle. Header shows a
  chevron (right=collapsed, down=expanded) and the item count.
- **Contrasting headers:** a filled band with bold dark text (Windows uses band `#D3DEEF`, text
  `#12294B`, chevron `#1C3D6B`, count `#5A6B82`) — distinctly heavier than a plain caption.
- **Pin icon:** replace the "Закрепить панель" checkbox with a pin **toggle icon** in the header
  (Android `setDrawerFixed` already exists). When pinned, the side "Ритмы" drawer handle is hidden
  (Android may already do this — verify).

## Phase 4 — Constructor: edit & create groups

Reference: Windows `Screens/ConstructorScreen.cs` (`OnGroupClick`) + `ViewModels/ConstructorViewModel.cs`
(`SetGroup`, `CurrentGroup`).

- Add a **group button** (tag icon) to the editor toolbar, visible when a pathology is loaded
  (next to rename).
- It opens a **group dialog**: a dropdown of `— No group —` + catalog groups (preselected to current),
  **plus a "create a new group" text field**. If the field is filled it wins: derive a unique key from
  the name, append the group to the dataset's `groups.txt` (UTF-8 no-BOM), reload the catalog, and
  assign it. Otherwise assign the dropdown choice. (Windows: `PathologyGroups.CreateGroup(name)` stores
  the entered name under all four language tags.)
- `SetGroup` marks metadata dirty so the existing Save flow persists it (`.dat` + manifest).
- **Live update on change:** when the group changes, immediately re-group the rhythm list (Android's
  equivalent of patching the drawer's entries with the new group, like it does for renames). Confirm a
  group change moves the pathology to its new section without requiring a save.

## Phase 5 — "Clinical cases" group

Already present as `clinical` in `groups.txt` (RU "Клинические случаи"). No code beyond Phases 2–4 — it
surfaces automatically once a pathology is assigned to it. Customer intent: collect clinical-case ECGs
there; the case write-up will live on the (future) ECG description page — **out of scope here**, track
separately.

## Phase 6 — First-launch welcome screen

Reference: Windows `Controls/WelcomeOverlay.cs` + `MainWindow.xaml.cs` (`MaybeShowWelcome`) +
`Data/DataSourcePrefs.cs` (`WelcomeShown`).

- A full-screen branded overlay shown over the Teaching screen on first launch only: title
  "Добро пожаловать в мир ЭКГ", a short intro, a feature list, tagline "Давайте начнём!", and a
  **«Начать»** button that dismisses it.
- Persist a `welcome_shown` flag (Android DataStore) so it shows once.
- Localize title/body/features/tagline/button (RU/EN/ZH/ES) — copy from Windows `AppStrings` keys
  `welcome_*`.
- Android has no airspace constraint, so it can be a true translucent overlay over Teaching (the
  Windows version hides the shell behind an opaque panel only because the Win2D monitor renders above
  XAML — Android does not need that workaround).

## Phase 7 — Teaching screen UX realignment

From the customer's annotated screenshots (these were Android screenshots). Reference: Windows
`Screens/MainScreen.xaml`, `Controls/TopControlPanel.xaml`, `Controls/MonitorViewerOverlay.cs`.

- **"Растянуть блок до линии":** the top "Обучение" mode block spans the left-panel width so its right
  edge lines up with the rhythm-panel / monitor divider.
- **"Одна линия по всей вертикали":** one continuous vertical divider from the top bar down between the
  rhythm panel and the monitor.
- **"Показывать полностью, без рамки":** remove the gutter/frame around the monitor; show it full width.
- **"Уменьшить блоки или шрифт":** smaller font + tighter rows in the left rhythm panel.
- Re-confirm against the screenshots on a tablet layout; some Windows specifics (the hidden monitor
  "title bar") may not exist on Android.

## Out of scope / follow-up

- **ECG description page** (where clinical cases get written up): no field/page exists yet on either
  platform — needs a `description:` field + an editor + a viewer (e.g. wire the existing "Подсказки/Tips"
  button). Plan separately.

## Acceptance criteria

- Same `Pathologies.zip` loads on Android; rhythms appear under their groups; `groups.txt` drives the
  list and names.
- Group/A–Z toggle, collapse/expand, contrasting headers, and the pin icon all work in Teaching.
- Constructor can assign an existing group and create a new one; the list updates live on change and the
  assignment persists across relaunch.
- Welcome screen shows once on first launch and never again.
- Teaching layout matches the annotated screenshots.
- Parity check: behavior matches the Windows port feature-for-feature.
