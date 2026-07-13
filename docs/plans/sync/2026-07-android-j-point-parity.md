# Plan: Port J-Point Behavior to Android

**Created:** 2026-07-12  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

Add support for a new significant ECG landmark: the **J-point** (junction between the QRS complex and the ST segment) in the Android ECG graph editor. 

In the Windows version, the following changes were successfully implemented:
- Added `J_POINT` to the `EcgPointType` enum, positioned between `S_PEAK` and `QRS_END`.
- Localized the J-point label and description across En, Ru, Zh, Es, and Hi.
- Updated `SignificantPointPanel` to display the "J" point toggle in the QRS group array.
- Updated `EcgRenderer` to draw `J_POINT` on the canvas as `"J"` rather than `"J_POINT"`.

This plan details how to apply these identical changes to the Android Jetpack Compose codebase.

---

## 2. Part A: Domain Model (SignificantPoint.kt)

Add the `J_POINT` enum constant to `EcgPointType` in [SignificantPoint.kt](file:///E:/VLN_Project/CardioSimulator/app/src/main/java/com/example/cardiosimulator/domain/SignificantPoint.kt).

### Target File:
`app/src/main/java/com/example/cardiosimulator/domain/SignificantPoint.kt`

### Modifications:
Add `J_POINT("J", "Точка J")` to `EcgPointType` in the `QRS` block:
```kotlin
enum class EcgPointType(val label: String, val descriptionRu: String) {
    P_START("P<sub>s</sub>", "Начало зубца P"),
    P_PEAK("P", "Пик зубца P"),
    P_END("P<sub>e</sub>", "Конец зубца P"),
    
    QRS_START("QRS<sub>s</sub>", "Начало комплекса QRS"),
    Q_PEAK("Q", "Пик зубца Q"),
    R_PEAK("R", "Пик зубца R"),
    S_PEAK("S", "Пик зубца S"),
    J_POINT("J", "Точка J"),
    QRS_END("QRS<sub>e</sub>", "Конец комплекса QRS"),
    
    T_START("T<sub>s</sub>", "Начало зубца T"),
    T_PEAK("T", "Пик зубца T"),
    T_END("T<sub>e</sub>", "Конец зубца T")
}
```

---

## 3. Part B: UI Panels (SignificantPointPanel & SignificantPointsControlPanel)

Update the UI panels to include `J_POINT` within the QRS group array.

### Target Files:
1. `app/src/main/java/com/example/cardiosimulator/ui/panels/SignificantPointPanel.kt`
2. `app/src/main/java/com/example/cardiosimulator/ui/panels/SignificantPointsControlPanel.kt`

### Modifications:
- In [SignificantPointPanel.kt](file:///E:/VLN_Project/CardioSimulator/app/src/main/java/com/example/cardiosimulator/ui/panels/SignificantPointPanel.kt#L73):
  Modify the `waves` list's QRS section to include `EcgPointType.J_POINT`:
  ```kotlin
  val waves = listOf(
      stringResource(R.string.constructor_p_wave) to listOf(EcgPointType.P_START, EcgPointType.P_PEAK, EcgPointType.P_END),
      stringResource(R.string.constructor_qrs_complex) to listOf(EcgPointType.QRS_START, EcgPointType.Q_PEAK, EcgPointType.R_PEAK, EcgPointType.S_PEAK, EcgPointType.J_POINT, EcgPointType.QRS_END),
      stringResource(R.string.constructor_t_wave) to listOf(EcgPointType.T_START, EcgPointType.T_PEAK, EcgPointType.T_END)
  )
  ```

- In [SignificantPointsControlPanel.kt](file:///E:/VLN_Project/CardioSimulator/app/src/main/java/com/example/cardiosimulator/ui/panels/SignificantPointsControlPanel.kt#L29-L33):
  Add `EcgPointType.J_POINT` to the `pointTypes` list:
  ```kotlin
  val pointTypes = listOf(
      EcgPointType.P_START, EcgPointType.P_PEAK, EcgPointType.P_END,
      EcgPointType.QRS_START, EcgPointType.Q_PEAK, EcgPointType.R_PEAK, EcgPointType.S_PEAK, EcgPointType.J_POINT, EcgPointType.QRS_END,
      EcgPointType.T_START, EcgPointType.T_PEAK, EcgPointType.T_END
  )
  ```

---

## 4. Part C: Chart Rendering (SignificantPointOverlay.kt)

Clean up `J_POINT` to render as `"J"` on the ECG canvas by trimming the suffix.

### Target File:
`app/src/main/java/com/example/cardiosimulator/ui/components/SignificantPointOverlay.kt`

### Modifications:
In [SignificantPointOverlay.kt](file:///E:/VLN_Project/CardioSimulator/app/src/main/java/com/example/cardiosimulator/ui/components/SignificantPointOverlay.kt#L91):
Replace `_POINT` in the point type name alongside `_PEAK` to format the label as `"J"`:
```kotlin
val cleanLabel = pt.type.name.replace("_PEAK", "").replace("_POINT", "")
```

---

## 5. Part D: Localized Resources (strings.xml)

Define string resources for the J-point in all localization files.

### Target Files:
1. `app/src/main/res/values/strings.xml` (English)
2. `app/src/main/res/values-ru/strings.xml` (Russian)
3. `app/src/main/res/values-zh/strings.xml` (Chinese)
4. `app/src/main/res/values-es/strings.xml` (Spanish)
5. `app/src/main/res/values-hi/strings.xml` (Hindi)

### Add the following resource elements to each file:
- **English**: `<string name="ecg_point_j_point">J Point</string>` (insert near `ecg_point_s_peak`)
- **Russian**: `<string name="ecg_point_j_point">J точка</string>`
- **Chinese**: `<string name="ecg_point_j_point">J 点</string>`
- **Spanish**: `<string name="ecg_point_j_point">Punto J</string>`
- **Hindi**: `<string name="ecg_point_j_point">J बिंदु</string>`

---

## 6. Part E: Verification

### 6.1 Manual Verification Flow
1. Open the Android application in an emulator or device.
2. Open a pathology in the ECG editor / Constructor Screen.
3. Open the "Significant Points" side drawer or control panel.
4. Select a sample, and look at the QRS wave group. Verify that the new button **J** is visible.
5. Click **J** to place the J-point. Verify that:
   - A red circle with white center and label **J** appears on the ECG canvas.
   - The J-point is correctly listed in the marked points list.
6. Verify that saving/exporting/reloading a record containing the J-point correctly reads it back.
