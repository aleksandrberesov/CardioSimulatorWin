# Plan: Port Last-Built App Launcher Tooling & Build Restore Fix to Android / Script Environment

**Created:** 2026-08-23  
**Updated:** 2026-08-26  
**Status:** COMPLETE (Windows Fix Applied) / NOT APPLICABLE (Android uses Gradle / adb)  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\`  

---

## 1. Background & Root Cause Analysis

### Issue Identified
When running built binaries or executing `./tools/build-and-run.ps1`, the app failed at startup with process exit code `-2147450745` (0x80008087, `CoreHostLibMissingFailure / Could not resolve CoreCLR path`).

### Root Cause
In the PowerShell build scripts (`build-and-run.ps1`, `build.ps1`, `build-release.ps1`, `build-limited.ps1`), `dotnet restore` was executed without specifying target architecture flags (`--arch $Platform` / `-r win-$Platform`). 

Because `--arch $Platform` was omitted during restore:
1. `dotnet restore` restored NuGet dependencies for generic framework targets only, skipping the platform-specific `win-x64` runtime pack (`runtimepack.Microsoft.NETCore.App.Runtime.win-x64`).
2. Subsequent `dotnet build --arch x64 --no-restore -p:SelfContained=true` was forced to consume the generic restore manifest (`project.assets.json`), generating an incomplete `deps.json` missing runtime pack entries.
3. At runtime, the .NET Host (`hostpolicy.dll`) inspected `deps.json`, failed to resolve `CoreCLR`, and terminated with error `-2147450745`.

---

## 2. Fixes Applied in Windows Repository

Updated all PowerShell build scripts to pass `--arch $Platform` during `dotnet restore`:
- `tools/build-and-run.ps1`: `dotnet restore --arch $Platform`
- `tools/build.ps1`: `dotnet restore --arch $Platform`
- `tools/build-release.ps1`: `dotnet restore --arch $Platform`
- `tools/build-limited.ps1`: `dotnet restore --arch $Platform`

---

## 3. Verification & Results

1. Executed `./tools/build-and-run.ps1`. Restoration, compilation, publishing, and launch succeeded.
2. Verified process status: `antiAI-ECG-Simulator.exe` launched cleanly (PID verified active, exit code 0).
