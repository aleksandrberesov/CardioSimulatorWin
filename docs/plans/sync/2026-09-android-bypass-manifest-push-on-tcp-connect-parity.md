# Plan: Port Bypass Manifest Push on TCP Connect Behavior to Android

**Created:** 2026-09-19  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In CardioSimulator Win, upon establishing a TCP connection to the monitor server, `SendManifestAsync` was previously automatically invoked to push `manifest.txt` (the catalog dataset) on every connection.
To reduce connection overhead or adapt to server payload expectations, auto-pushing `manifest.txt` on connection has been temporarily disabled/bypassed, keeping all other TCP connection initialization steps (`SendSystemTimeAsync`, initial rhythm data push, and `ReceiveLoopAsync`) active.

This sync plan details the corresponding adjustment for the Android client TCP connection pipeline.

---

## 2. Part A: TCP Connection Pipeline Adjustment

### Reference (Windows): `src/CardioSimulator.App/ViewModels/AppViewModel.cs`
In `AppViewModel.cs` inside `ConnectLoopAsync` (after TCP connection establishment):
```csharp
await SendSystemTimeAsync(socket, ct);

// await SendManifestAsync(socket, ct); // Bypassed per configuration

if (_tcpSocket == socket && _currentRhythmProvider?.Invoke() is { } selection)
{
    _ = BeginRhythmSend(socket, selection.Pathology, selection.Name, selection.Calibration, restartPlayback: false);
}
```

### Target (Android): `app/src/main/java/com/example/cardiosimulator/...`
In the Android TCP client manager or connection lifecycle handler:
1. Locate the post-connect initialization sequence where time sync (`sendSystemTime`) and manifest upload (`sendManifest`) are called.
2. Comment out or flag-gate the call to `sendManifest` during initial connection establishment.
3. Ensure system time sync, active rhythm cache probe/push, and the response listener loop continue operating as usual.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Connect the Android client to the CardioSimulator TCP monitor server.
2. Monitor TCP traffic logs.
3. Verify that `TimeMessage` is sent first.
4. Verify that `manifest.txt` `UploadMessage` is **not** sent upon connection.
5. Verify that active rhythm selection push and server communication function as expected.
