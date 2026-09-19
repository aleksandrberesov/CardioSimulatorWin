# Plan: Port TCP Auto-Connect on Startup behavior to Android

**Created:** 2026-09-19  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

The user requested an auto-connect feature for the TCP server connection. When enabled via a new checkbox in the Settings dialog, the app automatically initiates a TCP connection to the configured target IP and port upon application launch.

In the Windows app (`CardioSimulator.App`):
1. Extended `DataSourcePrefs` with `KeyTcpAutoConnect` (`"tcp_auto_connect"`).
2. Added `TcpAutoConnect` property, `_tcpAutoConnect` observable property, and `UpdateTcpAutoConnect(bool)` in `AppViewModel`.
3. Added automatic invocation of `ConnectTcp()` in `AppViewModel` constructor if `TcpAutoConnect` is enabled and IP/Port are valid.
4. Added a CheckBox (`_autoConnectCb`) to the TCP section of `SettingsContent` bound to `AppViewModel.TcpAutoConnect`.
5. Added localized string `settings_tcp_auto_connect` ("Auto-connect on startup" / "Автоподключение при запуске" etc.) across all supported languages.

---

## 2. Part A: App Settings & Preference Storage (Android DataStore)

Identify the DataStore preferences repository (`DataSourcePrefs.kt` or equivalent).
- Add preference key `TCP_AUTO_CONNECT = booleanPreferencesKey("tcp_auto_connect")`.
- Expose `tcpAutoConnect: Flow<Boolean>` or property in `DataSourcePrefs`.
- Provide setter `suspend fun setTcpAutoConnect(enabled: Boolean)`.

---

## 3. Part B: ViewModel & Connection Flow (AppViewModel.kt / SettingsDialog.kt)

- In `AppViewModel` initialization / startup coroutine:
  - Read `tcpAutoConnect` preference.
  - If `tcpAutoConnect` is true and valid target `tcpIp` and `tcpPort` are configured, trigger `connectTcp()`.
- In `SettingsDialog` / `SettingsScreen` (Jetpack Compose UI):
  - Add a `Row` containing a `Checkbox` and `Text` for `stringResource(R.string.settings_tcp_auto_connect)`.
  - Bind checkbox `checked` state to `viewModel.tcpAutoConnect`.
  - On checked state change, invoke `viewModel.updateTcpAutoConnect(it)`.
- In `strings.xml` (values, values-ru, etc.):
  - Add `<string name="settings_tcp_auto_connect">Auto-connect on startup</string>` (and localized equivalents).

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open Settings in the app.
2. Under the TCP Connection section, enable "Auto-connect on startup".
3. Close the app and re-launch it.
4. Verify that the TCP client automatically initiates connection to the saved IP:Port on startup without needing to manually click "Connect".
5. Re-open Settings and verify that the checkbox remains checked.
6. Uncheck "Auto-connect on startup", restart the app, and verify that it starts in disconnected state.
