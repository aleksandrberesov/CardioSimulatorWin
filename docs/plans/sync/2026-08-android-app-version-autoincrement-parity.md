# Plan: Port App Version + Auto-Increment to Android

**Created:** 2026-08-12
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goals

The Windows app now has a **build version that auto-increments on every build** and is **shown in-app**. Mirror it on Android.

What Windows does (reference):
- Single source of truth `version.json` (`{ "version": "1.0.1", "build": N }`) — semver edited by hand, `build` auto-increments and is committed.
- An MSBuild target (`Version.targets`) bumps `build` on every real build, stamps the assembly `FileVersion`, and generates a `BuildInfo` class the UI reads.
- The version is shown **(a)** in the window title bar (top-left, next to the app name) and **(b)** in an **About** section at the bottom of the Settings dialog.

Android goals:
1. Auto-increment a build counter on **every real build** (not on Gradle sync / `./gradlew tasks`).
2. Expose it through `BuildConfig` (Android's native equivalent of `BuildInfo`).
3. Show it in-app: an **About** section in Settings (direct parity) and — since **mobile has no OS title bar** — a small version caption on the **Welcome overlay** (the closest analog to "next to the app name", which on Windows is the title bar).

### Windows reference files
| Concern | Windows file |
|---|---|
| Version source of truth | `src\CardioSimulator.App\version.json` |
| Auto-increment mechanism | `src\CardioSimulator.App\Version.targets` (imported by the csproj) |
| Title-bar display | `src\CardioSimulator.App\MainWindow.xaml.cs` (`Title = $"{BuildInfo.Name}  v{BuildInfo.FullVersion}"`) |
| About section | `src\CardioSimulator.App\Screens\SettingsContent.cs` (`AboutSection()`) |
| Strings | `src\CardioSimulator.App\Localization\AppStrings.cs` (`settings_about`, `about_version`, `about_built`) |

**Android equivalents:** version source = `app/version.properties`; mechanism = `app/build.gradle.kts` + `BuildConfig`; About = `ui/dialogs/SettingsDialog.kt` (`SettingsContent`); welcome caption = `ui/components/WelcomeOverlay.kt`; strings = `res/values*/strings.xml`.

---

## 2. Part A — Version source of truth + auto-increment (Gradle)

### A.1 New file: `app/version.properties`
```properties
# Single source of truth for the app version.
# versionName (semver) is edited BY HAND on a real release.
# build auto-increments on every real build (see build.gradle.kts) and IS committed.
versionName=1.0.1
build=0
```
> Seed `versionName` to `1.0.1` to match the Windows baseline (`version.json`) and the WiX installer.

### A.2 Edit `app/build.gradle.kts`
`buildFeatures { buildConfig = true }` is **already enabled**, so `BuildConfig` is generated.

Add near the top of the file (before the `android { }` block):
```kotlin
import java.util.Properties

// --- Auto-incrementing build version ------------------------------------------------
// Single source of truth: app/version.properties  ->  versionName=1.0.1  /  build=N
val versionPropsFile = file("version.properties")
val versionProps = Properties().apply {
    if (versionPropsFile.exists()) versionPropsFile.inputStream().use { load(it) }
}
val semVer: String = versionProps.getProperty("versionName", "1.0.0")
val curBuild: Int = versionProps.getProperty("build", "0").toIntOrNull() ?: 0

// Bump only when a real build/assemble/install/bundle task is requested — NOT on IDE
// Gradle sync or `./gradlew tasks` (configuration runs on EVERY invocation). This is the
// Android analog of the Windows DesignTimeBuild guard in Version.targets.
val isRealBuild = gradle.startParameter.taskNames.any { name ->
    listOf("assemble", "build", "install", "bundle").any { name.contains(it, ignoreCase = true) }
}
val buildNumber: Int = if (isRealBuild) curBuild + 1 else curBuild
if (isRealBuild && buildNumber != curBuild) {
    versionProps.setProperty("versionName", semVer)
    versionProps.setProperty("build", buildNumber.toString())
    versionPropsFile.outputStream().use { versionProps.store(it, "Auto-incremented on build") }
}
val appFullVersion = "$semVer.$buildNumber"                 // e.g. "1.0.1.42"
val appBuildDate = java.time.LocalDate.now().toString()     // yyyy-MM-dd
```

Then wire it into `defaultConfig { }` (replace the current `versionCode = 1` / `versionName = "1.0"`):
```kotlin
versionCode = buildNumber                 // Android requires a monotonically increasing Int — the counter fits perfectly
versionName = appFullVersion              // -> BuildConfig.VERSION_NAME == "1.0.1.42"
buildConfigField("String", "BUILD_DATE", "\"$appBuildDate\"")
buildConfigField("String", "SEM_VERSION", "\"$semVer\"")
```
> The existing `limited` product flavor's `versionNameSuffix = "-limited"` will append to this, giving `1.0.1.42-limited` — fine, leave it.

**Caveats to note in the PR:**
- `version.properties` will show as modified after every real build (like `version.json` on Windows) — that is by design; commit it so the counter persists.
- Writing a file during the configuration phase is incompatible with Gradle's **configuration cache**. This project does not appear to enable it; if it is ever turned on, move the counter write into a small task hooked to `preBuild` (compute `buildNumber` at config, persist in the task's `doLast`).
- A failed compile still bumps here (unlike Windows, where persist runs after `CoreCompile`). Acceptable; note it. If undesired, move the write to a task that runs after `:app:compile*Kotlin`.

