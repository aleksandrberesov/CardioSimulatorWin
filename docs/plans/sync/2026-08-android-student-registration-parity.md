# Plan: Port the Student Registration screen (+ exam roster pick-list) to Android

**Created:** 2026-08-13
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goals

Two linked changes were made on Windows:

1. **A new Full-edition-only operating mode, «Регистрация студентов» (Students).** A screen where an
   instructor adds/registers students (ФИО + группа + optional e-mail) onto a **persisted roster**, lists
   the already-registered students, and deletes entries. The mode is **hidden entirely in the Limited
   (student) build** — same mechanism the authoring/constructor modes use.
2. **The Examination → Individual dialog now offers the roster as a pick-list.** When the roster is
   non-empty, a "Registered student" dropdown appears at the top of the individual-exam setup dialog;
   choosing an entry pre-fills the ФИО + группа fields (still editable). Empty roster → the dialog is
   unchanged.

The roster identity (`fullName` + `group`) maps 1:1 onto the existing `ExamStudentInfo` the exam pipeline
already grades against — the roster just lets a teacher enter students once instead of re-typing per attempt.

**Reference (Windows) changes — mirror these:**
- `Core/Domain/OperatingMode.cs` — new `Students` enum value (appended **last**); new `IsFullEditionOnly()`
  extension (`IsConstructor() || == Students`); `TitleResourceKey` → `mode_students`.
- `App/ViewModels/AppViewModel.cs` — the mode-builder gate now filters on `IsFullEditionOnly()` (was
  `IsConstructor()`); new `StudentStore` property + construction.
- `Core/Domain/Student.cs` — **new** `Student` record + `Student.Create(...)` factory.
- `Core/Data/StudentStore.cs` — **new** single-file JSON roster store (atomic write, de-dupe by name+group).
- `App/Data/AppPaths.cs` — `StudentsFile` path (`%LOCALAPPDATA%/CardioSimulator/students.json`).
- `App/ViewModels/StudentRegistrationViewModel.cs` — **new** (register / remove / list + `RegisterOutcome`).
- `App/Screens/StudentsScreen.cs` — **new** form + roster list UI.
- `App/Screens/MainScreen.xaml.cs` — `case OperatingMode.Students` builds the screen.
- `App/Screens/ExaminationScreen.cs` — roster pick-list in `ShowIndividualDialogAsync`.
- `App/Localization/AppStrings.cs` — `mode_students`, the `students_*` block, and `exam_pick_student` /
  `exam_pick_student_manual` (En + Ru; `mode_students` in zh/es/hi too).

---

## 2. Part A: Operating mode + edition gate

**Target:** `domain/OperatingModeModel.kt`

- Append **`Students(R.string.mode_students)` last** in the `OperatingMode` enum (after `LearningScale`), so
  existing modes keep their ordering. Note the Android enum order already differs from Windows — that's fine,
  just append at the end.
- Add a new extension mirroring the Windows `IsFullEditionOnly`:
  ```kotlin
  val OperatingMode.isFullEditionOnly: Boolean
      get() = isAuthoring || this == OperatingMode.Students
  ```
  Leave `isAuthoring` unchanged (Students is **not** authoring — it must not pick up any
  constructor-specific behaviour that keys off `isAuthoring`).
- **Change the three mode-filter sites** from `!it.isAuthoring` to `!it.isFullEditionOnly` so the Limited
  build drops Students too:
  - `MainActivity.kt:47` and `MainActivity.kt:111`
  - `ui/screens/MainScreen.kt:438`

  (Each currently reads `.filter { !AppEdition.IS_LIMITED || !it.isAuthoring }`.)

## 3. Part B: Domain model + roster store

