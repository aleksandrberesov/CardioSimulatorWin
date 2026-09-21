# Plan: Port TCP NoDelay (`TCP_NODELAY`) Socket Option to Android

**Created:** 2026-09-20  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

By default, OS TCP socket stacks enable Nagle's algorithm (`TCP_NODELAY = false`), which aggregates small outbound packets to reduce packet overhead. When paired with delayed ACKs from the server (standard in Linux/Windows stacks with a 40–200 ms ACK delay window), sending back-to-back small control frames (`query`, `time`, `start`, `stop`, `ack`) introduces an artificial 40–200 ms latency before packets leave the network buffer.

In the Windows version (`CardioSimulator.App`):
- Updated `AppViewModel.cs` at socket creation (`new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)`) to set `NoDelay = true`.
- This ensures control frames and rhythm packets are transmitted immediately without waiting for packet coalescing or peer ACK confirmation.

This plan details the matching change required in the Android app.

---

## 2. Part A: Socket Configuration in `AppViewModel.kt`

**Target File:** `Android\app\src\main\java\com\example\cardiosimulator\ui\viewmodels\AppViewModel.kt`

In `connectTcp()`:
- Locate the socket instantiation:
  ```kotlin
  val socket = Socket()
  ```
- Configure `tcpNoDelay = true` on the socket instance before connection:
  ```kotlin
  val socket = Socket().apply {
      tcpNoDelay = true
  }
  ```
  *(or `socket.tcpNoDelay = true` before `socket.connect(...)`)*.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Connect the Android client to a running TCP monitor server (or mock TCP listener).
2. Trigger playback commands (`start`, `stop`) and rhythm selections.
3. Inspect network traffic / packet timestamps (e.g., via Wireshark or server traffic logger) and confirm that command frames leave the Android device immediately without 40–200 ms Nagle coalescing delays.