---

## 3. Part B — About section in Settings (direct parity)

### File: `ui/dialogs/SettingsDialog.kt` (`SettingsContent`)
Add an **About** block as the **last** item inside the scrollable `Column` (the one with `.verticalScroll(...)`), i.e. right after the `if (!AppEdition.IS_LIMITED) { ... }` data block closes (~line 454) and before that `Column` ends. Mirror the Windows `AboutSection()` (app name bold, version line, build-date line, both muted):

```kotlin
Spacer(modifier = Modifier.height(24.dp))

Text(
    text = stringResource(R.string.settings_about),
    style = MaterialTheme.typography.titleMedium,
    modifier = Modifier.padding(bottom = 8.dp)
)
Text(
    text = stringResource(R.string.app_name),
    style = MaterialTheme.typography.bodyLarge,
    fontWeight = FontWeight.SemiBold
)
Text(
    text = "${stringResource(R.string.about_version)} ${BuildConfig.VERSION_NAME}",
    style = MaterialTheme.typography.bodyMedium,
    color = MaterialTheme.colorScheme.outline
)
Text(
    text = "${stringResource(R.string.about_built)} ${BuildConfig.BUILD_DATE}",
    style = MaterialTheme.typography.bodySmall,
    color = MaterialTheme.colorScheme.outline
)
```
Add the import: `import com.example.cardiosimulator.BuildConfig`.

---

## 4. Part C — Welcome-overlay version caption (mobile analog of the title bar)

Windows shows the version in the **title bar**, which mobile does not have. The closest analog is the **Welcome overlay**, where the app name/branding is shown prominently on launch.

### File: `ui/components/WelcomeOverlay.kt`
Under the app title/tagline, add a small, muted caption:
```kotlin
Text(
    text = "v${BuildConfig.VERSION_NAME}",
    style = MaterialTheme.typography.labelSmall,
    color = Color.White.copy(alpha = 0.6f)
)
```
(Match the overlay's existing text colors.) This is **optional/secondary** — the About section is the primary surface. Do not try to reproduce a desktop title bar.

---

## 5. Part D — Localized strings

Add three strings to **each** of `res/values/strings.xml` (default/en), `res/values-ru`, `res/values-zh`, `res/values-es`, `res/values-hi`. Use the exact same wording as the Windows port:

| key | en | ru | zh | es | hi |
|---|---|---|---|---|---|
| `settings_about` | About | О программе | 关于 | Acerca de | ऐप के बारे में |
| `about_version` | Version | Версия | 版本 | Versión | संस्करण |
| `about_built` | Built | Сборка от | 构建于 | Compilado | निर्मित |

```xml
<string name="settings_about">About</string>
<string name="about_version">Version</string>
<string name="about_built">Built</string>
```
> `app_name` ("Cardio Simulator") already exists in `res/values/strings.xml` — reuse it for the About title.

---

## 6. Verification

1. `./gradlew :app:assembleFullDebug` twice → `version.properties` `build` increases by exactly **1** each time; the installed APK's `versionName` reads `1.0.1.<N>`.
2. `./gradlew tasks` (or an Android Studio Gradle sync) → `build` **does not** change (guard works).
3. Launch → open **Settings**, scroll to **About** → shows app name, `Version 1.0.1.<N>`, `Built <date>`.
4. (If Part C done) first-launch Welcome overlay shows `v1.0.1.<N>`.
5. Confirm all 5 locales render the About labels (switch language in Settings).

---

## 7. Parity notes
- Keep `version.properties` `versionName` in sync with Windows `version.json` `version` and the installer version when bumping semver — there is no automated cross-platform link.
- `AppEdition.IS_LIMITED` already exists on Android (mirror of Windows `AppEdition.IsLimited`); the About section is shown for both editions (it is outside the `if (!AppEdition.IS_LIMITED)` block).
- `BuildConfig.VERSION_NAME`/`VERSION_CODE` are Android's native `BuildInfo`; no separate generated class is needed (unlike Windows' `BuildInfo.g.cs`).
