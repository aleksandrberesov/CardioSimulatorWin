param(
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

$Configuration = "Release"
$Platform      = "x64"

function Exec {
    param ([scriptblock]$ScriptBlock)
    & $ScriptBlock
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE" }
}

Write-Host "=== CardioSimulatorWin Release Build ===" -ForegroundColor Cyan

Write-Host "Restoring dependencies..." -ForegroundColor Green
Exec { dotnet restore }

Write-Host "Building app ($Platform)..." -ForegroundColor Green
Exec { dotnet build src\CardioSimulator.App\CardioSimulator.App.csproj `
    --configuration $Configuration --arch $Platform --no-restore -p:SelfContained=true }

# Stop any running instance first: a live app loads native dlls (e.g. assimp.dll) from the publish
# folder and locks them, which makes the Remove-Item below fail with "Access denied".
Write-Host "Stopping any running app instances..." -ForegroundColor Green
Get-Process -Name "CardioSimulatorWin" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$outputPath = if ($OutputDir) { $OutputDir } else { Join-Path $PSScriptRoot "artifacts\publish" }
if (Test-Path $outputPath) { Remove-Item $outputPath -Recurse -Force }

Write-Host "Publishing application..." -ForegroundColor Green
Exec { dotnet publish src\CardioSimulator.App\CardioSimulator.App.csproj `
    --configuration $Configuration --arch $Platform --output $outputPath --no-build `
    -p:PublishReadyToRun=false -p:PublishSingleFile=false -p:SelfContained=true }

# Copy WinUI3 XAML resources (.xbf / .pri) — omitted by dotnet publish, required at runtime
$appBuildDir = Join-Path $PSScriptRoot "src\CardioSimulator.App\bin\$Configuration\net8.0-windows10.0.19041.0\win-$Platform"
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

Write-Host "=== Release build completed successfully! ===" -ForegroundColor Cyan
Write-Host "Output: $outputPath" -ForegroundColor Cyan

$exePath = Join-Path $outputPath "CardioSimulatorWin.exe"
if (-not (Test-Path $exePath)) { throw "Executable not found at: $exePath" }

Write-Host "Launching app..." -ForegroundColor Green
Start-Process -FilePath $exePath -WorkingDirectory $outputPath
