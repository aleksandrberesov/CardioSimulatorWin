# Plan: OSCE (ОСКЭ) station — exam screen, auto-grading, results & answer-key constructor

**Created:** 2026-06-14
**Status:** Draft for review. Built on the three *recommended* defaults below (D1–D3); confirm
or override before WS2 starts.
**Source:**
- WhatsApp discussion (Николай ↔ Aleksandr, 11 Jun 2026) about building the ОСКЭ window.
- Accreditation passport PDF *«Регистрация и интерпретация электрокардиограммы»* (Сеченовский
  Университет, Методический центр аккредитации, 25.05.25), §15 — the conclusion form the student
  fills in. Two specialty variants: §15.1 Терапия (pp. 15–16), §15.2 Кардиология / Функциональная
  диагностика (pp. 17–19).

This turns the chat + PDF into concrete, code-grounded workstreams against the current WinUI 3 port
(`src/CardioSimulator.App` + `src/CardioSimulator.Core`). **Net-new on both platforms** — Android's
`OSKEScreen` is also an empty stub, so there is no port oracle; we design fresh but reuse the existing
course/constructor/monitor machinery.

---

## What the discussion + PDF actually ask for

From the chat (Николай):
1. An **ОСКЭ window**: show the **ECG graph** + the **list of questions** («портянка вопросов»).
   Questions are the **same for all tickets**, one answer per block.
2. **Specialties** as a sub-section of OSCE: 1 Терапия, 2 Кардиология, 3 Функциональная диагностика.
3. **Before the exam**, a popup: enter **ФИО + группа**; saved to a file together with the results.
4. **The process**: take an ECG + the *correct* answers (stored in a file); the student fills the
   form; compare the student's file to the correct one and mark **✓ / ❌** per block.
5. **Results are saved and viewable** — add a sub-section «Результаты ОСКЭ».
6. **An OSCE/test constructor** «по типу конструктора курсов» to author the questions and correct
   answers *together with the teacher* — because accreditation requirements change every 2–3 years,
   so nothing should be hard-coded long-term.
7. (Later) admin of results + a student **database** — explicitly deferred («этим потом займемся»);
   for now **files**.

From the PDF (corroborates and constrains):
- §3 / §6 brief: the candidate records an ECG and **interprets it by filling the on-screen form**;
  the ECG is drawn from a single bank, selected automatically on exam day; the trace can be
  zoomed in/out.
- §13 chek-list item 29 «Сформулировал верное заключение*» — *«Компьютерная оценка правильности
  заполнения … проводится автоматически»*: the conclusion is **auto-graded by the computer**, not by
  the proctor. That is exactly the screen we are building.
- §15 form: single-choice (☐) per block **except** the Cardiology block «Динамика сегмента ST»,
  marked *«возможно несколько ответов»* → the model must support **multi-select** blocks too.

---

## Current state (verified in code)

- **Mode + routing exist, screen is empty.** `OperatingMode.OSKE` is a real mode
  (`Core/Domain/OperatingMode.cs`), localized (`AppStrings`: EN "OSKE", RU "ОСКЭ", ZH, ES "ECOE"),
  and routed at `MainScreen.xaml.cs:127` to `OSKEScreen` — which is a centered-grey-"OSKE"
  placeholder (`Screens/OSKEScreen.cs`). `ExaminationScreen` is the same kind of stub. No OSCE
  domain or data types exist yet — clean slate.
