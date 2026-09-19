# Plan: Port Immediate Start Command (Remove 4s ACK Wait) to Android

**Created:** 2026-09-18  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

Previously, after sending the TCP `start` command to play the selected rhythm, the application registered a cache waiter and awaited a server ACK confirmation (timing out after 4000 ms / 4 seconds). This created an unnecessary 4-second delay before the UI monitor was set to running state.

In the Windows codebase (`CardioSimulator.App/ViewModels/AppViewModel.cs`), the `SendStartAwaitingAckAsync` method was replaced with `SendStartAsync`, which writes the `start` command frame directly over the TCP socket without awaiting a server cache reply ACK. This allows monitoring (`SetIsRunning(true)`) and playback to start immediately upon sending `start`.

This plan details how to port this change to the Android repository (`AppViewModel.kt`).

---

## 2. Part A: AppViewModel Start Command Updates

### 1. Target Kotlin File
`E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\ui\viewmodels\AppViewModel.kt`

### 2. Changes Needed
- In `sendStartCommand` / `sendStartCommandAsync`:
  - Do not register a cache reply waiter for `start` command IDs.
  - Do not call `awaitCacheReply` or block on a 4000 ms timeout for `start` ACK.
  - Send the encoded `start` command frame directly over the socket or output stream (`sendLine(cmd)`).
  - Complete the method immediately upon sending the frame so that callers (`MainScreen` / UI controller) receive immediate completion and start monitoring state (`SetIsRunning(true)`) without delay.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Launch CardioSimulator on Android device or emulator with a connected TCP server.
2. Select a rhythm and press the **Start** button.
3. Verify that the ECG monitor graph starts running immediately upon pressing Start without any 4-second delay or waiting dialog.
4. Verify that the `start` command frame is logged correctly in the TCP traffic log.
