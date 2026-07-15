# Build Instructions

This project uses a PowerShell script for building, testing, and publishing the application.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10/11 (for WinUI 3 support)

## Usage

Run the `build.ps1` script from the project root using PowerShell.

### Basic Build

Builds the application and runs tests in `Release` configuration for `x64`.

```powershell
.\build.ps1
```

### Build with Specific Configuration and Platform

```powershell
.\build.ps1 -Configuration Debug -Platform x86
```

### Skip Tests

```powershell
.\build.ps1 -SkipTests
```

### Clean and Build

```powershell
.\build.ps1 -Clean
```

### Publish Application

Publishes the application to the `artifacts/publish` folder as a self-contained app.

```powershell
.\build.ps1 -Publish
```

## Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `-Configuration` | `Release` | Build configuration (e.g., `Debug`, `Release`). |
| `-Platform` | `x64` | Target platform (e.g., `x86`, `x64`, `arm64`). |
| `-SkipTests` | `false` | If set, skips running the test suite. |
| `-Publish` | `false` | If set, publishes the application after a successful build. |
| `-Clean` | `false` | If set, cleans the solution and removes `bin`/`obj` folders before building. |

## Editions: Full vs. Limited (student)

The app ships in two editions, selected by build configuration:

- **Full** (`Debug` / `Release`) — the complete app, including the authoring/constructor
  screens (ECG, Course, OSCE, Test constructors) and the data import/export controls in Settings.
- **Limited** (`Limited`) — the locked-down build handed to end users (students). The four
  constructor operating modes are hidden from the mode picker and keyboard shortcuts, and the
  ECG-data / course-data import & export sections are removed from the Settings dialog.

The edition is a compile-time switch: the `Limited` configuration defines the `LIMITED` symbol,
which `AppEdition.IsLimited` reads. Because it is a compile-time constant, the full-edition entry
points are genuinely absent from the limited binary — there is no runtime toggle to flip.

### Build the limited edition

```powershell
.\build-limited.ps1          # publish the student build to artifacts\publish
.\build-limited.ps1 -Run     # ...and launch it afterwards
```

The output in `artifacts\publish` is packaged by the existing WiX installer exactly like the full
build (the installer harvests that folder and is edition-agnostic), so a limited installer is just
`build-limited.ps1` followed by the usual installer build.

To build the limited edition manually or in Visual Studio, select the `Limited` solution
configuration (equivalent to `dotnet build -c Limited`).
