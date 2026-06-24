# Plan: Port the Question Bank + Individual/Group test sessions to Android

**Created:** 2026-06-24
**Status:** NOT STARTED
**Direction:** **Windows → Android** (reverse of the usual). These features were built in the WinUI 3
port first; the Android app must now catch up. The Windows port is the **reference implementation** for
behavior, data shapes, and visuals — match it feature-for-feature, adapting idioms to Kotlin/Compose.

**Target (Android) source root:** `<CardioSimulator repo>/app/src/main/java/com/example/cardiosimulator/`
(Jetpack Compose, Kotlin, `master`).
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`
**Contracts:** `CardioSimulator/docs/architecture.md` and `docs/cross-platform-app-prompt.md` (both repos).

## Context

The customer specified a full testing system across three slides (question bank; question = text/image/ECG
with themes+tags+id; AI JSON import/export) and a test-administration flow («Формирование теста»:
Individual vs Group, generating 10/20/30-question tests from the bank, Group via a QR + LAN server). All of
it now exists on Windows. On Android the Testing / Examination / Test-Constructor screens are still
**placeholders** (`TestingScreen`/`TestingControlPanel` were stubs), so this is largely **net-new** on
Android, built to mirror the Windows implementation — not a small enhancement.

This plan has two parts: **A. Question Bank** (authoring + storage + import/export) and **B. Test
administration** (Individual + Group sessions). Part B depends on Part A.

---

## Shared data & schema — keep the JSON byte-compatible

The Windows on-disk JSON is the interchange format (especially for **AI-generated question import/export**).
Android must read/write the **same schema** (camelCase keys, string enums, omit-nulls) so a bank exported on
one platform imports on the other and the AI targets one schema. Reference: `TestJson.cs` options
(camelCase, `JsonStringEnumConverter`, `WhenWritingNull`, relaxed escaping). Use **kotlinx.serialization**
with `@SerialName`/`encodeDefaults=false`/`explicitNulls=false` and an enum serializer to match.

Storage layout mirrors Windows `Data/AppPaths.cs`, rooted at Android `filesDir` instead of `%LOCALAPPDATA%`:
- `filesDir/tests/<id>.json` — one test per file.
- `filesDir/tests/bank/<questionId>.json` — one bank question per file (+ `themes.json` in the same dir).
- `filesDir/tests/images/<id>.<ext>` — copied image stimuli.
- `filesDir/tests/results/<attempt>.json` — graded attempts (already the exam-results pattern).

---

# PART A — Question Bank

## A1. Domain model (Kotlin data classes)
Reference: `Core/Domain/Test.cs`, `Core/Domain/Exam.cs`, `Core/Domain/ExamGrader.cs`, `Core/Domain/TestSeed.cs`.
- `TestOption(id, text)`, `TestQuestion(id, number, text, options, correctOptionId, comment, pathologyId?,
  leads?, scheme=Grid, imagePath?, theme?, tags?)` with derived `stimulus` (`Text/Image/Ecg`) and a
  `tagList` accessor. `Test(testId, title, questions, questionTimeSeconds=0)`.
- `QuestionStimulus { Text, Image, Ecg }` (derived: image if `imagePath`, else ecg if `pathologyId`, else text).
- Exam types: `ExamStudentInfo`, `ExamQuestionResult`, `ExamResult`; `ExamGrader.grade(test, selections,
  student)` (pass fraction 0.6) — pure.

## A2. Data layer
Reference: `Core/Data/{TestJson,ITestSource,FileTestSource,TestRepository,IQuestionBankSource,
FileQuestionBankSource,QuestionBankRepository,TestThemeStore}.cs`.
- Test store: one JSON per test under `tests/`; repository with cache + change notification (Flow/LiveData).
- **Question bank**: one JSON per question under `tests/bank/` (the non-recursive test scan ignores
  subfolders — replicate that). `QuestionBankRepository` with `import(list)` (overwrite by id) and
  `exportAll()` → JSON array. `ReadQuestions` **skips `themes.json`**.
- **Theme catalog**: editable string list at `tests/bank/themes.json` with `seedIfMissing()` +
  `DefaultThemes` (RU defaults from `TestThemeStore.cs`).
- Seed on first run (once pathologies load): demo test + seed the bank from its questions + themes — mirror
  `AppViewModel.SeedSampleTestIfNeeded`.

## A3. Test Constructor (Compose)
Reference: `ViewModels/TestConstructorViewModel.cs`, `Screens/TestConstructorScreen.cs`.
- A **Тесты / Банк вопросов** toggle. Editing a test = title, per-question time, ordered question cards;
  the bank view = searchable/theme-filtered list with add/edit/delete + **Import JSON** / **Export JSON** +
  **Manage themes** dialog.
- Question card: shows **id**; **stimulus selector** (Text / Image / ECG) — keep stimulus as an **explicit
  `kind`** in the edit model (not derived) so the ECG picker / image button appears before a specific
  ECG/image is chosen; ECG → rhythm picker; Image → pick (SAF `GetContent`) + copy into `tests/images/`
  (mirror `Data/TestImageStore.cs`) + thumbnail; **theme** dropdown (from catalog) + **tags** field;
  options capped **4–6**; comment. A live monitor preview with a **start/stop** toggle.
- Bridge: **«Добавить из банка»** (snapshot-copy bank questions into the test) and per-card **«В банк»**.
- JSON pickers via SAF (`CreateDocument`/`OpenDocument`, `application/json`).

## A4. Testing & Examination consume the bank + image questions
Reference: `Screens/TestingScreen.cs`, `Screens/ExaminationScreen.cs`, `Controls/{TestQuestionPanel,
ExamQuestionPanel}.cs`, `ViewModels/{TestViewModel,ExaminationViewModel}.cs`.
- Build out the **self-assessment Testing** flow (counter + per-question countdown, single-choice, reveal
  comment, ✓/✗, score) and the graded **Examination** flow (no feedback, grade+save → results report) — both
  net-new on Android.
- **Image questions**: render the picture where the ECG monitor sits (Compose has no Win2D re-parenting
  constraint, so just swap the composable); ECG questions drive the monitor; text-only shows neither.

---

# PART B — Test administration («Формирование теста»)

## B1. Bank-based generation + answer-free DTO (pure)
Reference: `Core/Domain/TestGenerator.cs`, `Core/Data/QuizDto.cs`.
- `TestGenerator.generate(bank, count, theme?, rng)` → shuffle (Fisher–Yates), theme-filter, cap to
  available, renumber, synth `gen_<hex>` id + title. `CountOptions = [10,20,30]`.
- `QuizDto`: public question shape (`id, text, stimulus, options[]`) that **omits `correctOptionId` and
  `comment`** — the grading key must never leave the teacher's device. `toPublic(test)` + JSON serialize.

## B2. Individual mode (Compose)
Reference: `Screens/ExaminationScreen.cs` (Individual/Group choice + Individual dialog).
- Examination start = **Индивидуальное / Групповое** choice. Individual → register (ФИО, группа) + **count
  10/20/30** + optional **theme** → generate from the bank → run the graded flow → result saved to the
  report. Keep a "use a saved test" alternative.

## B3. Group mode — LAN server + QR + live roster
Reference: `App/Network/GroupTestServer.cs` (logic), `App/Network/GroupQuizPage.cs` (the mobile page).
- **Embedded HTTP server**: use **NanoHTTPD** (lightweight, standard for Android) — *not* a hand-rolled
  socket server. Bind `0.0.0.0:8080` (free-port fallback). Run it from a **foreground Service** so Android
  doesn't kill it mid-session; stop it when the session ends. Routes (mirror Windows exactly):
  - `GET /` → the mobile quiz page. **Reuse `GroupQuizPage.Html` verbatim** — it's platform-agnostic
    vanilla HTML/JS; drop it into an Android asset/string.
  - `POST /api/register {fullName, group}` → generate a per-student test, store under a token, return
    `{token, questions}` (answer-free `QuizDto`).
  - `GET /api/image?token=&qid=` → stream `tests/images/<file>`.
  - `POST /api/submit {token, selections}` → `ExamGrader.grade` → save to results → return `{correct,total,passed}`.
- **Session state**: thread-safe map token→participant (student + generated test + result); a state Flow the
  UI observes for the **live roster** (name · group · finished · score).
- **QR**: generate with **ZXing** (`com.google.zxing:core`, e.g. `BarcodeEncoder`) → `Bitmap` for the URL
  `http://<lan-ip>:<port>/`. LAN IPv4 via `NetworkInterface` (prefer Wi-Fi/Ethernet, skip loopback/APIPA) —
  same logic as `GroupTestServer.LocalIPv4()`; `WifiManager` is an acceptable alternative.
