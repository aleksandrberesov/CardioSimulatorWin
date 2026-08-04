# Plan: Port Course Language-Fallback Fix + Load Report to Android

**Created:** 2026-08-04
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goals

An instructor reported a course pack that **"loads empty — only the structure appears."** On Windows the
course tree (manifest + `course.txt` lecture list) rendered, but every lecture body was blank. Root cause,
confirmed against the real file (`course44.pak`, 1 course / 66 lectures):

- Every lecture file is named `<id>.ru.html` (Russian content).
- But the course's `course.txt` declares `language: en`.
- `readLecture` asked for `<id>.en.html`, and the language fallback only ever retried `en` — so it **never
  looked at the `.ru.html` files**. All lectures read empty even though the content was present.

Two things came out of this on Windows, and this plan ports the **two portable** ones to Android:

1. **Language-fallback fix (PRIMARY).** When the requested language and the `en` fallback both miss, fall
   back to whatever language the lecture files **actually** use (discovered from the files on disk). **Android
   has the identical bug** — `FileCourseSource.readLecture` uses the same two-entry `fallbackLanguages()`.
2. **Load report (visibility).** After an explicit "Change course pack" import, show a progress spinner then a
   report: courses loaded, per-course lecture counts, a short **content preview read from a real lecture**, and
   a warning when the manifest lists lectures but none yield readable content. This is what made the bug
   visible on Windows in the first place.

**Explicitly NOT ported (see Part D):** the Windows "overlay keyed by pack identity" fix and
`ContentCrypto.TryReadPackIdentity`. Those address the *encrypted writable overlay* layer, which Android does
not have — `CourseZipExtractor.extract()` wipes the target dir on every re-import, so no stale deltas can
survive. Revisit only if/when Android adopts the CSP2 overlay model (see
`2026-07-android-content-pack-csp2-parity.md`).

**Reference (Windows) changes:**
- `src/CardioSimulator.Core/Data/EncryptedCourseSource.cs` — `FallbackLanguages(courseId, lectureId, language)` + `AvailableLanguages(...)`
- `src/CardioSimulator.App/ViewModels/AppViewModel.cs` — `SetCourseFolderAsync` returns a `CourseLoadReport`; `BuildCourseLoadReport(...)`
- `src/CardioSimulator.App/ViewModels/CourseLoadReport.cs` — report DTOs
- `src/CardioSimulator.App/Screens/CourseLoadReportDialog.cs` — progress → report dialog
- Tests: `tests/CardioSimulator.Core.Tests/CourseLanguageFallbackTests.cs`

---

## 2. Part A: Language-Fallback Fix (PRIMARY — same bug on Android)

**Target files:**
- `app/src/main/java/com/example/cardiosimulator/data/CourseSource.kt` — the shared `fallbackLanguages(language)` helper
- `app/src/main/java/com/example/cardiosimulator/data/FileCourseSource.kt` — `readLecture(...)`

### A.1 The current Android bug

`CourseSource.kt`:
```kotlin
internal fun fallbackLanguages(language: String): List<String> =
    listOfNotNull(language.takeIf { it != COURSE_FALLBACK_LANG }, COURSE_FALLBACK_LANG)
```

`FileCourseSource.readLecture` walks only `[requested, "en"]`, so a course declaring `en` whose files are
`.ru.html` reads every lecture as null. (`listLectures` scans the directory directly, so the tree still shows
— hence "structure loads, content empty".)

### A.2 The fix

Discover the languages that actually exist for the lecture and append them to the fallback list (after the
requested language and `en`, de-duplicated). Because `FileCourseSource` knows its `root`, do the discovery
there rather than in the pure `fallbackLanguages(language)` helper.

Edit `FileCourseSource.readLecture`:

