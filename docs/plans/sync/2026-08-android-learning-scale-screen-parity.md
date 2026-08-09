# Plan: Port the Learning Scale («Шкала обучения») Screen to Android

**Created:** 2026-08-09
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`
**UI prototype:** `E:\VLN_Project\CardioSimulator\Docs\шкала прогресса обучения.html`

---

## 1. Background & Goals

A new **student-facing progress dashboard** was added as its own operating mode. It shows the ЭКГ course
map (7 sections, each with subtopics + mastery %), an AI-style **adaptive plan** (weakest subtopics bucketed
into critical / growth / reinforcement tasks the student "solves" to raise mastery), a difficulty slider, a
per-section histogram, and header stats. Progress persists across launches (localStorage-equivalent).

It is a **net-new feature** (no prior Android screen) — this is a from-scratch Compose port. The **logic**
(course model, task generation, mark-as-solved, stats, persistence) ports 1:1 from Windows; the **UI** is a
faithful reproduction of the prototype using the app's theme + `stringResource`.

**Reference (Windows) changes:**
- `src/CardioSimulator.Core/Domain/OperatingMode.cs` — `LearningScale` enum value (declared **last** so
  existing modes keep their positions/shortcuts) → title key `mode_learning_scale`; it is **not** an
  authoring mode, so it stays visible in the Limited/student edition.
- `src/CardioSimulator.App/ViewModels/LearningScaleViewModel.cs` — **all the logic + seed data + persistence**.
- `src/CardioSimulator.App/Screens/LearningScaleScreen.cs` — the native UI.
- `src/CardioSimulator.App/Screens/MainScreen.xaml.cs` — `case OperatingMode.LearningScale` builds the screen.
- `src/CardioSimulator.App/Data/AppPaths.cs` — `LearningScaleFile` (JSON persistence path).
- `src/CardioSimulator.App/Localization/AppStrings.cs` — `mode_learning_scale` + the `ls_*` block (En + Ru).

---

## 2. Part A: Operating mode

**Target:** `domain/OperatingModeModel.kt`

Append `LearningScale(R.string.mode_learning_scale)` **last** in the `OperatingMode` enum (last so the
existing modes and their ordering are untouched). Leave `isAuthoring` as-is — Learning Scale must **not** be
authoring (it shows for students). Then add its entry wherever the mode list / picker and the mode router
are built (mirror where Windows routes `case OperatingMode.LearningScale`).

## 3. Part B: The view-model (port 1:1 — this is the bulk of the value)

**Target:** new `ui/viewmodels/LearningScaleViewModel.kt` (mirror `LearningScaleViewModel.cs`).

Port faithfully:

- **Model:** `LsSection { id, name, progress, status(Good/Warning/Critical), subtopics }`,
  `LsSubtopic { id, name, progress }`, `PlanTask { id, sectionId, sectionName, subtopicId, subtopicName,
  type(Critical/Growth/Fix), progress }`.
- **Seed:** the 7 sections + subtopics with their progress — copy verbatim from `LearningScaleViewModel.SeedCourse()`.
  **Keep the Russian section/subtopic names as-is** (course content, not UI chrome) — do not translate them.
- **Task generation** (`GenerateTasks`): flatten subtopics, sort by progress asc; buckets **critical < 30**,
  **growth 30–60**, **fix ≥ 70**; take **3 / 2 / 2**; order critical → growth → fix; drop completed.
- **Mark solved** (`MarkDone`): subtopic += 8 (cap 100); section progress = round(avg of its subtopics);
  band: ≥80 Good, ≥50 Warning, else Critical; persist; return the section's new progress (for the toast).
- **Stats:** `globalProgress = round(avg section progress)`; `cases = 184 + completed`;
  `accuracy = 78.4 + (avg−50)·0.2` (invariant one-decimal, dot); `accuracyChange = "▲"+(2.1+completed·0.1)`
  (or "▲2.1%" when none); `rank = idx≥6 ? "🏆" : "#"+(6−idx)` where `idx = min(floor(completed/1.5), 6)`;
  `avgSeconds = 47` (demo constant).
- **Persistence:** JSON `{ sections:[{id,progress,status,subtopics:[{id,progress}]}], completedTasks:[] }`
  in the app's files dir (Windows uses `%LOCALAPPDATA%/CardioSimulator/learning-scale.json`; Android →
  `context.filesDir/learning-scale.json`). Load on init, save on each mark-solved; corrupt file → fall back
  to the seed.

Expose observable state (a `StateFlow`/Compose state) so the screen recomposes on mark-solved.

## 4. Part C: The screen (Compose port of the prototype)

**Target:** new `ui/screens/LearningScaleScreen.kt`. Reproduce the prototype (`шкала прогресса обучения.html`)
and the Windows `LearningScaleScreen.cs` structure, theme-aware (light/dark) via the app's colour tokens:

1. **Header** — brand (📈 + mode name in accent), a demo user chip (avatar + `ls_demo_user_name` /
   `ls_demo_user_group`), a level badge (`ls_level_badge`), and a stats strip (cases / accuracy+change /
   avg-seconds / rank).
2. **Global progress** — a rounded track filled to `globalProgress` + the percent.
3. **Two-column grid** — left **sections map** (each section a row: number, name, status badge, a mini
   progress bar, a chevron; tapping expands its subtopics — dot-coloured by progress, tapping a subtopic
   shows a toast); right **adaptive plan** (groups `ls_group_critical/growth/fix`, each task a card with the
   subtopic, section chip, a type badge (critical→"error N%", growth→"P%", fix→"repeat"), tap opens a
   **detail dialog** with a "mark solved" action → `MarkDone`). Below the plan: a **difficulty slider**
   (5–95) updating a live label + a level text (`ls_level_high/mid/low`).
4. **Histogram** — one bar per section (height ∝ progress), coloured ≥80 green / ≥40 amber / else red, with a
   legend.
5. **Footer** — `ls_footer_stats` / `ls_footer_algo` / `ls_footer_saved`.
6. **Toast** — bottom-right transient (welcome on open; on solve shows section + new %).

Data-viz colours (green `#1A9A82`, amber `#D6A84A`, red `#D66A6A`) are constant across themes; card
surfaces/text use the app's theme tokens. Section/subtopic **content** stays Russian; **chrome** uses
`stringResource`.

