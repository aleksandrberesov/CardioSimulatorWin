# Plan: Loading indicator when opening a lecture («Обучение») on Android

**Created:** 2026-08-11
**Status:** NOT STARTED
**Direction:** **Windows → Android**
**Depends on:** nothing (self-contained UI fix in the Teaching lecture viewer).

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goals

**Reported bug:** «Нет индикатора загрузки при открытии учебных файлов.» Открыть **Обучение → любой файл**;
ожидается индикатор загрузки, фактически — ничего, создаётся ощущение зависания.

**Cause.** Opening a lecture runs a multi-step pipeline with no visual feedback: the lecture HTML is read/
parsed, every inline `<ecg>` figure is resolved off-thread, the document is built, the WebView navigates, and
KaTeX lays out. During that gap the view just sits blank — which reads as a freeze. Same root cause on both
platforms; Windows was fixed, this ports it to Android.

**Goal.** Show a spinner + caption over the lecture area from the moment a lecture starts loading until the
WebView has finished rendering it, then reveal the content. Only in the **Teaching lecture viewer** — the
constructor preview (which re-renders on every debounced keystroke) must stay spinner-free.

**Reference (Windows) changes — already done, mirror these:**
- `src/CardioSimulator.App/Controls/LectureWebView.cs` — new **`LoadingStarted`** / **`LoadingCompleted`**
  events (UI-thread, opt-in). `LoadingStarted` fires at the top of `SetLecture` (before the off-thread render,
  and even while the WebView is still initializing and the render is deferred to `_pendingLecture`).
  `LoadingCompleted` fires on **every** terminal path so the indicator can never stick: navigation completed
  (success **or** failure), the identical-HTML short-circuit (`html == _currentHtml`, no navigation), and the
  torn-down-mid-render guard (`CoreWebView2 == null`).
