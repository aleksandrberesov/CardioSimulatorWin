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

---

## 5. Corrections (2026-09-18) — port THESE; they supersede Parts A–B where they differ

Found by driving the Windows build against a fake server that answers with bare `OK` (no ids) after a 2 s delay.
The flow as first written above lets `start` reach the server for a rhythm it does not have:

1. **Start waits for the rhythm, not only for its own ACK.** Before sending `start`, make sure the server holds the
   selected rhythm: if a `query` → `rhythm` send for it is in flight, await it; if it never completed on this
   connection (a Stop or a newer selection cancelled it, or it failed), run `query` → `rhythm` again first. Keep
   "delivered on this connection" as a set of pathology ids — add when a send ends with verdict `OK` or with the
   rhythm written; clear it on every (re)connect and whenever the dataset changes (an edit keeps the id, not the
   samples). Windows: `AppViewModel.EnsureRhythmOnServerAsync` + `MainScreen.StartOnServerAsync`.
   The waiting dialog spans both steps (load, then `start` + ACK). Its Cancel sends nothing if the load was still
   running, and sends `stop` if `start` had already gone out.
2. **Every `start` owns its ACK — including the `stop` → `start` of a switch while playing.** Register the ACK
   waiter before sending. Replies without an id are matched FIFO, so an unowned `OK` is taken as the next
   request's reply (typically the next selection's `query`) and read as "cached": the app then skips uploading a
   rhythm the server does not have and sends `start` for it. Windows: `SendStartAwaitingAckAsync`, shared by both
   paths; the switch path also waits for this ACK before redrawing the new rhythm.
3. **Frames are atomic.** Once a frame's first byte is written, finish it even if its send was cancelled — cancel
   only frames not yet begun. A half-written multi-MB `rhythm` line without its `\n` makes the server read the next
   frame as its tail and lose both.
4. **Every start entry point goes through the same hand-off** (on Windows the Space shortcut used to start the
   local trace before the ACK and bypass the dialog).
5. Part A's `waitForRhythmLoad()` was **removed** on Windows: it tracked whatever was sent last (a rhythm OR a
   start), which is exactly how a start overtook a load. Don't port it — the dialog awaits the task from item 1.

**Add to verification (4.1)** — fake server replying bare `OK`/`no_data` with a 2 s delay:
- two switches while playing, the second before the first's start-ACK arrives → the second rhythm is still
  uploaded (`rhythm` precedes its `start`);
- Stop during a pending switch, then Start → `rhythm` is re-sent before `start`;
- Start while a stopped selection is still loading → on the wire `start` comes after `rhythm`.
