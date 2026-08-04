param(
    [string]$OutputDir = "",
    [switch]$Run
)

$ErrorActionPreference = "Stop"

# This script lives in tools\; resolve the repo root (its parent) so the artifacts folder and the
# relative project paths below target the repo itself — not tools\ — regardless of where it is run.
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

# The limited (student) edition. Identical to build-release.ps1 except the build configuration is
# "Limited", which defines the LIMITED compile symbol (see CardioSimulator.App.csproj) so
# AppEdition.IsLimited is true: the constructor modes and the data import/export controls are
# absent from this binary. Directory.Build.props gives the Limited configuration Release-quality
# output. The produced publish folder can be packaged by the existing WiX installer as usual
# (it harvests artifacts/publish and is edition-agnostic).
$Configuration = "Limited"
$Platform      = "x64"

function Exec {
    param ([scriptblock]$ScriptBlock)
    & $ScriptBlock
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE" }
}

Write-Host "=== CardioSimulatorWin LIMITED (student) Build ===" -ForegroundColor Cyan

Write-Host "Restoring dependencies..." -ForegroundColor Green
Exec { dotnet restore }

Write-Host "Building app ($Configuration / $Platform)..." -ForegroundColor Green
Exec { dotnet build src\CardioSimulator.App\CardioSimulator.App.csproj `
    --configuration $Configuration --arch $Platform --no-restore -p:SelfContained=true }

# Stop any running instance first: a live app locks native dlls in the publish folder, which makes
# the Remove-Item below fail with "Access denied".
Write-Host "Stopping any running app instances..." -ForegroundColor Green
Get-Process -Name "CardioSimulatorWin" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$outputPath = if ($OutputDir) { $OutputDir } else { Join-Path $RepoRoot "artifacts\publish" }
if (Test-Path $outputPath) { Remove-Item $outputPath -Recurse -Force }

Write-Host "Publishing application..." -ForegroundColor Green
Exec { dotnet publish src\CardioSimulator.App\CardioSimulator.App.csproj `
    --configuration $Configuration --arch $Platform --output $outputPath --no-build `
    -p:PublishReadyToRun=false -p:PublishSingleFile=false -p:SelfContained=true }

# Copy WinUI3 XAML resources (.xbf / .pri) — omitted by dotnet publish, required at runtime
$appBuildDir = Join-Path $RepoRoot "src\CardioSimulator.App\bin\$Configuration\net8.0-windows10.0.19041.0\win-$Platform"
if (-not (Test-Path $appBuildDir)) { throw "App build output not found at: $appBuildDir" }
Write-Host "Copying WinUI3 XAML resources..." -ForegroundColor Green
Get-ChildItem -Path $appBuildDir -Recurse -Filter *.xbf | ForEach-Object {
    $relative = $_.FullName.Substring($appBuildDir.Length).TrimStart('\')
    $dest = Join-Path $outputPath $relative
    New-Item -ItemType Directory -Path (Split-Path $dest -Parent) -Force | Out-Null
    Copy-Item $_.FullName $dest -Force
}
$appPri = Join-Path $appBuildDir "CardioSimulatorWin.pri"
if (Test-Path $appPri) { Copy-Item $appPri $outputPath -Force } else { throw "App PRI not found at: $appPri" }

Write-Host "=== Limited build completed successfully! ===" -ForegroundColor Cyan
Write-Host "Output: $outputPath" -ForegroundColor Cyan

if ($Run) {
    $exePath = Join-Path $outputPath "CardioSimulatorWin.exe"
    if (-not (Test-Path $exePath)) { throw "Executable not found at: $exePath" }
    Write-Host "Launching limited app..." -ForegroundColor Green
    Start-Process -FilePath $exePath -WorkingDirectory $outputPath
}
