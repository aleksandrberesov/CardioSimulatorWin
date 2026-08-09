# Plan: Port the Question Bank Redesign («Банк вопросов») + Rich Seed to Android

**Created:** 2026-08-09
**Status:** NOT STARTED
**Direction:** **Windows → Android**
**Depends on:** `2026-08-android-question-difficulty-field-parity.md` (the difficulty badge)

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`
**UI prototype:** `E:\VLN_Project\CardioSimulator\Docs\страница банк вопросов.html`

---

## 1. Background & Goals

The Test Constructor's **Bank view browse** was redesigned to the prototype: a full-width page with a
header + live stats, a filter panel (**search + section/theme + rhythm + question-type tags**), **rich
question cards** (index · id, type badge, **difficulty badge**, question text, a stimulus placeholder for
image/ECG/assemble, meta chips for theme + rhythm + tags, a 2-column answers preview with the correct one in
green ✅), and **client-side pagination** (8/page). The existing per-question **editor stays reachable** via
✏️ (it opens the current card editor with the monitor preview). New/Import/Export/Themes moved into the
browse panel. A **rich 32-question seed** fills the bank so the browse/generator/quick-test have content.

**Reference (Windows) changes:**
- `src/CardioSimulator.App/Screens/TestConstructorScreen.cs` — new `BuildBankList` (header/stats + filters +
  cards + pagination), `BankHeader`, `BankFilters` (+ type-tag toggles), `RefreshBankItems`,
  `RefreshPagination`/`PageWindow`, `FilteredBankQuestions` (theme + rhythm + type + search),
  `BuildBankListItem` (rich card), the full-width `_bankBrowseScroll` host + `UpdateHostVisibility`.
- `src/CardioSimulator.Core/Domain/TestSeed.cs` — new **`BankQuestions(pathologyIds)`** (32 curated
  cross-theme questions, mixed difficulties, ~half ECG-bound, tags, stable ids `b01…b32`).
- `src/CardioSimulator.App/ViewModels/AppViewModel.cs` — `SeedSampleTestIfNeeded` imports
  `TestSeed.BankQuestions(...)` (24 ECGs to cycle) instead of the old 3-question demo.
- `src/CardioSimulator.App/Localization/AppStrings.cs` — `bank2_*` + `diff_*` blocks.

---

## 2. Part A: Rich bank seed (do this first — makes the redesign demoable)

**Target:** `domain/TestSeed.kt` + the first-run test/bank seeder (mirror Windows
`AppViewModel.SeedSampleTestIfNeeded`; find Android's equivalent — likely in the app view-model or a
seeder in `data/`).

- Port `TestSeed.BankQuestions(pathologyIds)` **verbatim** — the 32 questions across the 8 themes (Основы
  ЭКГ, Нарушения ритма, Нарушения проводимости, Гипертрофия, Ишемия миокарда, Инфаркт миокарда,
  Электролитные нарушения, ЭКГ-синдромы), with difficulty, tags, stable ids, and ECG binding by **cycling
  the passed pathology ids** (`pathologyIds[i++ % size]`). Keep the Russian medical text as-is.
- Change the bank seed call to import `BankQuestions(...)` (pass ~24 pathology ids) when the bank is empty.
  Keep the small demo **test** (`Sample`) for the Testing picker.
- Note: Android question `tags` is a **comma-joined `String?`**, not a list — join the tag array with `,`.

## 3. Part B: The bank browse redesign (Compose port)

**Target:** `ui/screens/TestConstructorScreen.kt` (its Bank view) + `TestComponents.kt`. Reproduce
`страница банк вопросов.html` / the Windows browse, full-width and theme-aware:

- **Header** — `📚 Банк вопросов` (reuse `test_gen_open_bank`) + `bank2_subtitle`, and a stats chip:
  bank question count / theme count / rhythm count (`bank2_stat_questions/themes/rhythms`).
- **Filter panel:**
  - **Search** (`bank2_search_placeholder`) matching id / text / theme / rhythm code+name / tags.
  - **Section/Theme** dropdown (real theme catalog; "all" = `bank_filter_all`) and **Rhythm** dropdown
    (real pathologies; "all" = `bank2_all_rhythms`).
  - **Type tags** (`bank2_type_all/image/detect/assemble/case`) — multi-select; "All" resets. Category:
    assemble→`isAssembly`, image→Image, detect→Ecg, case→Text.
  - **New / Import / Export / Themes** actions live here (moved out of the old toolbar).
- **Cards** — `#index · id: …`, a type badge, a **difficulty badge** (if set; easy=green/medium=amber/
  hard=red soft chips), the question text, a stimulus placeholder (`bank2_stimulus_ecg/image/assemble`) for
  non-text, meta chips (📖 theme, 💓 code — name, #tags), a **2-column answers preview** (correct in green
  with ✅), and ✏️ edit / 🗑️ delete. **✏️ opens the existing question editor** (do not replace it).
- **Pagination** — 8/page, `bank2_pagination_format` ("Showing X of Y"), page buttons (windowed with `…`
  when many).

Search/filter/tag/page changes update only the list (don't rebuild the search field, to keep focus) — mirror
`RefreshBankItems`.

## 4. Part C: Strings

Port the **`bank2_*` block** (+ `diff_*` from the difficulty plan) from `AppStrings.cs` (En + Ru) into
`values/strings.xml` + `values-ru`; convert `{0}`/`{1}` → `%1$d`/`%2$d`. Keys: `bank2_subtitle`,
`bank2_stat_questions`, `bank2_stat_themes`, `bank2_stat_rhythms`, `bank2_search_placeholder`,
`bank2_section_label`, `bank2_all_rhythms`, `bank2_type_label`, `bank2_type_all/image/detect/assemble/case`,
`bank2_stimulus_ecg/image/assemble`, `bank2_pagination_format`, `bank2_difficulty_label`,
`bank2_welcome_title`, `bank2_welcome_desc_format`. Values are in the Windows `En`/`Ru` tables (zh/es/hi
fall back to EN).

## 5. Verification

1. First run on an empty bank seeds **32 questions**; the browse shows them paginated (4 pages).
2. Search / theme / rhythm / type filters narrow the list and update the "showing X of Y" count; type "All"
   resets. Difficulty badges render (easy/medium/hard) where set.
3. ✏️ opens the existing editor (with monitor preview); 🗑️ deletes with confirm; New/Import/Export/Themes
   work from the panel.
4. Light/dark + RU/EN both fine.

## 6. Commit

```
feat(question-bank): redesigned full-width browse + rich 32-question seed

Header/stats, search + theme + rhythm + type filters, rich cards (type +
difficulty badges, meta chips, answers preview), pagination. Editor kept
reachable via edit. Seeds a curated cross-theme bank so browse/generator have
content. Ports the Windows bank redesign + TestSeed.BankQuestions + strings.
```
