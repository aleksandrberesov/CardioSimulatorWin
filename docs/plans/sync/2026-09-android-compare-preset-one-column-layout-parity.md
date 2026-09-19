# Plan: Port Compare Mode Preset Layout (One Column Default) to Android

**Created:** 2026-09-19  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

When loading/applying a saved Comparison Mode preset (e.g., containing 4 rhythms), the expected default display scheme is 1 column (`SeriesScheme.OneColumn`), matching the standard layout used when comparison mode is newly created or entered.

In Windows, `ApplyCompareLayout()` in `MainScreen.xaml.cs` previously calculated `count <= 2 ? SeriesScheme.TwoColumn : SeriesScheme.Grid`, which forced presets with 3 or more rhythms into a 2x2 grid view instead of 1 column. This was fixed by setting `_monitorViewModel.SetSeriesScheme(SeriesScheme.OneColumn)` when applying the layout.

On Android, `MonitorViewModel.applyPreset(preset)` currently updates `comparisonTargets` and `isCompareMode = true`, but does not set `seriesScheme = SeriesScheme.OneColumn` or update `count` to fit the preset targets. This plan updates Android `applyPreset` to ensure consistency across platforms.

---

## 2. Part A: Update `MonitorViewModel.kt` on Android

In `app/src/main/java/com/example/cardiosimulator/ui/viewmodels/MonitorViewModel.kt`:
Update `applyPreset(preset: ComparisonPreset)` to set `seriesScheme = SeriesScheme.OneColumn` and update `count` based on the preset targets:

```kotlin
fun applyPreset(preset: ComparisonPreset) {
    val maxPane = if (preset.targets.isEmpty()) 1 else preset.targets.keys.maxOrNull() ?: 1
    val count = (maxPane + 1).coerceIn(2, 12)
    _monitorMode.update { 
        it.copy(
            comparisonTargets = preset.targets,
            isCompareMode = true,
            count = count,
            seriesScheme = SeriesScheme.OneColumn
        )
    }
}
```

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Open Teaching Screen (Обучение) on Android.
2. Tap "Сравнение" (Compare mode button) and select 4 rhythms.
3. Save the comparison preset (e.g., name "test").
4. Exit compare mode or restart app.
5. Tap "Сравнение" and choose the saved preset "test".
6. Verify: The 4 rhythms in the comparison preset display in 1 single column by default (not in a grid).
