# Plan: Acronym taxonomy — wire rhythms · lectures · tests · results (Android parity)

**Created:** 2026-08-12
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background — what the customer gave us and why it matters

The customer supplied three tables (under `CardioSimulator/Docs/`):

1. `таблица соответсвия общая.txt` — master rows: **acronym → group / subsection / section**
2. `Группировка по подразделам.txt` — reverse index: subsection → acronyms
3. `Акронимы инфарктов (детальная привязка).txt` — the `MI_*` family under 6.3

Together they define a **canonical taxonomy**: ~119 acronyms, each mapping to
`{ name_ru, group, subsection (e.g. 3.1.2), subsection title, section (Раздел N) }`. This is the **join
spine** that ties the four things that were previously connected only by fragile free-text:

| Entity | Was linked by | Now also carries |
| --- | --- | --- |
| Rhythm (pathology) | coarse `group:` only | `acronym:` |
| Lecture / course subsection | arbitrary slug id | `subsection:` |
| Test / exam question | free-text `Theme`/`Tags` | `Acronyms[]` (the graded tag) |
| Student results | — (Learning Scale used **fake seed numbers**) | rolled up from real attempts via the taxonomy |

The payoff: the **Learning Scale** dashboard shows *real* per-subsection/section mastery computed from
graded exam attempts, instead of the hardcoded demo numbers ported from the prototype.

Key normalization facts baked into the table (mirror these):
- Group column reuses the existing `groups.txt` keys (Синусовый→`sinus`, …, Инфаркт→`infarction`).
- **Multi-node acronyms** keep a *primary* subsection + alternates: `WPW`→`4.11.1`(+`8.1`),
  `STC`→`6.1`(+`6.2`), `LVQRSAL`→`5.4`(+`5.5`), `BRAD`→`3.1.2`(+`4.1`), `AVD`→`4.6`(+`3.3`).
- `section` is **derived** from the primary subsection's leading number (never stored inconsistently).
- Learning-Scale subtopics are 2-level (`3.1`); taxonomy subsections are 3-level (`3.1.2`). Rollup
  trims to the `X.Y` prefix (`SubtopicKeyOf`).

---

## 2. The data file (ship it identically)

Windows generates `src/CardioSimulator.Core/Domain/Taxonomy.tsv` from the three source tables via
`tools/taxonomy-build/build_taxonomy.py` and **embeds it in the Core assembly**.

**Android:** copy the generated `Taxonomy.tsv` verbatim into `app/src/main/assets/taxonomy.tsv` (or a
raw resource) and load it once at startup. Do **not** re-derive it on Android — use the same generated
artifact so both platforms agree byte-for-byte. Columns (tab-separated):
`acronym  name_ru  group  section  subsection  subsection_title  alt_subsections`.
Lines starting with `#` and the header row are skipped; malformed rows ignored.

---

## 3. Core domain — new types (port to Kotlin)

### 3.1 `Taxonomy` + `TaxonomyEntry`  (ref: `Core/Domain/Taxonomy.cs`)
- `TaxonomyEntry(acronym, nameRu, group, section, subsection, subsectionTitle, altSubsections)`
  with a derived `subtopicKey` = first two dotted components of `subsection`.
- `Taxonomy`: case-insensitive `find(acronym)` / `contains`, `forSubtopic(key)`, `forSection(n)`,
  `forGroup(key)`, static `normalize(acronym)` = trim + upper-case, `subtopicKeyOf(subsection)`,
  `parse(tsv)`, and a lazy `shared` loaded from the bundled asset (falls back to empty on failure —
  never throw at the call site).

### 3.2 `MasteryRollup` + `MasteryReport` + `MasteryStat`  (ref: `Core/Domain/MasteryRollup.cs`)
- `MasteryStat(answered, correct)` → `progress` = `round(100*correct/answered)` (0 when none).
- `MasteryReport` = `bySubtopic: Map<String,MasteryStat>`, `bySection: Map<Int,MasteryStat>`,
  `byGroup: Map<String,MasteryStat>`, `totalAnswered`, `totalCorrect`, `hasData`.
- `MasteryRollup.compute(results, taxonomy)`: for each graded question with acronyms, resolve the
  **distinct** set of `{subtopic, section, group}` buckets it touches (so two acronyms in the same
  subtopic count the answer **once**) and tally correctness into each. Questions with no recognized
  acronym are ignored. **Pure / unit-tested** — port the tests in `MasteryRollupTests.cs`.

---

## 4. Core domain — additive fields (port to Kotlin data classes + parsers)

All additive and back-compatible (old files load unchanged; empty/null fields are omitted on write).

| Windows change | File | Android target |
| --- | --- | --- |
| `TestQuestion.Acronyms: List<string>?` + `AcronymList` | `Domain/Test.cs` | question model + JSON (Moshi/kotlinx) — field name `acronyms` |
| `PathologyEntry.Acronyms` / `PathologyFile.Acronyms` (**list**, `+AcronymList`) | `Domain/Pathology.cs` | pathology models |
| `.dat` header `acronym:` (parse + serialize) & manifest `;acronym:` | `Domain/PathologyParser.cs` | `CourseParser`/pathology parser equivalents |
| `ExamQuestionResult.Acronyms` (+ `AcronymList`) | `Domain/Exam.cs` | exam-result model + JSON |
| `ExamGrader.Grade` captures `q.AcronymList` into each result | `Domain/ExamGrader.cs` | exam grader |
| `LectureEntry.Subsection` / `TopicEntry.Subsection` + course parse/serialize `subsection:` | `Domain/Course.cs`, `Domain/CourseParser.cs` | course model + parser |