- **Group UI**: count + theme → Start session → QR + URL + live roster → Stop session. Results also in the
  results report. Make the server **survive navigation** (hold it in a ViewModel scoped above the screen, or
  the service) and re-attach the UI if a session is already running.
- **Permissions/manifest**: `INTERNET` (present) + a foreground-service type if used. The server speaks plain
  HTTP to clients — no client-side cleartext config needed; note the "not secure" browser warning is expected.

## B4. Lecture-end «Пройти тестирование» button
Reference: `Controls/CourseViewerPanel.cs`. Add the button to the lecture/course viewer that navigates to
the Examination (test) flow.

---

## Android tech mapping (quick reference)

| Windows | Android |
|---|---|
| System.Text.Json (`TestJson`) | kotlinx.serialization (match camelCase / omit-nulls / string enums) |
| `%LOCALAPPDATA%/CardioSimulator/...` (`AppPaths`) | `filesDir/...` |
| `FileOpenPicker` (image/JSON) | Storage Access Framework (`ActivityResultContracts`) |
| `QRCoder` `PngByteQRCode` | ZXing `BarcodeEncoder` → `Bitmap` |
| raw `TcpListener` HTTP server (`GroupTestServer`) | **NanoHTTPD** in a foreground `Service` |
| `GroupQuizPage.Html` | reuse the same HTML string verbatim |
| Win2D monitor + Visibility-swap rules | Compose Canvas; just swap composables (no re-parent constraint) |
| `AppStrings` keys (`test_*`, `bank_*`, `exam_*`, `teaching_take_test`) | `strings.xml` (RU/EN/ZH/ES) — port the same keys |

## Out of scope / notes
- **ECG-on-phone**: Group quizzes show ECG-stimulus questions as **text only** on phones (no Compose Canvas
  in the browser). Same limitation as Windows; a future enhancement could pre-render an ECG PNG server-side.
- Keep the **JSON schema identical** to Windows so banks/tests are cross-platform interchangeable.

## Acceptance criteria
- A bank exported from Windows imports on Android (and vice-versa) unchanged; questions carry id, theme,
  tags, and text/image/ECG stimulus.
- Test Constructor: author/import/export bank questions, manage themes, build tests (incl. add-from-bank);
  Testing + Examination run and save results; image questions show the picture.
- Individual mode generates a 10/20/30 test from the bank and records the result.
- Group mode: Start session → a phone on the same Wi-Fi scans the QR → registers → takes an
  individually-generated test → submits → appears in the live roster and in the results report.
- Behavior matches the Windows port feature-for-feature; strings localized RU/EN/ZH/ES.