- `src/CardioSimulator.App/Controls/CourseViewerPanel.cs` — a centered `ProgressRing` + `lecture_loading`
  caption in the content area. While loading, `UpdateContentArea` **collapses the WebView** and shows the
  spinner, then reveals the WebView on completion. (Windows collapses the web because its WebView2 is a native
  airspace surface that renders **above** XAML siblings, so a spinner floated on top would be hidden — see the
  panel's own comment. Collapsing also gives a clean A→B transition instead of a stale frozen frame.) The
  events are **opt-in**: the constructor preview `LectureWebView` does not subscribe, so no spinner there.
- `src/CardioSimulator.App/Localization/AppStrings.cs` — new `lecture_loading` string in all five languages.

---

## 2. Part A: Loading state in `LectureWebView`

**Target:** `ui/components/LectureWebView.kt`.

The composable already has the two load phases we need to cover:
1. **HTML build (off-thread):** `val html by produceState<String?>(initialValue = null, …)` — `html == null`
   while `EcgSvgRenderer` + `buildDocument`/`buildStandaloneDocument` run on `Dispatchers.IO`.
2. **WebView load:** the `update` block calls `web.loadDataWithBaseURL(…)`; `WebViewClientCompat.onPageFinished`
   signals completion.

Add an **opt-in** parameter and an internal loading flag (Android's WebView composes normally — no airspace
issue — so a `Box` overlay works directly; make it **opaque** to hide the stale/blank page during load, which
is what the Windows collapse achieves):

```kotlin
@Composable
fun LectureWebView(
    lecture: Lecture,
    modifier: Modifier = Modifier,
    refreshTrigger: Int = 0,
    resolveEcg: … = { _, _ -> emptyList() },
    resolveEcgSegment: … = { _, _, _, _ -> null },
    answers: Map<String, Map<String, String>> = emptyMap(),
    scrollToBlockId: String? = null,
    onCellEdit: (…)? = null,
    onMonitorClick: (() -> Unit)? = null,
    showLoadingIndicator: Boolean = false,   // NEW — Teaching viewer passes true; constructor preview leaves false
) {
    …
    // true from first composition until the first onPageFinished; re-armed on every new load.
    val webLoading = remember { mutableStateOf(true) }
    // Overall loading = HTML still building OR the WebView hasn't finished the current load.
    val loading = html == null || webLoading.value
    …
```

Wire the flag:
- In the factory's `WebViewClientCompat`, in **`onPageFinished`** set `webLoading.value = false`. Also clear it
  in **`onReceivedError`** / **`onReceivedHttpError`** so a failed load never strands the spinner (mirrors the
  Windows "clear on failure" path).
- In the `update` block, in the branch that actually issues a load (`current != null && web.tag != cacheKey`),
  set `webLoading.value = true` **before** `web.loadDataWithBaseURL(…)`. This re-arms the spinner for each
  new lecture (lecture A → B). The identical-HTML branch (`web.tag == cacheKey`) issues no load and leaves
  `webLoading` alone → no spinner for a no-op recomposition (mirrors the Windows `html == _currentHtml`
  short-circuit).

Wrap the existing `AndroidView` in a `Box` and overlay the indicator only when opted in:

```kotlin
Box(modifier) {
    AndroidView(
        modifier = Modifier.fillMaxSize(),
        factory = { … },   // unchanged
        update = { … },    // unchanged except webLoading.value = true before the load
        onRelease = { it.destroy() },
    )
    if (showLoadingIndicator && loading) {
        Box(
            modifier = Modifier
                .matchParentSize()
                .background(MaterialTheme.colorScheme.background),   // opaque → hides stale/blank page
            contentAlignment = Alignment.Center,
        ) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                CircularProgressIndicator()
                Spacer(Modifier.height(12.dp))
                Text(
                    text = stringResource(R.string.lecture_loading),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}
```

Note the `modifier` moves to the outer `Box`; the inner `AndroidView` gets `Modifier.fillMaxSize()`.

**Alternative (host-owned, closer to the Windows split):** instead of the internal overlay, expose
`onLoadingChanged: (Boolean) -> Unit` (fired from the same `loading` derivation via a `LaunchedEffect(loading)`)
and draw the spinner in `CourseViewerOverlay`. The internal opt-in flag above is simpler and keeps z-order
correct, so prefer it unless there's a reason to hoist the state.

## 3. Part B: Opt in from the Teaching lecture viewer

**Target:** `ui/screens/TeachingScreen.kt` → the private `CourseViewerOverlay` composable (the Android
equivalent of Windows `CourseViewerPanel`), where `LectureWebView` is hosted inside
`Box(Modifier.fillMaxWidth().weight(1f))`.

Pass the new flag on that one call site:

```kotlin
LectureWebView(
    lecture = lecture,
    refreshTrigger = refreshTrigger,
    resolveEcg = resolveEcg,
    resolveEcgSegment = resolveEcgSegment,
    onMonitorClick = onMonitorClick,
    showLoadingIndicator = true,          // NEW
    modifier = Modifier.fillMaxSize(),
)
```

**Leave `ui/screens/CourseConstructorScreen.kt` unchanged** — its preview `LectureWebView` keeps the default
`showLoadingIndicator = false`, so the constructor's keystroke-debounced re-renders never flash a spinner
(matches the Windows opt-in: the constructor preview doesn't subscribe to the loading events).

## 4. Part C: String

Add `lecture_loading` to `res/values/strings.xml` (EN) and each locale, matching the Windows values:

| file | value |
|------|-------|
| `res/values/strings.xml` (en) | `Loading…` |
| `res/values-ru/strings.xml`   | `Загрузка…` |
| `res/values-zh/strings.xml`   | `加载中…` |
| `res/values-es/strings.xml`   | `Cargando…` |
| `res/values-hi/strings.xml`   | `लोड हो रहा है…` |

```xml
<string name="lecture_loading">Loading…</string>
```

(Key style matches existing snake_case keys like `teaching_take_test` / `course_viewer_select_lecture`.)

## 5. Verification

1. **Обучение → select a course → open a lecture:** a centered spinner + «Загрузка…» appears immediately,
   then is replaced by the rendered lecture. No blank/frozen gap.
2. **Switch lecture A → B:** spinner re-appears for B (not left showing A's stale content), then B renders.
3. **Re-select the already-open lecture / theme is unchanged:** no spinner flash (no-op recomposition issues
   no new WebView load).
4. **Constructor preview:** typing in the block editor does **not** flash a spinner (opt-in flag stays false).
5. A lecture that fails to load does not strand the spinner (cleared in `onReceivedError`).
6. Light/dark + RU/EN both fine; the overlay background matches the theme so there's no white flash in dark.

## 6. Commit

```
fix(teaching): loading indicator when opening a lecture

Opening a lecture ran a multi-step pipeline (read/parse HTML, resolve inline
ECGs, load WebView, lay out KaTeX) with no feedback, so it looked frozen. Add
an opt-in spinner + caption over the lecture area, shown while the HTML builds
off-thread and the WebView loads, cleared on onPageFinished (and on load
error). Teaching viewer opts in; the constructor preview stays spinner-free.
Ports the Windows LectureWebView/CourseViewerPanel fix.
```