Wire-format anchors (must match, see `tests/.../AcronymWiringTests.cs`):
- pathology dat header line: `acronym:SB,LVH,TWC` (comma-separated **list**, primary first); manifest field: `;acronym:SB,LVH`. A single code is just a one-item list, so already-single-tagged packs stay valid.
- course topic/lecture field: `;subsection:4.6` / `;subsection:3.1.2`
- question JSON key: `acronyms: ["AFIB","SR"]` (omitted when empty)

---

## 5. App — behavior changes

### 5.1 Learning Scale = real mastery  (ref: `ViewModels/LearningScaleViewModel.cs`, `Screens/LearningScaleScreen.cs`, `Screens/MainScreen.xaml.cs`)
- The VM takes an optional `MasteryReport`. When it `hasData`, subtopic progress = rolled-up accuracy
  (0 + **"no data"** flag when a subtopic has no attempts); section progress = average over **assessed**
  subtopics only (so an all-theory section isn't dragged to a false "critical"); the adaptive plan only
  recommends assessed subtopics; "mark as solved" no longer fabricates +8 progress; stats
  (cases / accuracy / global %) read from the report. **No data at all → falls back to the demo seed.**
- Host builds it as: `MasteryRollup.compute(examResultStore.list(), Taxonomy.shared)`.
- Screen renders `HasData == false` subtopics/sections with a neutral tone + `—` (not red).
- **Android note:** the Android Testing/Exam screen may still be a placeholder. If so, port
  `Taxonomy` + `MasteryRollup` + the field plumbing now, and wire the dashboard when the exam pipeline
  lands. The rollup is the reusable core.

### 5.2 Post-lecture Quick Test filter  (ref: `Screens/QuickTestScreen.cs`, `Controls/CourseViewerPanel.cs`)
- `QuickTestContext` gains `Subsection`. The launcher resolves the lecture's acronym set via
  `Taxonomy.shared.forSubtopic(subtopicKeyOf(subsection))` and matches questions by **acronym
  intersection** (precise), falling back to the legacy free-text `Theme`. Ready-test filter, "by theme"
  badge, and generation all use this signal.

### 5.3 Authoring pickers
- **Test constructor** (`Screens/TestConstructorScreen.cs`, `ViewModels/TestConstructorViewModel.cs`):
  per-question acronym picker — an autosuggest over the taxonomy (match by code or RU name) adding
  removable chips; bank browse shows `🔖 <acr>` badges. `EditQuestion.Acronyms` compiles to
  `TestQuestion.Acronyms` (normalized, deduped, unknown codes rejected).
- **Rhythm constructor** (`Screens/ConstructorScreen.cs`, `ViewModels/ConstructorViewModel.cs`):
  the group dialog gains an acronym autosuggest; `SetAcronym` normalizes + validates; tagging an
  **ungrouped** rhythm auto-files it into the acronym's taxonomy group.

### 5.4 Strings
New keys (English + RU authored; ZH/ES/HI fall back to English via the existing mechanism):
`test_ctor_acronyms`, `test_ctor_acronyms_placeholder`, `test_ctor_acronyms_none`
(ref: `Localization/AppStrings.cs`). Mirror into Android `strings.xml` (values + values-ru/zh/es/hi).

---

## 6. Testing (mirror these)

Windows added `TaxonomyTests`, `MasteryRollupTests`, `AcronymWiringTests` (Core.Tests, all green;
385 total). Port the pure ones (`Taxonomy.parse`/lookups/`subtopicKeyOf`; `MasteryRollup.compute`
including the **distinct-bucket** and **ignore-untagged** cases; the acronym/subsection round-trips).

## 6a. Auto-tagging the bundled rhythm dataset

Windows tagged the shipped 500-record pathology pack (`Assets/Pathologies.pak`, ids `ecgNNNNN`) so the
rhythm library carries acronyms out of the box. **499/500** rhythms tagged (986 codes, avg 2.0/rhythm);
only *Biventricular hypertrophy* is left untagged (no combined code).

- The records' titles are compound finding-lists ("Sinus Bradycardia + Left ventricular hypertrophy +
  T wave Change"). `tools/taxonomy-build/build_rhythm_acronyms.py` maps **every** finding phrase to an
  acronym (span-containment drops sub-phrase noise like `RBBB` inside `Complete right bundle branch
  block`) and orders them **primary-first**, where the primary is chosen using the record's authored
  `group:` as ground truth (the mapped finding whose taxonomy group matches the record group wins; else
  the most significant finding).
- The generated `tools/taxonomy-build/rhythm_acronyms.tsv` (`ecgNNNNN → acronym`) is applied to the pack
  by a new **`ContentPacker apply-acronyms <in.pak> <map.tsv> <out.pak>`** command, which writes
  `acronym:` into both the manifest entries and the `.dat` headers and round-trip-verifies the result.

**Android:** if Android ships the same `ecgNNNNN` dataset (siblings share it — see
[[repo-layout-and-sync-plan-paths]]), apply the **same** `rhythm_acronyms.tsv` to its pack/dataset so the
acronyms match byte-for-byte. Re-run the generator only if the source dataset titles change. Regenerate
the map from the pack via `ContentPacker cat <pak> manifest.txt > manifest.txt` then the Python script.

## 7. Order of work (Android)

1. Bundle `taxonomy.tsv`; port `Taxonomy` + `MasteryRollup` (+ tests).
2. Add the additive model/parser/JSON fields (+ round-trip tests).
3. Capture acronyms in the exam grader.
4. Wire the Learning Scale dashboard (or defer to when the exam screen lands) + Quick Test filter.
5. Authoring pickers + strings.

## 8. Out of scope / notes
- Results are aggregated over **all** locally-stored attempts (single-user dashboard). Per-student
  filtering is a future refinement.
- The taxonomy is a fixed reference embedded in the app; per-dataset overrides are not needed yet.
