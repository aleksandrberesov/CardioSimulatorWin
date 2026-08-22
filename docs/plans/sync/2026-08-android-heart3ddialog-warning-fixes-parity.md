# Plan: Port Heart3DDialog Nullability & Warning Fixes to Android

**Created:** 2026-08-22  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

During solution build verification on Windows (`CardioSimulatorWin.sln`), nullability and dynamic reflection warnings in `Heart3DDialog.cs` were fixed, and WiX installer ICE60 harvest warnings were suppressed.

This sync plan documents the nullability and collection handling safeguards made in `Heart3DDialog.cs` so corresponding 3D heart rendering components or state handlers on Android maintain clean type-safety and null-safety standards.

---

## 2. Part A: Nullability & Material State Safeguards

- **Reference (Windows):** [Heart3DDialog.cs](file:///e:/VLN_Project/CardioSimulator/Win/src/CardioSimulator.App/Controls/Heart3DDialog.cs)
- **Target (Android):** 3D model / SceneView rendering components in `com.example.cardiosimulator`
- **Key Changes:**
  1. Updated pre-wavefront material backing store (`_preWavefrontMaterials`) to support nullable materials (`MaterialCore?`) to avoid possible null reference assignment warnings.
  2. Applied null-checks and warning suppression around dynamic 3D vertex color updates (`geom.Colors`) to safely handle dynamic reflection / 3D engine collection updates without runtime null pointer exceptions.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Build the Android project (`./gradlew assembleDebug`).
2. Open the 3D Heart dialog/view.
3. Toggle the Wavefront view and verify smooth animation of depolarization wave without null reference or collection manipulation exceptions.
