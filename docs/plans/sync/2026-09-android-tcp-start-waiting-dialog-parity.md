# Plan: Port TCP Server Start Wait Dialog & Deferred Monitor Trace Behavior to Android

**Created:** 2026-09-17  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

When the CardioSimulator client app is connected to an external TCP monitor server over LAN, selecting a rhythm or pressing **Start** initiates cache probes (`query`), raw sample uploads (`rhythm`), and play commands (`start`).

On Android, sending `start` commands or `rhythm` data payloads when connected to a TCP server should register an ACK waiter and maintain `isRhythmLoadPending = true` while waiting for the server to acknowledge receipt (`OK`, `ack`, or `{"id":"...","status":"ok"}`). Tapping **Start** while ACK is pending displays a progress dialog ("Loading rhythm into monitor server… / Waiting for server confirmation"). Crucially, the local monitor trace must also wait (remain paused/stopped) while the dialog is active, and only start sweeping/rendering waveforms after the server ACK settles.

---

## 2. Part A: ACK Waiter & State Tracking

- In the Android TCP client / ViewModel layer:
  - Add correlation ID handling for `start` commands and `rhythm` payloads.
  - When receiving incoming server lines (`OK`, `ack`, or `{"id":"...","status":"ok"}`), complete matching ACK waiters.
  - In `sendStartCommand()` and `sendRhythmData()`, raise `isRhythmLoadPending = true`, send the command, and await the ACK waiter (with 4s timeout fail-open).
  - Expose `waitForRhythmLoad()` completing when the pending ACK settles.

---

## 3. Part B: Start Button Dialog & Deferred Monitor Start

- In the Android UI (Jetpack Compose / Activity dialog):
  - When the user taps **Start** while connected to the TCP monitor server:
  - Launch `sendStartCommand()`. Do **not** set `monitorViewModel.setRunning(true)` immediately on click.
  - If `isRhythmLoadPending` is `true`, display a modal progress dialog containing a `CircularProgressIndicator` spinner and localized message `"Loading rhythm into monitor server…"`.
  - The monitor surface remains paused/stopped behind the dialog.
  - Await `waitForRhythmLoad()`.
  - Automatically dismiss the dialog when ACK settles or times out.
  - Only after ACK settles or timeout expires, set `monitorViewModel.setRunning(true)` to begin local trace sweeping and rendering.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Connect Android app to external TCP monitor server on LAN.
2. Select a rhythm and tap **Start**.
3. Verify that a modal progress dialog appears with the message `"Loading rhythm into monitor server…"`.
4. Verify that the monitor screen waveform rendering remains paused/waiting while the dialog is visible.
5. Verify that once the server responds with ACK (or after 4s timeout), the dialog automatically closes and local monitor sweep/playback starts immediately.
