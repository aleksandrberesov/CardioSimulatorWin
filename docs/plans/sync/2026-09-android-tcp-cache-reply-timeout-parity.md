# Plan: Port TCP Cache Reply Timeout (1500 ms) to Android

**Created:** 2026-09-20  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

During the TCP cache handshake (`query` message probing whether the server already possesses the selected rhythm data), the client waits for the server's cache verdict (`OK` or `no_data`). Previously, `CacheReplyTimeoutMs` in the Windows client was set to 4000 ms (4.0 seconds). 

On local LAN and classroom Wi-Fi networks, typical network RTT is under 5 ms. If the server drops or delays the verdict response, waiting 4 seconds creates an unnecessary UI delay before the fail-open fallback activates (which sends the rhythm anyway).

In the Windows version (`CardioSimulator.App`):
- Reduced `CacheReplyTimeoutMs` in `AppViewModel.cs` from `4000` to `1500` ms (1.5 seconds).
- Updated protocol documentation (`docs/tcp-protocol.md`) to specify the 1.5-second fail-open timeout.

This plan documents the parity requirement for the Android client.

---

## 2. Part A: Cache Waiter Timeout Configuration in Android

**Target File:** `Android\app\src\main\java\com\example\cardiosimulator\ui\viewmodels\AppViewModel.kt` (and/or `network` package)

- When implementing the cache query handshake (`query` $\to$ `OK`/`no_data`) on Android:
  - Define the timeout constant:
    ```kotlin
    private const val CACHE_REPLY_TIMEOUT_MS = 1500L
    ```
  - When awaiting the cache response via coroutines `withTimeoutOrNull(CACHE_REPLY_TIMEOUT_MS)`:
    - If the timeout expires without a response, fail open (proceed to deliver rhythm data or acknowledge start) without blocking the UI for 4 seconds.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Run a mock TCP server on LAN that listens on port 8080 and deliberately ignores `query` messages (simulating a slow/hanging server).
2. Select a rhythm in the app.
3. Verify that the client waits no longer than 1.5 seconds before timing out and failing open (triggering rhythm delivery).
