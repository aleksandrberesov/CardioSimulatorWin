# Plan: Exclude Clinical Cases from Rhythm List in Android

**Created:** 2026-07-02  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

A customer reported feedback regarding the rhythm selector panel:
> В блоке “Ритмы”, графики ЭКГ с клин историей не должны показыватся. (сейчас они отображаются и там и там).
> (In the "Rhythms" block, ECG charts with clinical history should not be displayed. Currently, they are displayed in both places.)

To align with this, the Windows version was updated to exclude rhythms with clinical cases from the rhythm list when clinical mode is inactive. To maintain parity, the Android version needs to implement the same partition:
1. **Rhythm Mode:** Show only rhythms that *do not* have clinical cases (`clinicalCase` is null or blank).
2. **Clinical Cases Mode:** Show only rhythms that *do* have clinical cases (`clinicalCase` is not null or blank).

---

## 2. Proposed Changes

### 2.1 RhythmViewModel.kt
Modify [RhythmViewModel.kt](file:///E:/VLN_Project/CardioSimulator/app/src/main/java/com/example/cardiosimulator/ui/viewmodels/RhythmViewModel.kt) to filter out clinical cases when `isClinical` is false.

#### [MODIFY] [RhythmViewModel.kt](file:///E:/VLN_Project/CardioSimulator/app/src/main/java/com/example/cardiosimulator/ui/viewmodels/RhythmViewModel.kt)
```kotlin
            if (isClinical) {
                list = list.filter { !it.clinicalCase.isNullOrBlank() }
            } else {
                list = list.filter { it.clinicalCase.isNullOrBlank() }
            }
```

---

### 2.2 RhythmSelector.kt
Modify the display filtering logic in [RhythmSelector.kt](file:///E:/VLN_Project/CardioSimulator/app/src/main/java/com/example/cardiosimulator/ui/panels/RhythmSelector.kt) to ensure that only the relevant items are shown based on the active mode (`isClinicalMode`). This ensures correct presentation across all editor/view screens.

#### [MODIFY] [RhythmSelector.kt](file:///E:/VLN_Project/CardioSimulator/app/src/main/java/com/example/cardiosimulator/ui/panels/RhythmSelector.kt)
```kotlin
    val filtered = remember(rhythms, searchQuery, currentLanguage, isClinicalMode) {
        rhythms
            .filter { entry ->
                if (isClinicalMode) {
                    !entry.clinicalCase.isNullOrBlank()
                } else {
                    entry.clinicalCase.isNullOrBlank()
                }
            }
            .filter { entry ->
                val title = if (isClinicalMode) {
                    entry.getClinicalTitle() ?: (if (currentLanguage == Language.RU) entry.nameRu ?: entry.titleEn else entry.titleEn)
                } else {
                    if (currentLanguage == Language.RU) entry.nameRu ?: entry.titleEn else entry.titleEn
                }
                title.contains(searchQuery, ignoreCase = true)
            }
    }
```

---

## 3. Verification Plan

### 3.1 Manual Verification
1. Run the Android application.
2. Navigate to **Teaching Mode** (Обучение) or **Constructor Mode** (Конструктор).
3. Open the left drawer / rhythm choosing panel.
4. Verify that:
   - When the **Clinical Case Toggle** (stethoscope icon) is **off**:
     - The list contains only standard rhythms (e.g. Sinus Rhythm, Atrial Fibrillation, etc.).
     - No clinical cases with descriptions/histories are visible.
   - When the **Clinical Case Toggle** is **on**:
     - The list contains only rhythms with clinical cases/histories.
