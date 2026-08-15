# Plan: Port Testing & Examination Mode Choice (Individual vs Group) to Android

**Created:** 2026-08-15  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In CardioSimulatorWin, the customer requested mode selection for **Индивидуальное** (Individual) and **Групповое** (Group) in both Testing mode (`TestingScreen`) and Exam mode (`ExaminationScreen`).
Previously, `TestingScreen` jumped straight to the individual test launcher (`QuickTestScreen`) without offering mode selection or group test server setup. Both `TestingScreen` and `ExaminationScreen` were updated on Windows to present a mode choice start screen ("Выберите режим тестирования") with styled option cards for Individual vs Group mode.

This plan details porting this mode selection parity and classroom LAN group server entry to Android (`TestingScreen.kt` and `ExaminationScreen.kt`).

---

## 2. Part A: TestingScreen Mode Selection & Group Session UI

- **Target file:** `app/src/main/java/com/example/cardiosimulator/ui/screens/TestingScreen.kt`
- **Reference Windows file:** `src/CardioSimulator.App/Screens/TestingScreen.cs`

### Steps:
1. Add `StartArea` composable displaying prompt (`AppStrings.ExamChoosePrompt`) with two option cards:
   - **Individual** (`AppStrings.ExamModeIndividual`): Opens `QuickTestScreen` launcher.
   - **Group** (`AppStrings.ExamModeGroup`): Opens `GroupArea` for group classroom testing.
2. Update `QuickTestScreen` launcher invocation in `TestingScreen.kt` to include a "Back" button handler returning to `StartArea`.
3. Add `GroupArea` composable displaying group session setup (question count dropdown, theme selector, Start button) and live view (QR code image, LAN URL text, live participant roster with scores, Stop button).
4. Connect `GroupTestServer` state (`isSessionRunning`, participant flow) to `TestingScreen.kt`.

---

## 3. Part B: ExaminationScreen Mode Choice Card Styling

- **Target file:** `app/src/main/java/com/example/cardiosimulator/ui/screens/ExaminationScreen.kt`
- **Reference Windows file:** `src/CardioSimulator.App/Screens/ExaminationScreen.cs`

### Steps:
1. Update `BuildStartArea` / `StartArea` in `ExaminationScreen.kt` to use modern card-based option selectors (`CreateModeCard`) with person (`\uE77B` / `Icons.Default.Person`) and group (`\uE716` / `Icons.Default.Group` or `Icons.Default.People`) icons.
2. Ensure visual and layout consistency between `TestingScreen.kt` and `ExaminationScreen.kt`.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open the Android application on device / emulator.
2. Navigate to **Testing** mode (`TestingScreen`):
   - Confirm mode selection prompt appears with **Individual** and **Group** option cards.
   - Tap **Individual**: Verify QuickTestScreen opens with a Back button returning to mode selection.
   - Tap **Group**: Verify group setup and live QR code / roster panel opens.
3. Navigate to **Exam** mode (`ExaminationScreen`):
   - Confirm card-based mode choice UI is displayed consistently.
