# Plan: Port AngleSharp Security Update Dependency Parity to Android

**Created:** 2026-08-07  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

During the build process of `CardioSimulator.Core.csproj`, NuGet raised security warning `NU1902` due to a moderate severity vulnerability ([GHSA-pgww-w46g-26qg](https://github.com/advisories/GHSA-pgww-w46g-26qg)) in `AngleSharp` package version `1.1.2`. `AngleSharp` was updated in Windows to version `1.7.1`.

This plan documents checking third-party dependencies in the Android repository (`CardioSimulator`) to ensure security compliance and dependency health.

---

## 2. Part A: Dependency Health Audit

- Check Gradle dependencies in `E:\VLN_Project\CardioSimulator\app\build.gradle.kts` for any parsing or external networking libraries (e.g., Jsoup, OkHttp).
- Ensure dependencies are kept at secure, patched versions.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Run Gradle build on Android project: `./gradlew assembleDebug`.
2. Ensure build completes with no high/moderate vulnerability advisories.
