# Plan: Port Erase Default 3D Hotspots Asset to Android

**Created:** 2026-08-27  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the Windows version (`CardioSimulator\Win`), dummy hotspots ("Title": "12", "Description": "kdklfsdjlksdf", etc.) were present in the default 3D model asset annotations file `heart.hotspots.json`.
These dummy annotations have been erased so that `heart.hotspots.json` contains an empty JSON array (`[]`), leaving the default 3D heart model cleanly annotated without test items.

This plan details updating the corresponding asset file `heart.hotspots.json` in the Android project.

---

## 2. Part A: Asset File Synchronization

- **Target File:** `E:\VLN_Project\CardioSimulator\app\src\main\assets\models\heart.hotspots.json` (or asset models directory on Android)
- **Reference File:** `E:\VLN_Project\CardioSimulator\Win\src\CardioSimulator.App\Assets\Models\heart.hotspots.json`

### Actions:
1. Ensure the asset file `heart.hotspots.json` in Android assets contains an empty JSON array:
   ```json
   [
   ]
   ```
2. Verify that when the 3D Heart dialog/view is opened in the Android app without custom hotspots loaded, no default test markers appear.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Launch CardioSimulator on Android emulator or physical device.
2. Navigate to 3D Heart view / dialog.
3. Verify that zero hotspot markers are displayed on the initial render of the default 3D heart model.
