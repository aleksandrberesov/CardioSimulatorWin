$ErrorActionPreference = "Stop"

$Configuration = "Release"
$Platform      = "x64"

function Exec {
    param ([scriptblock]$ScriptBlock)
    & $ScriptBlock
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE" }
}

Write-Host "=== CardioSimulatorWin Release Build ===" -ForegroundColor Cyan

# 1. Restore
Write-Host "Restoring dependencies..." -ForegroundColor Green
Exec { dotnet restore }

# 2. Build (self-contained so publish reuses the output cleanly)
Write-Host "Building app ($Platform)..." -ForegroundColor Green
Exec { dotnet build src\CardioSimulator.App\CardioSimulator.App.csproj `
    --configuration $Configuration --arch $Platform --no-restore -p:SelfContained=true }

# 3. Publish
$outputPath = Join-Path $PSScriptRoot "artifacts\publish"
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

# 4. Installer — one MSI per culture, then bootstrapper, then language-picker launcher
Write-Host "Building installer..." -ForegroundColor Green

$cultures        = @("en-US", "ru-RU", "zh-CN", "es-ES")
$installerBinDir = Join-Path $PSScriptRoot "src\CardioSimulator.Installer\bin\$Configuration"

foreach ($culture in $cultures) {
    Write-Host "  Building MSI for $culture..." -ForegroundColor Cyan
    Exec { dotnet build src\CardioSimulator.Installer\CardioSimulator.Installer.wixproj `
        --configuration $Configuration --arch $Platform "-p:Cultures=$culture" }
    $cultureDir = Join-Path $installerBinDir $culture
    New-Item -ItemType Directory -Path $cultureDir -Force | Out-Null
    Copy-Item (Join-Path $installerBinDir "CardioSimulatorWin.msi") (Join-Path $cultureDir "CardioSimulatorWin.msi") -Force
}

Exec { dotnet build src\CardioSimulator.Bootstrapper\CardioSimulator.Bootstrapper.wixproj `
    --configuration $Configuration --arch $Platform `
    "-p:Culture=en-us" "-p:BuildProjectReferences=false" }

$wixSetupPath = Join-Path $PSScriptRoot "src\CardioSimulator.Bootstrapper\bin\$Platform\$Configuration\CardioSimulatorSetup.exe"
if (-not (Test-Path $wixSetupPath)) {
    $wixSetupPath = Join-Path $PSScriptRoot "src\CardioSimulator.Bootstrapper\bin\$Configuration\CardioSimulatorSetup.exe"
}
if (-not (Test-Path $wixSetupPath)) { throw "WiX Setup executable not found at: $wixSetupPath" }

Write-Host "Building language-picker launcher..." -ForegroundColor Green
$launcherResourcePath = Join-Path $PSScriptRoot "src\CardioSimulator.Launcher\setup.bin"
Copy-Item $wixSetupPath $launcherResourcePath -Force

$tempLauncher = Join-Path $PSScriptRoot "artifacts\temp_launcher"
Exec { dotnet publish src\CardioSimulator.Launcher\CardioSimulator.Launcher.csproj `
    --configuration $Configuration --runtime win-x64 --output $tempLauncher `
    -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true }

$finalSetup = Join-Path $tempLauncher "CardioSimulatorSetup.exe"
if (Test-Path $finalSetup) {
    $dest = Join-Path $PSScriptRoot "artifacts\CardioSimulatorSetup_AllInOne.exe"
    Copy-Item $finalSetup $dest -Force
    Remove-Item $tempLauncher -Recurse -Force
    Write-Host "All-in-One Setup: $dest" -ForegroundColor Cyan
} else {
    Write-Warning "Final Setup executable not found at: $finalSetup"
}

Write-Host "=== Release build completed successfully! ===" -ForegroundColor Cyan