## 5. Part D: Strings

Port `mode_learning_scale` + the entire **`ls_*` block** from `AppStrings.cs` (En + Ru tables) into
`values/strings.xml` + `values-ru/strings.xml`. The Windows keys are already snake_case → use them verbatim
as Android string names. **Convert C# format specifiers** `{0}`,`{1}`,`{2}` → Android `%1$s`/`%1$d`,
`%2$s`, `%3$s` (e.g. `ls_toast_solved_desc_format` = `Section %1$d: %2$s → %3$d%%` — note `%%` for a literal
percent). Keys: `mode_learning_scale`, `ls_demo_user_name`, `ls_demo_user_group`, `ls_level_badge`,
`ls_stat_cases_format`, `ls_stat_accuracy_format`, `ls_stat_time_format`, `ls_stat_rank_format`,
`ls_global_progress`, `ls_global_badge`, `ls_sections_title`, `ls_updated_today`, `ls_updated_now`,
`ls_legend`, `ls_sections_hint`, `ls_plan_title`, `ls_ai_badge`, `ls_group_critical`, `ls_group_growth`,
`ls_group_fix`, `ls_task_click`, `ls_badge_error_format`, `ls_badge_repeat`, `ls_label_critical`,
`ls_label_growth`, `ls_label_fix`, `ls_all_done_title`, `ls_all_done_body`, `ls_all_done_hint`,
`ls_difficulty_label`, `ls_difficulty_easy`, `ls_difficulty_hard`, `ls_streak`, `ls_level_high`,
`ls_level_mid`, `ls_level_low`, `ls_chart_title`, `ls_chart_subtitle`, `ls_chart_legend_mastered`,
`ls_chart_legend_progress`, `ls_chart_legend_attention`, `ls_section_short_format`, `ls_footer_stats`,
`ls_footer_algo`, `ls_footer_saved`, `ls_footer_saved_at_format`, `ls_detail_section`, `ls_detail_subtopic`,
`ls_detail_type`, `ls_detail_progress`, `ls_detail_difficulty`, `ls_mark_done`, `ls_close`,
`ls_toast_solved_title`, `ls_toast_solved_desc_format`, `ls_toast_welcome_title`, `ls_toast_welcome_desc`,
`ls_toast_difficulty_title`, `ls_toast_difficulty_desc_format`, `ls_toast_subtopic_desc_format`. Values are in
the Windows `En`/`Ru` tables. Add `mode_learning_scale` to zh/es/hi too (Windows did: `学习进度`,
`Escala de aprendizaje`, `अधिगम पैमाना`); the `ls_*` set can fall back to EN.

## 6. Verification

1. Mode picker shows the new mode (last); selecting it opens the dashboard; it is present in the student
   edition and **not** treated as authoring.
2. Expand a section → subtopics show; tap a plan task → dialog → "mark solved" bumps the subtopic +8, the
   section %, histogram bar, and stats update; a toast shows.
3. Kill + relaunch → progress persisted (JSON in files dir); corrupt/delete the JSON → falls back to seed.
4. Light/dark both render correctly; RU/EN switch relabels chrome (section/subtopic names stay Russian).

## 7. Commit

```
feat(learning-scale): student progress dashboard mode («Шкала обучения»)

New non-authoring operating mode: course map with expandable subtopics, a
Leitner-bucketed adaptive plan (mark-solved raises mastery), a difficulty
slider, a per-section histogram and header stats. Progress persists to a JSON
file. Ports the Windows LearningScaleViewModel/Screen + strings.
```
