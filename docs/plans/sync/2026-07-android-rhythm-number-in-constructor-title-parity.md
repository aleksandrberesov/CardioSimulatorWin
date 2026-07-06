# Plan: Port Rhythm Number in Constructor Title to Android

**Created:** 2026-07-06  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

In the ECG Constructor screen, the rhythm title displayed in the toolbar did not include the rhythm number prefix even if one was assigned (unlike the rhythm choosing drawer/list which displays it). This plan outlines prepending the rhythm number (`number` property) to the rhythm title presented in the constructor screen on Android for parity with Windows.

---

## 2. Part A: Constructor Screen Title Formatting

We will update the `displayTitle` calculation inside `ConstructorScreen.kt` so that if `targetFile.number` is present, it gets prepended to the title string.

### File to change:
- [ConstructorScreen.kt](file:///E:/VLN_Project/CardioSimulator/app/src/main/java/com/example/cardiosimulator/ui/screens/ConstructorScreen.kt)

### Logic Modification:
Around line 601, locate the `displayTitle` definition:
```kotlin
                val displayTitle = targetFile?.let {
                    if (selectedLanguage == com.example.cardiosimulator.domain.Language.RU)
                        it.nameRu ?: it.titleEn
                    else
                        it.titleEn
                } ?: stringResource(R.string.constructor_no_pathology_selected)
```

Replace it with:
```kotlin
                val displayTitle = targetFile?.let { file ->
                    val title = if (selectedLanguage == com.example.cardiosimulator.domain.Language.RU)
                        file.nameRu ?: file.titleEn
                    else
                        file.titleEn
                    file.number?.let { "$it $title" } ?: title
                } ?: stringResource(R.string.constructor_no_pathology_selected)
```

Since `displayTitle` is also passed as the `titleName` argument to `AllLeadsPreviewOverlay` around line 1013:
```kotlin
                            AllLeadsPreviewOverlay(
                                targetFile = targetFile!!,
                                monitorMode = monitorMode,
                                baseline = rhythmViewModel.repository.manifest()?.baseline ?: 1024,
                                titleName = displayTitle,
                                onClose = { showAllLeads = false }
                            )
```
This change will automatically apply to the 12-lead preview overlay title as well.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Open the Android application on an emulator or device.
2. Navigate to the **ECG Constructor** screen.
3. Open a pathology/rhythm that has a number assigned (e.g. from the clinical cases or pathology lists that have numbers).
4. Verify that the toolbar at the top displays the rhythm number as a prefix to the rhythm name (e.g., `5 Sinus Rhythm`).
5. Open the **12-lead preview** overlay (View all leads) and verify that its title also displays the number prefix.
