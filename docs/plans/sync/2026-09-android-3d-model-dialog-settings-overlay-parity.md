# Plan: Move 3D Model Dialog Settings to Dedicated Settings Overlay on Android

**Created:** 2026-09-22  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

Previously, the 3D heart model dialog hosted all configuration elements directly on its left sidebar:
- Heart rate (BPM) slider
- Wave slow-motion dropdown
- Waveform depolarization color scheme dropdown
- Streamline orientation dropdown
- Cutaway position slider
- Infarct progression slider

This resulted in a cluttered, overly tall left sidebar that pushed primary action buttons down and required extensive scrolling.

In the Windows version (`CardioSimulator\Win`), all configuration sliders and dropdowns have been consolidated into a dedicated **Settings Dialog overlay**:
1. Added a **⚙️ Settings icon button** in the top-right corner of the 3D dialog card header (next to the ✕ close button).
2. Clicking the ⚙️ icon opens a clean modal card overlay within the dialog container with semi-transparent backdrop dismissal.
3. The left sidebar now contains only primary toggle buttons:
   - Leads Scheme
   - Description
   - X-ray view
   - Wavefront view
   - Streamlines
   - Cut in half
   - ▶ Develop infarct
4. All parameter controls (BPM slider, wave slow-motion factor, wave color palette, streamline direction, cut plane depth, and infarct progression) now reside neatly inside the Settings Overlay and update the 3D scene in real-time.

This plan details implementing the same Settings Overlay structure and simplifying the left control column on Android.

---

## 2. Part A: Windows Reference Implementation

**Reference File:** `E:\VLN_Project\CardioSimulator\Win\src\CardioSimulator.App\Controls\Heart3DDialog.cs`

1. **Card Header Settings Button:**
   - In `BuildCard()`, created `_settingsButton` with `SymbolIcon(Symbol.Setting)`.
   - Placed beside the close button in a horizontal `headerActions` panel in header column 1.
   - Click handler calls `ToggleSettingsDialog()`.

2. **Settings Dialog Overlay:**
   - Method `BuildSettingsOverlay()` builds a centered card border inside a dimmed backdrop grid (`#66000000`).
   - Header with localized "Settings" / "Настройки" title and ✕ dismiss button.
   - Scrollable body (`ScrollViewer`) containing:
     - **Section 1: Conduction System**: Heart rate slider (40–180 bpm) + label, slow-motion combo, wave colors combo, streamline orientation combo.
     - **Section 2: Cutaway (half heart)**: Cut position slider (0–100%) in `_cutawaySettingsGroup` (hidden when authored cutaway is active).
     - **Section 3: Infarct (necrosis)**: Necrosis progression slider (0–100%) + status label in `_infarctSettingsGroup` (hidden when model lacks infarct textures).
   - Bottom action bar with "Done" / "Готово" button.
   - Dimmed backdrop tap dismisses the dialog.

3. **Left Sidebar Simplification:**
   - `BuildConductionControls()` returns only `{ header, _xrayButton, _wavefrontButton, _streamlineButton }`.
   - `BuildCutawayControls()` returns only `{ header, _cutawayButton }`.
   - `BuildInfarctControls()` returns only `{ header, _infarctPlayButton }`.

4. **Dynamic Visibility Synchronization:**
   - `ResetCutawayState()` toggles `_cutawaySettingsGroup.Visibility` based on `!HasAuthoredCutaway`.
   - `SetupInfarct()` and `LoadInfarctTexturesAsync()` toggle `_infarctSettingsGroup.Visibility` based on texture availability.

---

## 3. Part B: Android Implementation Steps

**Target File:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\ui\dialogs\Heart3DDialog.kt`  
**Related Components:** `Heart3DViewer.kt`, `Heart3DController.kt`

### 1. Dialog Header: Add Settings Icon
In the top-right header row of `Heart3DDialog.kt`, add an `IconButton` before the close icon:
```kotlin
Row(
    verticalAlignment = Alignment.CenterVertically
) {
    IconButton(onClick = { showSettingsDialog = true }) {
        Icon(
            imageVector = Icons.Default.Settings,
            contentDescription = stringResource(R.string.settings),
            tint = MaterialTheme.colorScheme.onSurface
        )
    }
    IconButton(onClick = onDismissRequest) {
        Icon(
            imageVector = Icons.Default.Close,
            contentDescription = stringResource(R.string.close),
            tint = MaterialTheme.colorScheme.onSurface
        )
    }
}
```

### 2. Left Rail Simplification
Remove slider and dropdown composables from the left sidebar composable:
- Remove rate slider, speed dropdown, wave color dropdown, and streamline orientation dropdown.
- Keep only action buttons:
  - Lead Scheme
  - Description
  - X-ray
  - Wavefront
  - Streamlines
  - Cutaway
  - Develop Infarct

### 3. Create Settings Dialog Overlay Composable
Create `Heart3DSettingsDialog` composable (or overlay sheet) containing:
1. **Conduction section**:
   - BPM slider (`Slider` 40f..180f)
   - Slow-motion speed picker (`DropdownMenu` / `SegmentedButton`)
   - Wave color palette selector
   - Streamline orientation selector
2. **Cutaway section** (visible when model supports dynamic cut):
   - Cut position slider (0f..1f)
3. **Infarct section** (visible when model has infarct textures):
   - Infarct progression slider (0f..1f) + stage description text
4. **Bottom action**:
   - "Done" button calling `showSettingsDialog = false`.

### 4. Wire Controller State
Ensure changes to settings inside the overlay update the controller in real time:
- `controller.setHeartRate(bpm)`
- `controller.setSlowMotionSpeed(speedFactor)`
- `controller.setWavefrontScheme(scheme)`
- `controller.setStreamlineOrientation(orientation)`
- `controller.setCutPlaneProgress(progress)`
- `controller.setInfarctProgress(progress)`

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open the 3D Heart Model dialog.
2. Verify the ⚙️ Settings icon button is visible in the top-right corner of the dialog header beside the ✕ close button.
3. Verify the left sidebar only shows clean action buttons and no sliders/combos.
4. Tap ⚙️:
   - Verify the Settings overlay appears smoothly over the dialog.
   - Verify sliders for Heart rate, Cut position, and Infarct progression exist and respond to dragging.
   - Verify comboboxes for Slow motion, Wave colours, and Streamline orientation can be selected.
5. Change BPM to 120 bpm and tap "Done" (or tap outside overlay):
   - Verify 3D pulse / wavefront speed reflects 120 bpm.
6. Open Settings again, adjust Infarct slider to 50%:
   - Verify necrosis texture blend updates on the heart model.
7. Click ✕ or tap the semi-transparent backdrop to dismiss the Settings overlay.
