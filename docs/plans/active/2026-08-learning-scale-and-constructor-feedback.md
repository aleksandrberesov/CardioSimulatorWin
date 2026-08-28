# Plan: Learning Scale & Test-Constructor Customer Feedback (28-08-2026)

**Created:** 2026-08-27
**Status:** IN PROGRESS.
- **Done (build clean, 478 Core tests pass):** A1, A2, A3, A4, A5, B1, B2, B3, B4, B5, B6, B7, C2, C3, C4 — every requested item except C1.
- **Blocked:** C1 (needs the vendor's full ~45k dataset repackaged into the pak — not in this repo).
- **Runtime UI spot-check pending** for the newer items (mode-dropdown automation was unreliable this session); the A3 launch-and-return flow and B1's live ECG-in-preview specifically warrant a manual look.
**Platform:** Windows (then sync to Android — see §Sync)
**Sources:**
- `Docs/Шкала обучения 28-08-26.docx` — new requirements for the Learning Scale («Шкала обучения») dashboard.
- `Docs/Обратная связь конструктор тестов.docx` — fixes/feedback for the test constructor & testing section.

Each source item below carries a verbatim paraphrase of the request, the code it touches, and the proposed change. Items are ordered by dependency and effort, not by document order.

---

## Document A — Learning Scale («Шкала обучения»)

Screen: `src/CardioSimulator.App/Screens/LearningScaleScreen.cs`, VM: `src/CardioSimulator.App/ViewModels/LearningScaleViewModel.cs`, roll-up: `src/CardioSimulator.Core/Domain/MasteryRollup.cs`. Built in `MainScreen.xaml.cs` (`case OperatingMode.LearningScale`, ~line 298).

### A1. Add a Course selector to the dashboard  — **S** ✅ DONE
> «Надо добавить выбор Курса обучения… Электрокардиография – Общий курс. При добавлении новых курсов – шкала прогресса по этому курсу.»

Today the dashboard is built for a single course resolved once in `MainScreen.xaml.cs:304` (`SelectedCourseId ?? Courses.First()`). The app already has a multi-course model (`AppViewModel.Courses`, `SelectedCourseId`, `CourseSelectorDrawer`, string `course_selector_title`).

**Change**
- Add a course dropdown to `BuildHeader()` (left of the student chip / where the level badge was). Populate from `AppViewModel.Courses`; default to `SelectedCourseId`.
- On change, rebuild the section map + mastery for that course. Cleanest: pass `AppViewModel` (or a `Func<string, Course?>` + course list) into `LearningScaleViewModel` and add `SelectCourse(courseId)` that re-runs `BuildCourse` + `ApplySelectedReport`, firing `StateChanged`. Mastery roll-up already keys off taxonomy/subsection so it stays per-course automatically.
- If only one course is loaded, show it as a static label (no dropdown) — matches the "Электрокардиография – Общий курс" single-course case.

### A2. Remove the "Уровень" badge  — **XS** ✅ DONE
> «Уровень – пока давай уберем.»

Remove the `BuildLevelBadge()` call in `BuildHeader()` (`LearningScaleScreen.cs:381`). Keep `BuildUserChip()`. Leave `ls_level_badge` string in place (unused) or delete; the header row simplifies to brand ····· [course selector] [student chip]. `RankDisplay`/level logic in the VM can stay dormant.

### A3. Section/subsection "main" tests + per-block results  — **L** ✅ DONE
> «Курс состоит из разделов и подразделов. По каждому есть (будет) свой тест. В конструкторе — эти ключевые тесты выделять главными. Обучаемый сдаёт тест, получает результат за этот блок.»

New concept: a course section/subsection has a **designated key test**, and the dashboard shows each block's test + the student's latest result for it.

**Customer decisions (28-08):**
- Key test is **per подраздел (subsection)** — the block = a subsection.
- **Always exactly 1 test per block** (not a set).
- A subsection with **no test / no attempt counts as 0%**, and the block's (section's) % is averaged over **all** its subsections (empty ones drag it down — no "not assessed" exclusion for these blocks).
- **Launch:** «Сдать» **navigates into the Testing section with that test preselected**, and on finish **returns to the dashboard** (not an inline runner).

