# Plan: Port the Test Constructor Generator View («Конструктор тестов») to Android

**Created:** 2026-08-09
**Status:** NOT STARTED
**Direction:** **Windows → Android**
**Depends on:** `2026-08-android-question-difficulty-field-parity.md` (uses the difficulty field, optional here)

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`
**UI prototype:** `E:\VLN_Project\CardioSimulator\Docs\конструктор тестов.html`

---

## 1. Background & Goals

The Test Constructor gained a **"Generator" landing view** — a 2-step "build a test" flow: pick test
type(s) → pick topics/rhythms (with an OR/AND toggle) → set count + time → **Generate**, which builds a
**real `Test`** from the question bank and saves it. It also shows a **"Ready tests"** list and a
bank-stats card. The existing deep per-question editor + bank stay reachable via a 3-way view toggle
(**Generator | Tests | Bank**), with the generator as the default landing view.

**Note:** Android already has `domain/generators/TestGenerator.kt` — inspect it first and reuse/extend it
rather than duplicating generation logic.

**Reference (Windows) changes:**
- `src/CardioSimulator.App/Screens/TestConstructorScreen.cs` — the whole generator view: `RenderGenerator`,
  `GenHeader`, `GenReadyTestsCard`/`GenTestItem`, `GenConstructorCard` (steps + `GenTypeGrid` +
  `GenSelectionRow` + `GenParams`), `GenBankStats`, `GenFooter`, and **`GenGenerate`** (the real generation);
  plus the 3-view toggle (`View { Generator, Tests, Bank }`, `ShowView`, `UpdateHostVisibility`).
- `src/CardioSimulator.App/Localization/AppStrings.cs` — the `test_gen_*` block (En + Ru).

---

## 2. Part A: The 3-way view toggle

**Target:** `ui/screens/TestConstructorScreen.kt` + `ui/viewmodels/TestConstructorViewModel.kt`.

Add a **Generator** view alongside the existing Tests/Bank views and make it the default. On Windows the
toggle lives in the top bar (`ViewToggle` = Генератор | Тесты | Банк) and the generator is full-width while
the editor uses the monitor split. On Android, add a third tab/segment to the constructor's view switch;
default to Generator. **Do not remove** the existing editor or bank — they must stay reachable.

## 3. Part B: Real generation (the core value)

Mirror `GenGenerate`. Reuse/extend `domain/generators/TestGenerator.kt`:

- **Type filter** (multi-select; a question matches if ANY selected type matches):
  - `questions` → `!isAssembly && stimulus in {Text, Ecg}`
  - `image` → `stimulus == Image`
  - `detect` → `stimulus == Ecg && !isAssembly`
  - `assemble` → `isAssembly`
  - `clinical` → `stimulus == Text && !isAssembly`
- **Topic filter:** `themes` = selected theme names (match `question.theme`, case-insensitive);
  `rhythms` = selected pathology ids (match `question.pathologyId`). Combine by mode:
  - both selected → OR: `inThemes || inRhythms`; AND: `inThemes && inRhythms`
  - only one selected → use that one. Require **at least one** theme or rhythm (else error toast).
- **Build:** `candidates = bank.filter { typeMatch && topicMatch }`; error if empty; **shuffle**
  (Fisher–Yates); take `min(count, size)`; `perQuestion = round(minutes·60 / chosen.size)`; snapshot each
  chosen question with a **fresh id** and 1-based number; `title` = joined themes/rhythms (≤70 chars) or a
  default; build a `Test` and **save it** to the test repository. Refresh the Ready-tests list; toast success.
- Difficulty is **not** a generator filter here (it is in the Quick-Test launcher) — ignore it in this view.

Data sources: the theme catalog (Android's `TestThemeStore` equivalent — see `data/TestData.kt`), the
question bank, the pathology list (for the rhythm selector, like the ECG picker already in the constructor),
and the test repository (Ready tests + save target).

## 4. Part C: The generator UI (Compose port of the prototype)

Reproduce `конструктор тестов.html` / the Windows generator view, theme-aware:

1. **Header** — `test_gen_title` + `test_gen_subtitle`; buttons **`test_gen_open_bank`** (→ Bank view) and
   **`test_gen_new`** (→ new blank test in the editor).
2. **Ready tests** (left) — from the test repository: name + `test_gen_ready_meta_format` (questions · min),
   ✏️ edit (→ open in editor) and 🗑️ delete (with confirm); empty-state `test_gen_ready_empty`.
3. **Constructor** (right) — a 2-dot **step indicator** (`test_gen_step1/step2`); a **type grid** of 5 cards
   (`test_gen_type_questions/image/detect/assemble/clinical` + their `_desc`); a **selection row**: a Theme
   multi-select (chips + a live "questions in bank" count `test_gen_topic_count_format`), the **OR/AND**
   toggle (`или`/`+`), and a Rhythm multi-select (chips, capped at ~6); **params** (count + time number
   fields); **Reset** + **Generate** buttons; a hint.
4. **Bank-stats card** — real counts: bank questions / ready tests / ECG rhythms / themes.
5. **Footer + toast.**

Use the app's accent (not the prototype's blue) for consistency, and the theme tokens for surfaces.

## 5. Part D: Strings

Port the **`test_gen_*` block** from `AppStrings.cs` (En + Ru) into `values/strings.xml` + `values-ru`.
Keys are already snake_case → verbatim string names; convert `{0}`/`{1}` → `%1$s`/`%1$d`/`%2$d`. Keys
include: `test_gen_view`, `test_gen_title`, `test_gen_subtitle`, `test_gen_open_bank`, `test_gen_new`,
`test_gen_ready`, `test_gen_ready_meta_format`, `test_gen_ready_untimed_format`, `test_gen_ready_empty`,
`test_gen_ctor_title`, `test_gen_step_hint`, `test_gen_step1`, `test_gen_step2`, `test_gen_pick_types`,
`test_gen_type_*` (+ `_desc`), `test_gen_pick_topic`, `test_gen_topic_label`, `test_gen_topic_placeholder`,
`test_gen_topic_count_format`, `test_gen_mode_label`, `test_gen_rhythm_label`, `test_gen_rhythm_placeholder`,
`test_gen_count`(+`_suffix`), `test_gen_time`(+`_suffix`), `test_gen_reset`, `test_gen_generate`,
`test_gen_generate_done`, `test_gen_hint`, `test_gen_bank_subtitle`, `test_gen_stat_*`, `test_gen_footer_note`,
`test_gen_saved`, `test_gen_err_no_type`, `test_gen_err_no_topic`, `test_gen_err_empty`,
`test_gen_created_title`, `test_gen_created_desc_format`, `test_gen_mode_or_toast`, `test_gen_mode_and_toast`,
`test_gen_dup`, `test_gen_limit_format`, `test_gen_default_title_format`, `test_gen_welcome_title`,
`test_gen_welcome_desc`. Values are in the Windows `En`/`Ru` tables (zh/es/hi fall back to EN).

## 6. Verification

1. Constructor opens on the Generator view; toggle to Tests/Bank still works (nothing lost).
2. Pick types + at least one theme/rhythm + count/time → **Generate** builds a real test that appears in
   "Ready tests" and in the editor's test list; per-question time ≈ minutes·60/count. With no theme/rhythm →
   error toast; with a filter that matches nothing → "no matching questions" toast.
3. Bank-stats show real counts. Light/dark + RU/EN both fine.

## 7. Commit

```
feat(test-constructor): 2-step generator landing view

Generator | Tests | Bank toggle (generator default). Generate builds a real
Test from the bank filtered by type + theme/rhythm (OR/AND), count + time, and
saves it; ready-tests list + bank stats. Existing editor/bank kept reachable.
Ports the Windows generator view + strings.
```
