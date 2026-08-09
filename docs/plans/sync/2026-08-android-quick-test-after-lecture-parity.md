# Plan: Port the Post-Lecture Quick Test («Быстрый тест») to Android

**Created:** 2026-08-09
**Status:** NOT STARTED
**Direction:** **Windows → Android**
**Depends on:** generation logic from `2026-08-android-test-constructor-generator-parity.md`; optionally the
difficulty field.

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`
**UI prototype:** `E:\VLN_Project\CardioSimulator\Docs\быстрый тест-экзамен после лекции.html`

---

## 1. Background & Goals

A **post-lecture Quick Test launcher**: after finishing a lecture the student can pick a **ready test** or
**generate** one on the topic (test-type multi-select + count / time / difficulty), then **Start** (which
runs it in the Testing flow) or go **Back to lecture**. On Windows it's a reusable component wired into the
Teaching lecture view's **"Take the test"** button.

**Reference (Windows) changes:**
- `src/CardioSimulator.App/Screens/QuickTestScreen.cs` — the launcher UI + `QuickTestContext` record +
  generation (`GenerateTest`, an **ephemeral** unsaved test, with a theme→whole-bank fallback).
- `src/CardioSimulator.App/Controls/CourseViewerPanel.cs` — "Take the test" now opens the launcher in a
  dialog; `BuildQuickContext()` (from the open course/lecture); on start → set `PendingTest` + switch to
  Testing; on back → close.
- `src/CardioSimulator.App/ViewModels/AppViewModel.cs` — **`PendingTest`** one-shot property.
- `src/CardioSimulator.App/Screens/TestingScreen.cs` — consumes `PendingTest` on init (`TestViewModel.Start`).
- `src/CardioSimulator.App/Localization/AppStrings.cs` — the `quick_*` block.

---

## 2. Part A: The handoff (run an arbitrary/ephemeral test in Testing)

**Targets:** the app/shared view-model, `ui/screens/TestingScreen.kt`, `ui/viewmodels/TestViewModel.kt`.

- `TestViewModel` already has a `start(test)` that runs any `Test` (mirror Windows `TestViewModel.Start`),
  so an **ephemeral generated test needs no saving**.
- Add a **one-shot** `pendingTest: Test?` to the app/shared view-model (mirror Windows
  `AppViewModel.PendingTest`). When entering Testing, if it's non-null, `testViewModel.start(pending)` and
  clear it — instead of showing the picker. On Android use whatever the mode-switch/nav pattern is: a
  `StateFlow<Test?>` on the shared VM read by `TestingScreen`'s `LaunchedEffect`, then cleared.

## 3. Part B: The launcher (Compose port of the prototype)

**Target:** new `ui/screens/QuickTestScreen.kt` (a composable + a `QuickTestContext` data class; hosted in a
dialog/bottom-sheet). Parameters: the context + the repos; callbacks `onBackToLecture()` and
`onStartTest(test: Test)`. Reproduce `быстрый тест-экзамен после лекции.html` / Windows `QuickTestScreen.cs`:

- **Header** — `quick_title` + `quick_subtitle` + a section badge (`SectionLabel`).
- **Topic info** — an optional progress ring (only when `sectionProgressPercent >= 0`; the lecture flow
  doesn't track it, so hide it), a breadcrumb (section › subtopic) and an optional section name.
- **Action choice** — two cards, `quick_action_ready` vs `quick_action_generate`, toggling the section below.
- **Ready tests** — from the test repo; an optional `quick_filter_all` / `quick_filter_bytheme` filter
  (shown only when a theme is supplied; "by theme" = tests whose questions carry the context theme); each
  option selectable, with a "By theme" badge; empty-state.
- **Generator** — 6 type buttons (`test_gen_type_*` + `quick_type_mixed`), plus count (5–30) / time (5–45) /
  difficulty (`diff_*` + `quick_diff_mixed`).
- **Buttons** — `quick_back_to_lecture` (→ `onBackToLecture`) and `quick_start` (→ build/select a `Test`,
  fire `onStartTest`). Footer hint + toast.

**Generation** (`GenerateTest`, mirror Windows): filter bank by selected types (as in the generator plan) and
the context theme; **fall back to the whole bank** if a theme has no matches (so it never fails silently);
difficulty is a **soft** preference (matching questions first, then the rest); shuffle; take `count`;
`perQuestion = round(minutes·60 / size)`; build an **ephemeral** `Test` (do not save). "Mixed" type = all
types.

## 4. Part C: Wire into the lecture view

**Target:** the Teaching lecture screen (Android's `CourseViewerPanel` equivalent — where the "Take the
test"/"Take the exam" end-of-lecture buttons live; see `2026-07-android-back-to-lecture-button-parity.md`).

- Change **"Take the test"** to open the Quick Test launcher (dialog/sheet) instead of jumping straight to
  Testing. Build the context from the open course/lecture (mirror `BuildQuickContext`): the lecture's Тема =
  "section", the lecture = "subtopic"; `sectionProgressPercent = -1` (unknown → ring hidden);
  `theme = null` (no lecture→theme mapping yet → generation draws from the whole bank).
- `onBackToLecture` → dismiss. `onStartTest(test)` → set `pendingTest = test` and switch to Testing mode.
- Leave **"Take the exam"** unchanged (still → Examination mode).

## 5. Part D: Strings

Port the **`quick_*` block** from `AppStrings.cs` (En + Ru) into `values/strings.xml` + `values-ru`;
convert `{0}` → `%1$s`. Keys: `quick_title`, `quick_subtitle`, `quick_action_label`,
`quick_action_ready`(+`_desc`), `quick_action_generate`(+`_desc`), `quick_ready_header`, `quick_count_format`,
`quick_filter_all`, `quick_filter_bytheme`, `quick_ready_empty`(+`_hint`), `quick_badge_bytheme`,
`quick_gen_label`, `quick_gen_pick_types`, `quick_type_mixed`(+`_desc`), `quick_count`(+`_suffix`,`_hint`),
`quick_time`(+`_suffix`,`_hint`), `quick_difficulty`(+`_hint`), `quick_diff_mixed`, `quick_back_to_lecture`,
`quick_start`, `quick_footer_format`, `quick_progress_label`, `quick_err_no_test`, `quick_err_empty`,
`quick_welcome_title`, `quick_welcome_desc`, `quick_started_title`, `quick_started_desc_format`. Reuses
`test_gen_type_*` and `diff_*`. Values are in the Windows `En`/`Ru` tables (zh/es/hi fall back to EN).

## 6. Verification

1. Teaching → open a course/lecture → **Take the test** → launcher opens with the lecture's context.
2. Pick a ready test → **Start** runs it in Testing (no picker). Switch to **Generate**, choose types +
   params → **Start** builds an ephemeral test and runs it. **Back to lecture** dismisses.
3. Generation with an empty/narrow filter still produces a test (whole-bank fallback) or shows the
   "no matching questions" toast when the bank truly has none.
4. "Take the exam" still goes to Examination. Light/dark + RU/EN both fine.

## 7. Commit

```
feat(teaching): post-lecture Quick Test launcher («Быстрый тест»)

"Take the test" opens a launcher to pick a ready test or generate one on the
topic; Start runs it in Testing via a one-shot pendingTest (ephemeral generated
tests need no saving); Back to lecture dismisses. Ports the Windows
QuickTestScreen + wiring + strings.
```