**Model** — `src/CardioSimulator.Core/Domain/Test.cs` (`record Test`)
- Add `string? Subsection` (the block it is the key test for) and `bool IsPrimary` (the "главный/ключевой" flag). `TestQuestion` already carries `Subsection`/`Acronyms`, but the *test itself* needs an explicit binding so a block maps to one authoritative test.
- Persist via `TestRepository` (JSON) — additive, back-compatible (nullable/default).

**Constructor** — `src/CardioSimulator.App/Screens/TestConstructorScreen.cs`, `ViewModels/TestConstructorViewModel.cs`
- In the test editor add: a course subsection picker (reuse `CourseSubsections()` already in the constructor) + a "Сделать главным тестом раздела" toggle. Marking a test primary for a subsection should demote any previous primary for the same subsection (one key test per block).
- Show a "★ главный" badge on the ready-tests list rows (`GenTestItem` / ready list).

**Dashboard** — `LearningScaleViewModel` + `LearningScaleScreen`
- For each `LsSection`/`LsSubtopic`, resolve its primary test (by matching `Test.Subsection` to the node key) and its latest `ExamResult` for the selected student. Render the block row with: test name, "Сдать/Пересдать" action (launches it), and the block result/score.
- Wire "Сдать" to launch the test (navigate to Testing/Examination with that test preselected, or run it inline — see B1's runner reuse).

> Depends on: a clean way to launch a specific `Test` for a specific student from the dashboard. Reuse the runner introduced in **B1**.

### A4. Grading bands per the mockup  — **XS** ✅ DONE
> «Можно вести систему оценки, как описано в макете.» Mockup legend: **Освоено (>80%) · В процессе (40–80%) · Требует внимания (<40%)**.

Current `BandFor` (`LearningScaleViewModel.cs:292`) uses **≥80 / ≥50 / else**. Align to the mockup: **>80 Good / 40–80 Warning / <40 Critical**. Confirm the legend chips (`shkala_image2`) already render these labels; just move the Warning floor 50→40.

### A5. Adaptive-plan priority logic  — **M** ✅ DONE
> «Адаптивный план строится на приоритете: (1) что пройти следующим этапом от последнего блока, (2) места, требующие внимания (низкий балл), (3) то, что в процессе.»

Current `GenerateTasks` (`LearningScaleViewModel.cs:240`) is purely score-bucketed (critical<30 / growth 30–60 / fix≥70) with no notion of course sequence or "last block".

**Customer decision (28-08):** "last block" = the **furthest block the student has *started* (приступал)** in course order — **but if there are gaps (skipped blocks) before it, the next step starts from the last/earliest gap.** So: walk blocks in course order; the pointer is the first *un-started* block at or before the furthest started one (i.e. the earliest gap), else the block right after the furthest started.

**Redesign** — order tasks as:
1. **Next step** — the gap-aware pointer above (earliest un-started block up to the furthest-started, else the one after it). Drives the "продолжай отсюда" recommendation.
2. **Needs attention** — assessed blocks below the Critical threshold (<40%), weakest first.
3. **In progress** — blocks in the Warning band (40–80%).

Keep the acknowledged-task persistence. Update `PlanTaskType` labels if needed. Re-check `MarkDone`/task-id namespacing still holds.

---

## Document B — Test constructor & testing section

### B1. "Play" (preview/run) button on ready tests  — **M** ✅ DONE (ECG-in-preview needs manual look)
> «Добавить кнопку — запустить (play). Посмотреть, как получился тест, не выходя в раздел тестирования.» (annotated ▶ on a ready-test row)

Constructor ready-tests list: `TestConstructorScreen.cs` (`BuildReadyTestsList`/test row ~line 460–560, edit ✎ + delete 🗑 buttons). Add a ▶ button per row.

**Approach** — run the test in a modal **preview/sandbox** (no student, no saved `ExamResult`):
- Reuse the exam-run components: `ExaminationViewModel` + `ExamQuestionPanel` (`_questionPanel`) + `MonitorView`, which already render an in-progress `Test` in `ExaminationScreen`. Factor the "run a Test through the question panel" path into something callable from a dialog, or host a lightweight runner in a `ContentDialog`.
- Preview mode: skip `ShowStudentDialogAsync`, skip result persistence (`ExamResultStore.Save`), show the grade at the end for evaluation only.
- There is already a static generated-test *listing* preview (`ShowGeneratedTestPreviewAsync`) — that only lists questions; this needs an *interactive run*. Keep them distinct.

### B2. **BUG** — «Определи ЭКГ» + a theme generates nothing  — **S** (high value) ✅ DONE
> «Определи ЭКГ при добавленной теме — не генерирует тест. При выборе ритма без темы — генерирует, всё ок.» (screenshot: type «Определи ЭКГ» + theme «Раздел 5. Гипертрофия предсердий…» → "В банке нет подходящих вопросов под эти фильтры")

**Root cause** (`TestConstructorScreen.cs`): the on-the-fly ECG synthesis fallback is gated on **acronyms only** —
```
if (chosen.Count < _genCount && _genAcronyms.Count > 0)   // line ~2576
```
and `SynthesizeDetectQuestions`/`SynthesizeAssembleQuestions` iterate `_genAcronyms`. When the user picks a **theme** (`_genThemes`, not `_genAcronyms`) for the `detect`/`assemble` types and the bank has no matching ECG questions, `chosen` is empty and **no synthesis runs** → empty test. A rhythm/acronym selection populates `_genAcronyms`, so synthesis fires → works. Exactly matches the report.

**Fix** — when `detect`/`assemble` is selected, derive the acronym set from the picked **themes** as well as `_genAcronyms`:
- Map each theme name → its course subsection/section (theme names carry numbering; reuse `CourseNumbering.NumberPrefix` + the `CourseSubsections()` name→key resolution already in the file).
- Collect the taxonomy acronyms in that section/subsection (via `Taxonomy` entries whose `Section`/`Subsection` match), and pass that combined acronym set into the synthesizers.
- Change the gate to fire when there is *any* topic (theme OR acronym) resolvable to rhythms, not just `_genAcronyms.Count > 0`.
- Also fix the availability badge (`GenCandidates`/`GenAvailableBadge`) so its count reflects the synthesizable pool for theme-only ECG selections (so the UI stops saying "нет подходящих вопросов" when a test *can* be built).

**Tests** — add a Core-level (or screen-logic) test: theme-only + `detect` yields ≥1 synthesized question when the section has rhythms.

### B3. Move the ТЕМА row up and enlarge it  — **S** ✅ DONE
> «Строчку ТЕМА вынести вверх и сделать побольше, над блоками "Готовый тест" и "Сгенерировать".»

Individual launcher is `QuickTestScreen` (`Screens/QuickTestScreen.cs`). In `Render()` (line ~264–268) the order is `BuildActionSection()` then `BuildThemeSelector()`. 

**Change** — in course mode, emit `BuildThemeSelector()` **before** `BuildActionSection()`, and enlarge it (bigger header/label, full-width combo). Verify it still scopes both the ready-test list and the generator.

### B4. Student selection / registration on the launcher (individual)  — **M** ✅ DONE
> «Добавить выбор студента здесь, либо регистрацию, если новый. Можно и без регистрации тестироваться.»

Today identity is collected in a modal **after** picking a test (`ExaminationScreen.ShowStudentDialogAsync`, called from `OnIndividualTestChosen`). Customer wants it inline on the launcher.

**Change**
- Add a student picker to `QuickTestScreen` course mode (roster from `AppViewModel.StudentStore.List()`): a dropdown of registered students + "Новый студент" (inline register → `StudentStore`) + "Без регистрации" (anonymous) option.
- Carry the chosen `Student`/`ExamStudentInfo` through the `TestStartRequested` context so `ExaminationScreen.OnIndividualTestChosen` no longer needs to pop `ShowStudentDialogAsync` (keep the dialog as fallback when nothing was chosen). 
- Keep "тестироваться без регистрации" working end-to-end (anonymous attempts already grade; just don't require a roster entry).

### B5. Group mode — group selection on the launcher  — **S** ✅ DONE (session label only; join-restrict = open Q)
> «То же самое в групповом режиме, только там выбор группы.»

Group launcher is also a `QuickTestScreen` (`_groupLauncher`, group-session variant). Add a **group** selector (distinct groups from `StudentStore`) in place of the per-student picker for group mode. Feed the chosen group into `OnGroupConfigured`/the group session.

### B6. Return button after OSKE ends  — **XS** ✅ DONE
> «После окончания ОСКЭ — добавить кнопку "вернуться".»

`OSKEScreen.cs` graded footer (line ~446–452) only shows "Новая попытка" (`OskeNewAttempt`). Add a "Вернуться" button that clears the result and returns to the OSKE start screen (set VM result→null / call the start-state path so `UpdateExamView()` shows `_startArea`). Add string `oske_return` (En+Ru).

### B7. Exam view — start/stop + 1–2 column ECG display  — **S** ✅ DONE
> «Так же тут кнопки старт/стоп и способ отображения 1–2 колонки.»
> **Customer:** «Вид такой же остаётся. 1-2 колонки — это отображение ЭКГ самих, как обычно у нас это происходит. Кнопки останавливают бегущую ЭКГ линию.»

So this is **not** an exam-layout change — it's exposing the **monitor's own existing controls** inside the testing/exam view (`ExaminationScreen._examArea`; also OSKE `_examArea`):
- **Start/stop** — the monitor's run toggle (`MonitorViewModel.SetIsRunning`) — freezes/resumes the scrolling ECG line. Same control the monitor already has (bottom-bar ▷/⏹).
- **1–2 columns** — the ECG **lead layout** the monitor already supports (single column vs two columns of leads — same as the main monitor's column control), not the exam window split.

**Change:** surface the monitor's existing start/stop + lead-column controls in the exam view (reuse the same `MonitorControlPanel`/`MonitorViewModel` controls the main monitor uses; find the column-count control there). Low risk — reusing existing monitor controls, just making them visible on the exam monitor.

---

## Sync to Android

All of the above are Windows-first. Per repo convention, once each Windows change lands, mirror it to Android and write a parity plan in `Android/docs/plans/sync/` (note: `AGENTS.md` sync paths are stale — mirror to `Android\docs\plans\sync`). The `/create-promt-android` skill generates the Android active plan. Prior parity plans for these screens already exist under `docs/plans/sync/` (learning-scale-*, test-constructor-*, testing-exam-*) — extend that series.

---

## Verification plan

- **Automated:** `dotnet test` at repo root (`E:\VLN_Project\CardioSimulator\Win`). Add tests for **B2** (theme-only ECG synthesis) and **A4/A5** (band thresholds + adaptive ordering) in `tests/CardioSimulator.Core.Tests` (`MasteryRollupTests`, new gen-logic test).
- **Manual (build + run per the run memory):** 
  - Learning Scale: course selector switches maps; no level badge; bands match legend; adaptive plan orders next-step → attention → in-progress; block rows show primary test + result.
  - Constructor: ▶ preview runs a test in a modal without saving; «Определи ЭКГ» + theme now generates.
  - Testing: ТЕМА above the action cards; student picker (+ anonymous) on individual, group picker on group; OSKE shows "Вернуться"; exam start/stop + column toggle.

---

## Open questions — RESOLVED (customer, 28-08-2026)

1. **B7 "1–2 columns"** → the ECG **lead layout** (as usual in the app), view unchanged; buttons **freeze the running ECG line**. (Folded into B7.)
2. **A3 granularity** → **per подраздел**, always **1 test per block**, empty subsection = **0%** counted into the block. (Folded into A3.)
3. **A3 launch** → **navigate into Testing with the test preselected, return to the dashboard on finish.** (Folded into A3.)
4. **A5 "last block"** → **furthest block *started*; if gaps, start from the last gap.** (Folded into A5.)
5. **B4 registration** → **yes, add the new student to the roster** (already implemented that way). ✓

---

## Document C — Additional items (customer, 28-08-2026)

### C1. Load the full ECG database (all ECGs, not the 5000 subset)  — **M** ⛔ BLOCKED (needs vendor full dataset — not in repo)
> «В виду продолжения сортировки ритмов, предлагаю сейчас грузить базу со всеми ЭКГ. Сейчас грузится с 5000 шт.»

The bundled `Assets/Pathologies.pak` currently ships a **5000-rhythm subset** (constructor stat reads "5 000"; the full base is ~45k, shown as "45 204" in an older screenshot). Repackage/point the app at the **full** dataset.
- Find where the pak is built/selected (the packaging step that produced the 5000-subset pak) and switch it to the complete rhythm set. Verify list virtualization still holds at 45k (see memory *large-dataset-virtualize-lists*).
- Watch pak size / load time; confirm the constructor + Learning-Scale stats update. Likely a build-tooling/data change more than app code.

### C2. Doctor-verification status in the ECG constructor  — **M** ✅ DONE
> «В конструкторе ЭКГ добавить блочок в верхней панели — статус верификации врачом. Выпадающий список: Верифицировано / Проверка / Не проверено. Все ЭКГ из академического блока — автоматом Верифицировано.»

- **Model:** add a `VerificationStatus` enum (`Verified` / `InReview` / `Unchecked`) to the pathology/rhythm entry (`PathologyEntry` + manifest persistence). Default `Unchecked`; **academic-block rhythms default to `Verified`** (auto-set on load/import when the rhythm is academic — see memory *constructor-clinical-academic-follow* for the clinical/academic flag).
- **UI:** a dropdown in the **ECG constructor's top panel** (the rhythm/course ECG constructor — `ConstructorScreen`/`CourseConstructorScreen` + `ConstructorControlPanel`) bound to the active rhythm's status; persist on change.
- Consider showing the status as a small badge in rhythm lists later.

### C3. Compare-mode fixes  — **S–M** ✅ DONE (3 bugs fixed)
> «На ритме ЭКГ выводить номер ЭКГ; не работает зум с мышки (работает с нижней кнопки); линейка не работает.»

Compare mode = the monitor's comparison overlay (`MonitorView` compare panes, `MonitorViewerOverlay`, `_rhythmVm.ComparisonWaveforms`). Three defects:
- **C3a — show the ECG number** on each compared rhythm strip (label each compare pane with its rhythm id/number).
- **C3b — mouse-wheel zoom broken in compare mode** (works via the bottom zoom button). The wheel handler is likely suppressed/short-circuited when compare panes are active — fix the wheel→scale path for compare mode.
- **C3c — ruler (linейка/caliper) broken in compare mode.** The caliper tool doesn't work with compare panes active — fix caliper hit-testing/placement in compare mode.

These need reproduction + isolation in `MonitorView`/`MonitorViewerOverlay` (see memory *running-and-verifying-the-win-app* for the caliper/zoom internals).

### C4. Unify Подсказки / pQRSt popup colors  — **S** ✅ DONE (blue, per customer)
> «В кнопках подсказки, pQRSt — все всплывающие окна единого цветового формата. Давай сделаем синими, как ЭОС. Но по логике оформления просится светло-зелёный, как кнопки.»

All popups/flyouts opened from the **Подсказки** (hints) and **pQRSt** bottom-bar buttons should share **one** color format. Customer's explicit pick is **blue (like the ЭОС panel)**, but they note **light green (like the buttons)** may fit the design better — **needs a final pick before building.** Touch the popup/flyout styling for those buttons (`MonitorControlPanel` hint/measurements popups); reuse the ЭОС panel's blue tokens or the accent-green button tokens consistently. See memory *eos-monitor-overlay-deviation-red* (keep the blue panel; red only as an alert pill).