```kotlin
override fun readLecture(courseId: String, lectureId: String, language: String): Lecture? {
    for (lang in fallbackLanguages(courseId, lectureId, language)) {
        val file = File(root, "$courseId/lectures/$lectureId.$lang.html")
        if (!file.canRead()) continue
        return runCatching {
            CourseParser.parseLecture(file.readText(Charsets.UTF_8), courseId, lang)
        }.getOrNull()
    }
    return null
}

/**
 * Languages to try for a lecture, in order: the requested one, then the "en"
 * fallback, then whatever language suffixes actually exist on disk for this
 * lecture. The last step saves a course whose declared `language` does not
 * match its files (e.g. `language: en` but `<id>.ru.html`) — without it the
 * requested/en probes both miss and the lecture reads as empty even though the
 * body is right there under another suffix. Only reached when the exact probes
 * miss, so a well-formed course pays nothing.
 */
private fun fallbackLanguages(courseId: String, lectureId: String, language: String): List<String> {
    val ordered = LinkedHashSet<String>()
    ordered.add(language)
    ordered.add(COURSE_FALLBACK_LANG)
    ordered.addAll(availableLanguages(courseId, lectureId))
    return ordered.toList()
}

/** Language suffixes present for a lecture, from its `<id>.<lang>.html` files. */
private fun availableLanguages(courseId: String, lectureId: String): List<String> =
    File(root, "$courseId/lectures")
        .listFiles { f -> f.isFile && f.name.startsWith("$lectureId.") && f.name.endsWith(".html") }
        ?.mapNotNull { f ->
            // "<lectureId>.<lang>.html" -> "<lang>"; skip ".answers.json" (not .html) and dotted remainders.
            val lang = f.name.removePrefix("$lectureId.").removeSuffix(".html")
            lang.takeIf { it.isNotEmpty() && !it.contains('.') }
        }
        ?: emptyList()
```

**Keep the old pure `fallbackLanguages(language)` in `CourseSource.kt`** if any other `CourseSource`
implementation still uses it; only `FileCourseSource` needs the file-aware overload. (Grep first:
`grep -rn "fallbackLanguages" app/src/main`. As of this writing only `FileCourseSource` calls it.)

**Precedence to preserve:** requested language must still win when its file exists. `LinkedHashSet` insertion
order (requested → en → discovered) guarantees this; the discovered entries only add *new* languages.

### A.3 Parity test

Mirror `tests/CardioSimulator.Core.Tests/CourseLanguageFallbackTests.cs`. Create a course dir where
`course.txt` declares `language: en` but the only lecture file is `<id>.ru.html`, then assert:
- `readLecture(courseId, id, "en")` returns non-null, `.language == "ru"`, body contains the RU marker.
- With **both** `.en.html` and `.ru.html` present, `readLecture(..., "en")` returns the **en** body (requested
  language still wins — no shadowing by the fallback).

Android test root: `app/src/test/java/com/example/cardiosimulator/` (JVM unit test, no emulator needed —
`FileCourseSource` is plain `java.io.File`).

---

## 3. Part B: Load Report After Course Import

Goal: after "Change course pack", show the user **what actually loaded** — including a content preview read
from a real lecture, which distinguishes a good pack from a "structure-only / empty" one.

**Target files:**
- `app/src/main/java/com/example/cardiosimulator/ui/viewmodels/AppViewModel.kt` — build + expose the report
- `app/src/main/java/com/example/cardiosimulator/ui/dialogs/SettingsDialog.kt` — trigger + render the report
- `app/src/main/res/values*/strings.xml` — new strings

### B.1 Report model + builder (AppViewModel)

Add a data class (Kotlin equivalent of Windows `CourseLoadReport` / `CourseLoadSummary`):

```kotlin
data class CourseLoadReport(
    val success: Boolean,
    val fileName: String,
    val courses: List<CourseLoadSummary>,
    val totalLectures: Int,
    val previewCourseTitle: String?,
    val previewLectureTitle: String?,
    val previewSnippet: String?,
) {
    val courseCount get() = courses.size
    // manifest advertises lectures but none yielded readable body text
    val structureWithoutContent get() =
        success && totalLectures > 0 && previewSnippet.isNullOrEmpty()
}

data class CourseLoadSummary(val title: String, val lectureCount: Int, val languages: List<String>)
```

Build it after a load (reference: Windows `AppViewModel.BuildCourseLoadReport`). Read a **real lecture** for
the preview — that is the whole point:

