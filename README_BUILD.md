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