**Target (new):** `domain/Student.kt`
```kotlin
@Serializable
data class Student(
    val id: String,
    val fullName: String,
    val group: String,
    val email: String? = null,
    val registeredAt: Long,          // epoch millis (Windows uses DateTimeOffset)
)
```
Add a factory mirroring `Student.Create`: trim inputs, return `null` when `fullName` or `group` is blank,
else `Student(UUID.randomUUID().toString(), name, group, email?.ifBlank{null}, System.currentTimeMillis())`.
`fun toExamInfo() = ExamStudentInfo(fullName, group)`.

**Target (new):** `data/StudentStore.kt` (or add to `data/TestData.kt` beside `TestThemeStore`).
Model it on **`TestThemeStore`** (single-file JSON list, atomic write) — **not** the one-file-per-entry
`ExamResultStore`. Use the shared `testJson` serializer.
```kotlin
class StudentStore(private val file: File) {
    fun list(): List<Student>              // newest first; empty on missing/corrupt
    fun add(student: Student): Boolean      // false if same fullName+group already present (case-insensitive)
    fun remove(id: String): Boolean
    fun contains(fullName: String, group: String): Boolean
}
```
De-dup rule: same `fullName.trim()` **and** `group.trim()`, case-insensitive. Sort `list()` by
`registeredAt` descending. Writes atomic (temp file + rename), directory created if missing.

## 4. Part C: AppViewModel wiring

**Target:** `ui/viewmodels/AppViewModel.kt`

- Add `val studentStore: StudentStore? = null` to `AppViewModelState` (beside `testThemeStore` /
  `examResultStore` around lines 139–143).
- In the factory/初始化 that builds the other stores, construct it with
  `StudentStore(File(ctx.filesDir, "students.json"))`. No seeding (empty roster is the correct default).

## 5. Part D: Registration screen + view-model

**Target (new):** `ui/viewmodels/StudentRegistrationViewModel.kt`
Thin wrapper over `StudentStore`, exposing the roster as a `StateFlow<List<Student>>` (so the screen
recomposes on add/remove) and:
```kotlin
enum class RegisterOutcome { Added, Invalid, Duplicate, SaveFailed }
fun register(fullName: String, group: String, email: String?): RegisterOutcome
fun remove(id: String)
```
`register`: `Student.create(...) ?: return Invalid`; `store.contains(...) -> Duplicate`;
`!store.add(...) -> SaveFailed`; else refresh state + `Added`. Validation/de-dup live in the domain/store so
the same rules apply here and in the exam dialog.

**Target (new):** `ui/screens/StudentsScreen.kt` (Compose). Reproduce `StudentsScreen.cs`, theme-aware:
- A **form card**: title (`students_title`) + subtitle (`students_subtitle`); `OutlinedTextField`s for ФИО
  (`exam_field_full_name`, reused), группа (`exam_field_group`, reused), e-mail (`students_field_email`);
  a **Register** button (`students_register`) enabled only when name + group are non-blank; an inline status
  line that shows `students_added` (success, green) / `students_duplicate` / `students_invalid` /
  `students_save_failed` (error, alert-red `#D33A2F`). On success, clear the fields + refocus name.
- A **roster card**: header `"$students_list_title (${count})"`; each row shows `fullName` (semibold) and a
  secondary line `group · email · yyyy-MM-dd` with a trailing delete (✕) button (tooltip/`contentDescription`
  = `students_remove`). Empty roster → `students_empty`.

**Target:** `ui/screens/MainScreen.kt` — add `OperatingMode.Students -> StudentsScreen(...)` to the screen
router (mirror the `OperatingMode.LearningScale ->` case ~line 363) and a no-op branch in the control-panel
`when` (~line 412, like LearningScale). Build the VM with the app's `studentStore` (mirror how
`LearningScaleViewModel` is created ~line 237, or construct inline from `appViewModel`).

## 6. Part E: Examination Individual dialog pick-list

**Target:** `ui/screens/ExaminationScreen.kt` (the individual/group setup dialog — the composable around
lines 260–346 with `mode`, `name`, `group` states and `onStartIndividual`).