```kotlin
private fun buildCourseLoadReport(fileName: String, loaded: Boolean): CourseLoadReport {
    val repo = courseRepository
    if (!loaded || repo == null)
        return CourseLoadReport(false, fileName, emptyList(), 0, null, null, null)

    val summaries = mutableListOf<CourseLoadSummary>()
    var total = 0
    var pc: String? = null; var pl: String? = null; var ps: String? = null

    for (entry in repo.courses()) {                     // manifest entries
        val course = repo.readCourse(entry.id)
        val count = course?.lectures?.size ?: entry.lecturesCount
        total += count
        summaries.add(CourseLoadSummary(displayTitle(entry.nameRu, entry.titleEn, entry.id),
                                        count, course?.languages ?: emptyList()))
        if (ps != null || course == null) continue
        for (item in contentItems(course)) {            // lectures + leaf Темы
            val lang = course.languages.firstOrNull() ?: "en"
            val text = repo.readLecture(entry.id, item.id, lang)?.rawHtml?.let { plainTextPreview(it, 400) }
            if (!text.isNullOrBlank()) {
                pc = displayTitle(entry.nameRu, entry.titleEn, entry.id)
                pl = displayTitle(item.nameRu, item.titleEn, item.id); ps = text; break
            }
        }
    }
    return CourseLoadReport(true, fileName, summaries, total, pc, pl, ps)
}
```

Port the small helpers from Windows `AppViewModel`:
- `contentItems(course)` — course lectures, then leaf Темы (`topic.isLeaf`) as lecture items (mirror
  `Course.ContentItem` / the Android `Course.kt` equivalent).
- `displayTitle(nameRu, titleEn, id)` — `if (selectedLanguage == RU && !nameRu.isNullOrBlank()) nameRu else titleEn.ifBlank { nameRu ?: id }`.
- `plainTextPreview(html, 400)` — strip tags (`Regex("<[^>]+>")` → " "), decode entities
  (`androidx.core.text.HtmlCompat.fromHtml` or a small decode), collapse whitespace, truncate with `…`.

Expose the report as a one-shot event so the dialog can show it. Add:
```kotlin
private val _courseLoadReport = MutableStateFlow<CourseLoadReport?>(null)
val courseLoadReport: StateFlow<CourseLoadReport?> = _courseLoadReport.asStateFlow()
fun clearCourseLoadReport() { _courseLoadReport.value = null }
```
Set `_courseLoadReport.value = buildCourseLoadReport(fileName, loaded)` at the end of `setCourseDataFolder`'s
coroutine (derive `loaded` from whether `loadCoursesFromSaf` reached `DataState.Ready`; have it return
`Boolean`, or read `_courseDataState.value is DataState.Ready` after it). Get a display file name from the
picked `Uri` (`DocumentFile.fromSingleUri(context, uri)?.name ?: "course pack"`).

> Note: `reloadCourses` currently gates only on `repo.courses().size` (manifest count), so it reports
> `Ready` even for a content-empty pack — the same blind spot Windows had. The report's `structureWithoutContent`
> flag is what surfaces that; do **not** try to make `reloadCourses` fail on empty content (a pack may be
> legitimately structure-heavy), just show the warning.

### B.2 Report dialog (Compose, SettingsDialog)

In `SettingsDialog.kt`, collect `courseLoadReport` and show a dialog when non-null (reference:
`CourseLoadReportDialog.cs`). Because `setCourseDataFolder` already calls `onDismiss()`, host the dialog at a
level that survives the settings sheet closing (e.g. in the screen that owns `AppViewModel`), or keep the
settings sheet open until the report shows. Sketch:

```kotlin
val report by appViewModel.courseLoadReport.collectAsState()
report?.let { r ->
    AlertDialog(
        onDismissRequest = { appViewModel.clearCourseLoadReport() },
        confirmButton = { TextButton(onClick = { appViewModel.clearCourseLoadReport() }) {
            Text(stringResource(R.string.settings_close)) } },
        title = { Text(stringResource(
            if (r.success) R.string.course_load_title else R.string.course_load_failed_title)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState())) {
                Text(r.fileName, style = MaterialTheme.typography.bodySmall)
                when {
                    !r.success -> Text(stringResource(R.string.course_load_failed_body))
                    r.courseCount == 0 -> Text(stringResource(R.string.course_load_no_courses))
                    else -> {
                        Text(stringResource(R.string.course_load_summary_format, r.courseCount, r.totalLectures),
                             style = MaterialTheme.typography.titleMedium)
                        if (r.structureWithoutContent)
                            WarningBox(stringResource(R.string.course_load_empty_warning))  // amber Surface
                        r.courses.forEach { c ->
                            val langs = if (c.languages.isNotEmpty()) "   [${c.languages.joinToString()}]" else ""
                            Text("•  ${c.title}  —  " +
                                 stringResource(R.string.course_load_lectures_format, c.lectureCount) + langs)
                        }
                        if (!r.previewSnippet.isNullOrEmpty()) PreviewBox(r)  // header + crumb + snippet
                    }
                }
            }
        },
    )
}
```