- **Reusable: the monitor.** Teaching/Testing host `MonitorView` + `MonitorViewModel` (Win2D
  `EcgMonitorControl`/`EcgRenderer`) — a live/zoomable 12-lead or single-lead ECG. The OSCE screen
  reuses this for the trace pane (zoom satisfies the brief's «увеличивать/уменьшать»).
- **Reusable: the file+manifest+repository pattern.** `FileCourseSource` (atomic
  `manifest.txt` + per-item files + `*.answers.json`), `CourseRepository`, and the seeding flow
  (`AppViewModel.TrySeedBundledCoursesAsync` extracts a bundled `Assets/Courses.zip` into
  `AppPaths.CoursesDir` on first run) are a direct template for an `OskeRepository` + bundled
  `Assets/Oske.zip`.
- **Reusable: the constructor UI.** `CourseConstructorScreen` + `CourseConstructorViewModel`
  (top-toolbar selector + side list + editor + live preview + New/Rename/Delete + dirty-tracking +
  atomic save) is the template for the OSCE answer-key constructor.
- **ECG identity.** A pathology = one `<id>.dat`; `PathologyEntry { Id, TitleEn, NameRu, … }` from
  `manifest.txt` (`Repository.Pathologies()`). An OSCE "ticket" = a **pathology id + specialty**;
  the answer key is keyed by that pair.
- **Localization.** `AppStrings` is a per-language `Dictionary<string,string>` (EN/RU/ZH/ES) with a
  `Changed` event the screens already subscribe to. New OSCE strings slot straight in. The form
  *content* itself is Russian clinical text and lives in the seeded data files, not in `AppStrings`.

---

## Decisions (recommended defaults — confirm or override)

- **D1 — Constructor home → sub-tab inside OSKE mode (recommended).** OSKE mode gets a top bar
  **Экзамен / Результаты / Конструктор**. Honors Николай's «подраздел» framing, keeps the 6-mode
  `Ctrl+1–6` shortcut scheme intact, and keeps all OSCE concerns in one mode.
  *Alternatives:* a 7th top-level mode "OSKE Constructor" (most literal to «по типу конструктора
  курсов», but shifts the mode enum + shortcuts); or an OSCE tab inside Course Constructor.
- **D2 — MVP scope → exam + auto-grading + file results + answer-key constructor (recommended).**
  Seed the two PDF forms as **fixed templates**; the constructor authors **correct answers per ECG**.
  Editing the *question set itself* (add/remove blocks, rename options) is a later phase (WS6).
  *Alternatives:* include full question editing up front; or exam+grading only (hand-authored keys).
- **D3 — Attempt setup → student/proctor picks specialty + ECG at the start dialog (recommended).**
  The ФИО/группа popup also chooses specialty and the ECG from the bank.
  *Alternatives:* specialty chosen, ECG random from the answer-key bank (closest to the PDF's
  auto-selection); or specialty fixed by an app setting/session.
- **D4 — Storage → JSON files under an `OSKE/` tree** in app data (alongside `pathologies/`,
  `courses/`), bundled-seeded from `Assets/Oske.zip`; results as one JSON per attempt. Matches
  Николай's «хранятся в файле» and the deferred-DB note.
- **D5 — Grading → set-equality per block.** A block is ✓ iff the selected option set equals the key
  set (covers single- and multi-select). Score = correct blocks / total; pass threshold configurable
  in the form template (default: all blocks, i.e. 100%, tunable per Николай/teacher).

---

## Data model (Core, net-new)

`Core/Domain/Oske.cs`:
```
enum OskeSpecialty { Therapy, Cardiology, FunctionalDiagnostics }
// Form mapping: Therapy → form "therapy"; Cardiology & FunctionalDiagnostics → shared form "cardiology".

enum OskeAnswerKind { Single, Multi }

record OskeOption(string Id, string Text);
record OskeQuestion(string Id, int Number, string Title, OskeAnswerKind Kind,
                    IReadOnlyList<OskeOption> Options);
record OskeForm(string FormId, OskeSpecialty Specialty, string Version,
                IReadOnlyList<OskeQuestion> Questions, double PassFraction = 1.0);

record OskeAnswerKey(string EcgId, string FormId,
                     IReadOnlyDictionary<string, IReadOnlyList<string>> CorrectOptionIds);

record OskeStudentInfo(string FullName, string Group);

record OskeBlockResult(string QuestionId, IReadOnlyList<string> Selected,
                       IReadOnlyList<string> Correct, bool IsCorrect);
record OskeResult(OskeStudentInfo Student, OskeSpecialty Specialty, string EcgId, string FormId,
                  DateTimeOffset Timestamp, IReadOnlyList<OskeBlockResult> Blocks,
                  int CorrectCount, int TotalCount, bool Passed);
```

Grading helper (Core, pure + unit-testable):
```
OskeResult OskeGrader.Grade(OskeForm form, OskeAnswerKey key,
                            IReadOnlyDictionary<string, IReadOnlyList<string>> studentSelections,
                            OskeStudentInfo student, string ecgId);
// per question: IsCorrect = SetEquals(selected, key[questionId]); Passed = correct/total >= form.PassFraction.
```

## Data layer (Core)

`Core/Data/`, mirroring `FileCourseSource`/`CourseRepository`:
- `IOskeSource` / `FileOskeSource(root)` — reads/writes forms, answer keys, manifest; atomic writes
  (same temp-file pattern as `FileCourseSource.AtomicWriteText`).
- `OskeRepository` — caches the manifest + forms; raises `ManifestChanged`. Exposes
  `Forms`, `ReadAnswerKey(ecgId, formId)`, `WriteAnswerKey(...)`, `AnswerKeyEcgIds(formId)`.
- `OskeResultStore` — `Save(OskeResult)` → one JSON per attempt; `List()`/`Read()` for the viewer.
- `OskeJson` — `System.Text.Json` (de)serialization (forms, keys, results).

**Storage layout** (under `AppPaths`, new `OskeDir`):
```
OSKE/
  manifest.txt                     # form ids + versions + answer-key counts (parallels courses)
  forms/
    therapy.json                   # form template (questions + options), seeded from PDF §15.1
    cardiology.json                # shared Cardiology/ФД template, seeded from PDF §15.2
  answers/
    <ecgId>/
      therapy.json                 # OskeAnswerKey for this ECG under the Therapy form
      cardiology.json
  results/
    2026-06-14T10-22-05_Ivanov_I_I.json
```
Answer-key file:
```json
{ "ecgId": "afib_01", "formId": "therapy",
  "correct": { "rhythm": ["afib"], "hr": ["50_100"], "naghzhes": ["none"], "...": ["..."] } }
```
Result file: serialized `OskeResult` (ФИО + группа + specialty + per-block selected/correct/✓✗ +
score + timestamp).

Bundle the seed as `App/Assets/Oske.zip` (forms + an empty `answers/`), extracted on first run by a
new `TrySeedBundledOskeAsync` in `AppViewModel` (copy of the courses-seed path), with an
`OskeZipExtractor` (copy of `CourseZipExtractor`).

## App layer

- **`OSKEScreen` (replaces the stub).** Two columns: **left** = `MonitorView` bound to a
  per-attempt `MonitorViewModel` showing the chosen ECG (zoomable); **right** = a scrollable
  questionnaire built from the active `OskeForm` — one group box per block, `RadioButtons` for
  `Single`, `CheckBox`es for `Multi`. Top bar: **Экзамен / Результаты / Конструктор** (D1) +
  specialty label. Bottom: **Завершить** (Submit).
  - **Start flow:** a `ContentDialog` collects **ФИО + Группа + Специальность + ЭКГ** (D3), then loads
    the form + trace and starts the attempt.
  - **Submit flow:** read selections → `OskeGrader.Grade` → render ✓/❌ per block + total score →
    `OskeResultStore.Save`. Show a result summary (and offer "Новая попытка").
- **`OskeViewModel`** — holds the active form, student selections, the loaded answer key, and the
  graded result; exposes `Select(questionId, optionId)`, `Submit()`.
- **`OskeResultsScreen`** (Результаты sub-tab) — `ListView` of saved attempts (ФИО, группа,
  specialty, ECG, score, date); selecting one shows the per-block ✓/❌ detail. Read-only.
- **`OskeConstructorScreen` + `OskeConstructorViewModel`** (Конструктор sub-tab) — modeled on
  `CourseConstructorScreen`: top-bar **specialty selector** + **ECG selector** (from
  `Repository.Pathologies()`); body = the form rendered as authoring controls where the teacher
  marks the **correct** option(s) per block; **Save** writes the `OskeAnswerKey` via `OskeRepository`.
  A live ECG preview pane (reusing `MonitorView`) helps author against the actual trace. *(WS6 later:
  add form-template editing — add/remove questions/options, change `PassFraction`.)*

## Integration points

- `OperatingMode.OSKE` already routes; swap `new OSKEScreen()` for the real screen at
  `MainScreen.xaml.cs:127` and give it the `AppViewModel` (Repository, language, monitor VM factory,
  the same `MonitorViewModel` construction Teaching/Testing use).
- `AppViewModel`: add `OskeRepository` + `OskeResultStore` (constructed from `AppPaths.OskeDir`),
  seed-on-first-run, and TCP upload parity if desired (the existing `SendUploadArchiveAsync` already
  ships `Pathologies.zip` + `Courses.zip`; an `Oske.zip` upload is a 3-line addition — optional).
- `AppPaths`: add `OskeDir`.
- `AppStrings`: add OSCE UI keys (tab titles, dialog labels, ✓/❌ summary, buttons) in all four langs.
  Clinical form text stays in the seed JSON, not here.

---

## Workstreams (phased)

- **WS1 — Core domain + grader + seed the two forms (P0, small, fully testable).**
  Add the `Oske*` types, `OskeGrader`, `OskeJson`, and the two seed form JSONs built verbatim from
  the PDF (full content in the Appendix). Unit tests: grader single/multi/set-equality, pass
  threshold, JSON round-trip, both forms parse with the expected block counts (Therapy 10,
  Cardiology 13). *No UI.* Lands the foundation with an obvious oracle (the PDF).
- **WS2 — Data layer + seeding (P0).** `IOskeSource`/`FileOskeSource`, `OskeRepository`,
  `OskeResultStore`, `OskeZipExtractor`, `Assets/Oske.zip`, `AppPaths.OskeDir`, `AppViewModel`
  wiring + first-run seed. Tests mirror `FilePathologySourceTests`/course tests.
- **WS3 — Exam screen + auto-grading + result save (P0, the core UX).** `OSKEScreen` +
  `OskeViewModel`, start dialog (ФИО/группа/specialty/ECG), questionnaire rendering, Submit →
  grade → ✓/❌ summary → save. Reuse `MonitorView` for the trace.
- **WS4 — Results viewer (P1).** `OskeResultsScreen` listing + detail from `OskeResultStore`.
- **WS5 — Answer-key constructor (P1).** `OskeConstructorScreen`/`ViewModel`: specialty + ECG
  pick, mark correct answers, save key. The piece Николай needs to author keys with teachers.
- **WS6 — Editable question templates (P2).** Let the constructor add/remove blocks & options and
  set `PassFraction`, so the form survives accreditation changes without a code release. Bump the
  form `Version` and keep grading tolerant of unknown/removed ids.
- **WS7 — Admin / student DB (deferred).** Per Николай, later. The file layout (results JSON with
  ФИО/группа) is forward-compatible with a later import into a DB.

### Sequencing
WS1 → WS2 → WS3 give a working, gradable exam with file-saved results (the whole loop Николай
described). WS4–WS5 complete the «результаты» sub-section and the authoring tool. WS6/WS7 follow
product feedback.

## Risks / open points
- **«Выберите минимальную и максимальную ЧСС»** (form §2/§3) literally asks for *two* picks (min and
  max range). The PDF still renders one ☐ column; MVP models it as a single range select, but the
  form schema can carry two linked questions (`hr_min` / `hr_max`) if Николай wants the true min/max
  pair — a data-only change, no code impact.
- **Pass threshold / weighting.** The PDF auto-grades the conclusion but doesn't publish a numeric
  threshold or per-block weights. `PassFraction` defaults to 1.0; confirm the real rule with the
  teacher (some blocks may be "critical" / weighted) — extensible in the form schema.
- **ECG ↔ answer-key coverage.** Only ECGs with an authored key can be examined under a given
  specialty; the start dialog should list **only** ECGs that have a key for the chosen specialty (so
  WS5 gates WS3's bank in practice). Until keys exist, exam mode shows an empty bank.
- **Localization of the form.** Form text is Russian clinical terminology; we keep it in the seed
  data as-is (not translated through `AppStrings`). UI chrome is localized.

---

## Appendix — exact form content (seed data, from PDF §15)

`single` = one answer; `multi` = several allowed. Option ids are suggestions (stable keys); titles are
verbatim Russian from the PDF.

### Form A — `therapy` (Терапия, §15.1) — 10 blocks
1. **Ритм** (single): Синусовый · Нижнепредсердный · Миграция водителя ритма по предсердиям ·
   Фибрилляция предсердий · Трепетание предсердий · АВУРТ
2. **Минимальная и максимальная ЧСС (ЧСЖ)** (single range): Менее 50 · От 50 до 101 · 101 и более
3. **Наджелудочковая экстрасистолия (НЖЭС)** (single): Нет · Единичная НЖЭС · Куплет · Триплет
4. **Желудочковая экстрасистолия (ЖЭС)** (single): Нет · Редкая одиночная ЖЭС · Куплет · Триплет
5. **Оценка атриовентрикулярной проводимости** (single): Нет нарушений · АВ-блокада 1 степени ·
   АВ-блокада 2 степени Мобиц 1 · АВ-блокада 2 степени Мобиц 2 · АВ-блокада 3 степени · Трепетание
   предсердий с переменным коэффициентом проведения · Трепетание предсердий · Невозможно оценить
   АВ-проводимость · Имеются признаки дополнительного проводящего пути
6. **Оценка внутрижелудочковой проводимости** (single): Нет нарушений · Полная блокада правой ножки
   пучка Гиса (ПБПНПГ) · Полная блокада левой ножки пучка Гиса (ПБЛНПГ) · Блокада передней ветви левой
   ножки пучка Гиса (БПВЛНПГ) · Увеличение длительности комплекса QRS из-за дельта-волны
7. **Оценка гипертрофии левого желудочка** (single): Достоверных признаков гипертрофии ЛЖ нет ·
   Имеются достоверные признаки гипертрофии ЛЖ
8. **Наличие патологического зубца Q** (single): Нет патологического Q зубца · Есть патологический Q зубец
9. **Признаки острого/подострого ИМпST** (single): Нет убедительных признаков острого/подострого
   ИМпST · Передне-перегородочный · Передне-верхушечный · Распространенный передний · Нижний ·
   Нижнебоковой
10. **Дополнительная информация по данной ЭКГ** (single): Нет · Полная блокада ножки пучка Гиса ·
    Синдром Вольфа-Паркинсона-Уайта (WPW) · Нельзя исключить ОКС без подъёма ST (ОКСбпST) · Нельзя
    исключить ОКС с подъёмом ST (ОКСпST)

### Form B — `cardiology` (Кардиология / Функциональная диагностика, §15.2) — 13 blocks
1. **Ритм** (single) — same options as Form A §1.
2. **Электрическая ось сердца (ЭОС)** (single): В норме · Отклонение влево · Отклонение вправо
3. **Минимальная и максимальная ЧСС (ЧСЖ)** (single range): Менее 50 · От 50 до 101 · 101 и более
4. **Наджелудочковая экстрасистолия (НЖЭС)** (single): Нет · Единичная НЖЭС · Куплет · Триплет
5. **Желудочковая экстрасистолия (ЖЭС)** (single): Нет · Редкая одиночная ЖЭС · Куплет · Триплет
6. **Оценка атриовентрикулярной проводимости** (single) — same 9 options as Form A §5.
7. **Оценка внутрижелудочковой проводимости** (single) — same as Form A §6.
8. **Оценка гипертрофии левого желудочка** (single) — same as Form A §7.
9. **Динамика сегмента ST** (**multi** — «возможно несколько ответов»): Нет · Депрессия сегмента ST ·
   Изменения, характерные для ОКСпST · Характерная для блокады ножки пучка Гиса депрессия ST ·
   Характерная для блокады ножки пучка Гиса элевация ST · Наличие аритмии затрудняет оценку ST
10. **Наличие патологического зубца Q** (single): Нет патологического Q зубца · Есть патологический Q зубец
11. **Оценка зубца Т** (single): Нет нарушений · Высокий заострённый · Отрицательный · Изменения зубца
    T, характерные для блокады ножки пучка Гиса · Наличие аритмии затрудняет оценку зубца Т ·
    Изменения зубца Т, характерные для ОКС · Двухфазный Т зубец
12. **Признаки острого/подострого ИМпST** (single) — same 6 options as Form A §9.
13. **Дополнительная информация по данной ЭКГ** (single) — same as Form A §10.
