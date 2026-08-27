# Plan: Port 3D Hotspot Edit Button Relocation to Left Panel on Android

**Created:** 2026-08-27  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the Windows version (`CardioSimulator\Win`), the hotspot authoring controls (the top-right toolbar containing "Edit Hotspots" and "Clear All" buttons) were removed from the 3D viewport canvas. A single styled function button ("Edit Hotspots" / "Редактировать точки") was added to the left control panel stack alongside the other mode controls.

This plan details updating the 3D Heart view layout on Android to relocate the point editing control to the left side control column and remove the overlay buttons from the 3D view area.

---

## 2. Part A: UI Layout & Navigation

- **Target File:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\ui\Heart3DDialog.kt` (or Compose equivalents)
- **Reference File:** `E:\VLN_Project\CardioSimulator\Win\src\CardioSimulator.App\Controls\Heart3DDialog.cs`

### Actions:
1. Remove the top-right overlay buttons ("Edit Hotspots" / "Clear All") from inside the 3D viewport canvas overlay.
2. Add a styled "Edit Hotspots" / "Редактировать точки" button to the left function button stack (`left` column).
3. Ensure toggling authoring mode updates the button label ("Exit Edit Mode" / "Выйти из ред.") and accent state.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Open the 3D Heart dialog.
2. Confirm that the top-right overlay buttons inside the 3D rendering viewport are removed.
3. Confirm that the left column contains the single "Edit Hotspots" function button.
4. Click "Edit Hotspots" and verify authoring mode toggles correctly.
