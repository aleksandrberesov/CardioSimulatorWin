# Plan: Port Anti-Screenshot & Screen-Switch Defense to Android

**Created:** 2026-08-19  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

The Windows application implemented anti-cheat security controls for active **Test (Тестирование)**, **Exam (Экзамен)**, and **OSKE (ОСКЭ)** sessions:
1. Prevent screen capture / screenshots using window display affinity (`FLAG_SECURE` on Android).
2. Detect app/screen switching (Activity pause / lifecycle deactivation or focus loss) and screenshot triggers.
3. Instantly terminate active testing sessions upon violation and display a notification informing the user that testing has been immediately finished.

---

## 2. Part A: Android Window Security Flag (`FLAG_SECURE`)

- In Android `MainActivity` or screen container:
  - When a Test, Exam, or OSKE session starts, set window flag:
    `window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)`
  - When the session finishes or terminates, clear window flag:
    `window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE)`

---

## 3. Part B: App Switch & Lifecycle Violation Detection

- Monitor `onPause` / `onStop` / lifecycle state or `onWindowFocusChanged(false)` when an active Test, Exam, or OSKE attempt is running:
  - If focus is lost or the app moves to background while taking a test/exam/OSKE:
    1) Submit/terminate the attempt immediately in the corresponding ViewModel (`TestViewModel`, `ExamViewModel`, `OskeViewModel`).
    2) Clear `FLAG_SECURE`.
    3) Display an AlertDialog / Snackbar informing the student: *"Тестирование сразу закончено из-за попытки переключения между экранами или создания скриншота."*

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. **FLAG_SECURE Test**: Start a test session on Android emulator/device and attempt to take a screenshot via power+volume down or ADB. Verify screenshot is blocked or produces a blank/black image.
2. **App Switch Test**: Start an active test session, then press Home, Recents (app switcher), or switch to another app. Re-open the app and verify the test was immediately finished with the security notification.
