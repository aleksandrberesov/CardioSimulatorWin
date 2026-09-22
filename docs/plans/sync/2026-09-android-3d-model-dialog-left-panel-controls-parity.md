# Plan: Consolidate 3D Model Dialog Controls on Left Rail (Android Parity)

**Created:** 2026-09-22  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the Windows version (`CardioSimulator\Win`), the layout of the 3D heart model dialog (`Heart3DDialog`) has been finalized to keep all viewing and configuration controls directly accessible on the scrollable left panel:
- **Conduction System**: Rate slider (40–180 bpm) + live readout label, wave slow-motion dropdown (0.5×..0.01×), X-ray view toggle, Wavefront view toggle, Wave colours dropdown, Streamlines toggle, and Line orientation dropdown.
- **Cutaway (half heart)**: "Cut in half" toggle button and collapsible "Cut position" slider (0–100%) visible only when cutaway is enabled.
- **Infarct (necrosis)**: Healthy myocardium status label, "▶ Develop infarct" animation button, and necrosis progression slider (0–100%).
- **Pure Viewer Mode**: Authoring buttons ("Edit pathway" and "Edit hotspots") remain removed.
- **No separate settings dialog**: The top-right header contains only the ✕ close button; all configuration controls live on the left panel.

This plan details keeping all controls unified on the left sidebar in Android, without a separate settings dialog or authoring tools.

---

## 2. Part A: Windows Reference Implementation

**Reference File:** `E:\VLN_Project\CardioSimulator\Win\src\CardioSimulator.App\Controls\Heart3DDialog.cs`

1. **Card Header**:
   - Header column 1 contains only the ✕ close button. No settings icon button.

2. **Left Control Column (`BuildContent()` / `left` ScrollViewer)**:
   - Contains:
     1. Leads Scheme toggle button (`_leadsSchemeButton`)
     2. MI toggle button
     3. Description floating card toggle (`_descriptionButton`)
     4. Conduction System group (`BuildConductionControls()`)
     5. Cutaway group (`BuildCutawayControls()`)
     6. Infarct group (`BuildInfarctControls()`)

3. **`BuildConductionControls()`**:
   - StackPanel containing `{ header, rateLabel, rateSlider, speedLabel, speedCombo, _xrayButton, _wavefrontButton, _wavefrontSchemeCombo, _streamlineButton, _streamlineOrientationCombo }`.
   - `rateSlider.ValueChanged` updates `_bpm`, `rateLabel.Text`, and redraws the ECG strip (`DrawEcgStrip`).

4. **`BuildCutawayControls()`**:
   - StackPanel containing `{ header, _cutawayButton, _cutSliderHost }`.
   - `_cutSliderHost` is collapsed by default, and set visible when `_cutaway` is true (non-authored cutaway).

5. **`BuildInfarctControls()`**:
   - `_infarctControls` StackPanel containing `{ header, _infarctLabel, _infarctPlayButton, _infarctSlider }`.
   - Visibility is collapsed unless the loaded model provides sidecar infarct textures.

---

## 3. Part B: Android Implementation Steps

**Target File:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\ui\dialogs\Heart3DDialog.kt`  
**Related Components:** `Heart3DViewer.kt`, `Heart3DController.kt`

### 1. Ensure Left Column Contains All Feature Controls
In `Heart3DDialog.kt`, organize the left vertical scroll column into the three functional groups:
1. **Conduction System**:
   - Heart rate `Slider` (40..180) + BPM text.
   - Slow-motion dropdown.
   - "X-ray" and "Wavefront" toggle buttons.
   - Wave colours scheme dropdown.
   - "Streamlines" toggle button and Line orientation dropdown.
2. **Cutaway (half heart)**:
   - "Cut in half" toggle button.
   - Animated visibility for Cut Position `Slider` (0..1f), shown only when cutaway is enabled and model supports dynamic section plane.
3. **Infarct (necrosis)**:
   - Shown only if infarct textures are resolved for the model.
   - Stage description label.
   - "▶ Develop infarct" button.
   - Infarct progression `Slider` (0..1f).

### 2. Dialog Header
Ensure the top-right header contains only the close button (`IconButton` with `Icons.Default.Close`). No settings modal or sheet is required.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open the 3D Heart Model dialog.
2. Verify all controls (rate slider, speed combo, X-ray, wavefront, wave colors, streamlines, line orientation, cut in half, and infarct controls) are visible directly in the left rail.
3. Drag the rate slider: verify the heart rate updates.
4. Click "Cut in half": verify the cut position slider expands underneath the button and adjusts the plane.
5. Verify no separate settings button or dialog exists.
6. Verify no "Edit pathway" or "Edit hotspots" buttons exist.