- Read the roster once: `val roster = appViewModel.studentStore?.list().orEmpty()` (the dialog already reads
  `appViewModel.testThemeStore?.readThemes()`, so it has `appViewModel` in scope — thread it in if not).
- When `mode == "Individual"` **and** `roster.isNotEmpty()`, render an **`ExposedDropdownMenuBox`** above the
  ФИО field, labelled `exam_pick_student`. First item `exam_pick_student_manual` (keep current text);
  remaining items `"${s.fullName} · ${s.group}"`. Selecting a student sets `name = s.fullName;
  group = s.group` (fields stay editable). Empty roster → render nothing new (dialog unchanged).
- Leave the existing enable rule (`name.isNotBlank() && group.isNotBlank()`), source (generate/saved),
  count/theme, and `onStartIndividual` untouched.

## 7. Part F: Strings

Add to `res/values/strings.xml` (En) + `res/values-ru/strings.xml` (Ru); add `mode_students` to
`values-zh`, `values-es`, `values-hi` too (the `students_*` and `exam_pick_*` set may fall back to EN).
Windows keys are already snake_case — reuse verbatim as Android string names. **No format specifiers** in
this set (the count header is composed in code), so no `%1$s` conversions are needed.

Keys + values (from the Windows `En`/`Ru` tables in `AppStrings.cs`):

| key | En | Ru |
|---|---|---|
| `mode_students` | Students | Студенты |
| `students_title` | Student registration | Регистрация студентов |
| `students_subtitle` | Add and register students who will take the exams. | Добавляйте и регистрируйте студентов для экзаменов. |
| `students_field_email` | E-mail (optional) | E-mail (необязательно) |
| `students_register` | Register | Зарегистрировать |
| `students_list_title` | Registered students | Зарегистрированные студенты |
| `students_empty` | No students registered yet. | Пока нет зарегистрированных студентов. |
| `students_remove` | Remove | Удалить |
| `students_added` | Student registered. | Студент зарегистрирован. |
| `students_duplicate` | This student is already registered. | Такой студент уже зарегистрирован. |
| `students_invalid` | Enter a full name and a group. | Укажите ФИО и группу. |
| `students_save_failed` | Couldn't save the student. | Не удалось сохранить студента. |
| `exam_pick_student` | Registered student | Зарегистрированный студент |
| `exam_pick_student_manual` | — Enter manually — | — Ввести вручную — |

`mode_students` for the other locales (as on Windows): zh `学生`, es `Estudiantes`, hi `छात्र`.
Remember to XML-escape the apostrophe in the En `students_save_failed` (`Couldn\'t`).

## 8. Verification

1. **Full build:** the mode picker shows **Students** (last); opening it shows the form + empty-state roster.
2. Register a student → appears in the list; the count increments; fields clear. Re-registering the same
   name+group → `students_duplicate`, no row added. Blank name/group → button disabled (and `students_invalid`
   if forced). Delete (✕) removes the row.
3. **Persistence:** kill + relaunch → roster persists (`filesDir/students.json`); delete/corrupt the file →
   empty roster, no crash.
4. **Limited build:** Students mode is **absent** from the picker and any shortcut (same as constructors).
5. **Exam pick-list:** with ≥1 registered student, Examination → Individual shows the "Registered student"
   dropdown; picking one fills ФИО + группа (still editable); manual entry still works; start/grade flow
   unchanged. With an empty roster the dialog is unchanged.
6. Light/dark both render; RU/EN switch relabels chrome.

## 9. Commit

```
feat(students): Full-edition student registration mode + exam roster pick-list

New instructor-only operating mode to register students (ФИО + группа +
optional e-mail) onto a persisted roster, list and delete them; hidden in the
Limited edition (isFullEditionOnly gate). The Examination Individual dialog
offers the roster as a pick-list that pre-fills the student fields. Ports the
Windows Student/StudentStore + StudentRegistrationViewModel/StudentsScreen and
the exam-dialog change + strings.
```