Loading phase: reuse the existing `courseDataState == DataState.Loading` to show a spinner (the Windows dialog
shows a `ProgressRing` first; on Android the settings/data-source screen already reacts to `DataState.Loading`).

### B.3 Strings

Add to `app/src/main/res/values/strings.xml` (English), and Russian
(`values-ru/strings.xml`). Other locales fall back to English automatically, matching the Windows approach
(where zh/es/hi fall back). Keys (English values shown):

| key | value |
|---|---|
| `course_load_title` | Course pack loaded |
| `course_load_failed_title` | Couldn't load course pack |
| `course_load_summary_format` | `%1$d courses · %2$d lectures` |
| `course_load_lectures_format` | `%1$d lectures` |
| `course_load_preview_header` | Content preview |
| `course_load_empty_warning` | This pack lists courses but no lecture content could be read — it may be empty or damaged. |
| `course_load_failed_body` | This file couldn't be opened as a course pack. Your current courses are unchanged. |
| `course_load_no_courses` | The pack loaded but contains no courses. |

Russian (from the Windows `Ru` table): `Пакет курсов загружен`, `Не удалось загрузить пакет курсов`,
`Курсов: %1$d · лекций: %2$d`, `лекций: %1$d`, `Предпросмотр содержимого`,
`В пакете есть курсы, но не удалось прочитать ни одной лекции — возможно, он пуст или повреждён.`,
`Не удалось открыть файл как пакет курсов. Текущие курсы не изменены.`,
`Пакет загружен, но не содержит курсов.`

---

## 4. Part D: What NOT to Port (and why)

- **Overlay keyed by pack identity** (Windows `AppPaths.CourseOverlayPakFor` → identity via
  `ContentCrypto.TryReadPackIdentity`). Windows keeps instructor edits in an encrypted *writable overlay*
  keyed by file path; re-exporting to the same path made a new pack inherit the old pack's tombstones/edits.
  **Android has no such overlay** — `FileCourseSource` writes edits straight into the extracted `courses/`
  dir, and `CourseZipExtractor.extract()` **wipes that dir on every re-import** (see its KDoc: "Wipes
  targetDir first so a re-import doesn't leave stale files"). So the stale-state hazard cannot occur. **Do not
  add pack-identity keying.** If Android later adopts the CSP2 encrypted-overlay model
  (`2026-07-android-content-pack-csp2-parity.md`), revisit this then.
- **`ContentCrypto.TryReadPackIdentity`** — no encrypted packs on Android; nothing to read an identity from.

---

## 5. Part E: Verification

### 5.1 Automated
1. Run the new JVM unit test from Part A.3 (`./gradlew testDebugUnitTest`).
2. Confirm existing course tests still pass (grep `app/src/test` for `Course`/`Lecture`).

### 5.2 Manual (emulator/device)
1. Build a "mismatched" course ZIP: `manifest.txt`, one `<course>/course.txt` with `language: en`, and
   lectures as `<id>.ru.html` (no `.en.html`). A quick way: export a real Russian course, edit `course.txt`'s
   `language:` line to `en`, re-zip. (Or just use the instructor's `course44` bundle.)
2. App → Settings → **Change course pack** → pick the ZIP.
3. **Expected before fix:** course tree shows, lectures open blank.
4. **Expected after Part A:** lecture bodies render (Russian content) even though the course declares `en`.
5. **Expected after Part B:** a report dialog appears showing `1 course · N lectures`, the course row, and a
   short text **preview from a real lecture**. With a genuinely empty/damaged pack, the amber
   `course_load_empty_warning` shows instead.
6. Re-import a *different* course ZIP to the same source and confirm the previous bundle's courses are gone
   (proves the extract-and-wipe still holds — no Android overlay-staleness).

---

## 6. Commit

Suggested message:
```
fix(courses): fall back to on-disk lecture language; add post-import load report

Ports Windows fixes: readLecture now tries the languages actually present as
<id>.<lang>.html files (a course declaring `language: en` with .ru.html files
no longer reads empty), and Change-course-pack shows a report with per-course
counts and a real lecture preview so an empty/mismatched pack is visible.
```
