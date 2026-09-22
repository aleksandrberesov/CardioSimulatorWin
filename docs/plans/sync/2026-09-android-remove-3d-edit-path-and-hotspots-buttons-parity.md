# Plan: Port Removal of 3D Model Dialog Authoring Buttons ("Edit Pathway" and "Edit Hotspots") to Android

**Created:** 2026-09-21  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

As part of the pure viewer transition for the 3D heart model dialog (`Heart3DDialog`), in-app authoring tools are being phased out in favor of pre-authored model packages and sidecars.

In the Windows version (`CardioSimulator\Win`), the authoring buttons:
1. **"Edit Hotspots"** (`_authoringModeButton` in the left control column)
2. **"Edit pathway"** (`_conductionEditButton` in the conduction controls section)

have been completely removed from the 3D model dialog UI and layout stacks, along with their associated click/toggle handlers (`ToggleAuthoringMode` and `ToggleConductionEdit`).

This plan details removing the corresponding pathway/hotspot authoring controls from the Android 3D heart dialog to maintain visual and functional parity.

---

## 2. Part A: Windows Reference Implementation

**Reference File:** `E:\VLN_Project\CardioSimulator\Win\src\CardioSimulator.App\Controls\Heart3DDialog.cs`

- Removed `_authoringModeButton` declaration, initialization, and addition to `left.Children`.
- Removed `_conductionEditButton` declaration, initialization, and addition to the conduction stack children list in `BuildConductionControls()`.
- Removed unused private methods `ToggleAuthoringMode()` and `ToggleConductionEdit()`.
- Suppressed unused field warnings for residual internal authoring flags (`_authoringMode`, `_conductionEditMode`, `_isAdmin`) pending complete removal of offline authoring routines in the package-loader milestone.

---

## 3. Part B: Android Implementation Steps

**Target File:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\ui\dialogs\Heart3DDialog.kt`  
**Related Components:** `Heart3DViewer.kt`, `Heart3DController.kt`

### Actions:
1. In `Heart3DDialog.kt`, locate the "Edit pathway" button block inside the left control column:
   ```kotlin
   Button(
       onClick = { 
           isEditing = !isEditing
           controller.setEditing(isEditing)
       },
       modifier = Modifier.fillMaxWidth(),
       colors = ButtonDefaults.buttonColors(
           containerColor = if (isEditing) Color.Gray else WindowsBlue
       ),
       shape = RoundedCornerShape(4.dp)
   ) {
       Text(stringResource(if (isEditing) R.string.monitor_3d_done_editing else R.string.monitor_3d_edit_pathway))
   }
   ```
2. Remove this `Button` composable from the UI column.
3. Clean up the now-unused `isEditing` state in `Heart3DDialog.kt` if it is not referenced elsewhere.
4. Ensure no "Edit Hotspots" button exists in the left sidebar or on top of the 3D WebView canvas.
5. Verify the dialog compiles cleanly without warnings or broken references to `R.string.monitor_3d_edit_pathway`.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Launch the Android application and navigate to the ECG monitor screen.
2. Open the 3D Heart model dialog.
3. Verify that the left control rail no longer displays:
   - "Edit pathway" / "Ред. путь"
   - "Edit Hotspots" / "Редактировать точки"
4. Verify that normal viewer controls (Lead scheme, X-ray/transparency, Infarct slider/animation, BPM rate, and play/pause transport) remain visible and function properly.
