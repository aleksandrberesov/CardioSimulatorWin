param (
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [switch]$SkipTests,
    [switch]$Publish,
    [switch]$Clean,
    [switch]$Installer
)

$ErrorActionPreference = "Stop"

function Exec {
    param ([scriptblock]$ScriptBlock)
    & $ScriptBlock
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE"
    }
}

if ($Clean) {
    Write-Host "Cleaning..." -ForegroundColor Green
    # Clean via dotnet
    Exec { dotnet clean --configuration $Configuration }
    # Deep clean bin/obj folders
    Get-ChildItem -Path $PSScriptRoot -Include bin,obj -Recurse | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Starting build for CardioSimulatorWin ($Configuration|$Platform)..." -ForegroundColor Cyan

# 1. Restore
Write-Host "Restoring dependencies..." -ForegroundColor Green
Exec { dotnet restore }

# 2. Build
Write-Host "Building App ($Platform)..." -ForegroundColor Green
Exec { dotnet build src\CardioSimulator.App\CardioSimulator.App.csproj --configuration $Configuration --arch $Platform --no-restore }

Write-Host "Building Tests..." -ForegroundColor Green
Exec { dotnet build tests\CardioSimulator.Core.Tests\CardioSimulator.Core.Tests.csproj --configuration $Configuration --no-restore }

# 3. Test
if (-not $SkipTests) {
    Write-Host "Running tests..." -ForegroundColor Green
    Exec { dotnet test tests\CardioSimulator.Core.Tests\CardioSimulator.Core.Tests.csproj --configuration $Configuration --no-build --verbosity normal }
}

# 4. Publish (Optional)
if ($Publish -or $Installer) {
    Write-Host "Publishing application..." -ForegroundColor Green
    $outputPath = Join-Path (Join-Path $PSScriptRoot "artifacts") "publish"
    # Ensure output path is clean for installer harvesting
    if (Test-Path $outputPath) { Remove-Item $outputPath -Recurse -Force }
    
    Exec { 
        dotnet publish src\CardioSimulator.App\CardioSimulator.App.csproj `
            --configuration $Configuration `
            --arch $Platform `
            --output $outputPath `
            --no-build `
            -p:PublishReadyToRun=false `
            -p:PublishSingleFile=false `
            -p:SelfContained=true
    }
    
    Write-Host "Published to: $outputPath" -ForegroundColor Cyan
}

# 5. Build Installer (Optional)
if ($Installer) {
    Write-Host "Building Universal Multi-language Installer..." -ForegroundColor Green
    
    # 5.1 Build all MSIs (one per culture)
    $cultures = @("en-US", "ru-RU", "zh-CN", "es-ES")
    foreach ($culture in $cultures) {
        Write-Host "Building MSI for $culture..." -ForegroundColor Cyan
        Exec { 
            dotnet build src\CardioSimulator.Installer\CardioSimulator.Installer.wixproj `
                --configuration $Configuration `
                --arch $Platform `
                "-p:Cultures=$culture"
        }
    }
    
    # 5.2 Build the Universal Bootstrapper (Setup.exe)
    Exec {
        dotnet build src\CardioSimulator.Bootstrapper\CardioSimulator.Bootstrapper.wixproj `
            --configuration $Configuration `
            --arch $Platform `
            "-p:Culture=en-us"
    }
    
    $wixSetupPath = Join-Path (Join-Path (Join-Path (Join-Path (Join-Path $PSScriptRoot "src") "CardioSimulator.Bootstrapper") "bin") $Platform) $Configuration
    $wixSetupPath = Join-Path $wixSetupPath "CardioSimulatorSetup.exe"
    if (-not (Test-Path $wixSetupPath)) {
         $wixSetupPath = Join-Path (Join-Path (Join-Path (Join-Path (Join-Path $PSScriptRoot "src") "CardioSimulator.Bootstrapper") "bin") $Configuration) "CardioSimulatorSetup.exe"
    }

    if (-not (Test-Path $wixSetupPath)) {
        throw "WiX Setup executable not found at: $wixSetupPath"
    }

    # 5.3 Build the Launcher (Language Picker)
    Write-Host "Building Language Picker Launcher (WinForms)..." -ForegroundColor Green
    
    # Copy the WiX Setup.exe into the Launcher project folder as 'setup.bin'
    $launcherResourcePath = Join-Path (Join-Path (Join-Path $PSScriptRoot "src") "CardioSimulator.Launcher") "setup.bin"
    Copy-Item $wixSetupPath $launcherResourcePath -Force
    
    # Build Launcher as a single-file, self-contained EXE
    Exec {
        dotnet publish src\CardioSimulator.Launcher\CardioSimulator.Launcher.csproj `
            --configuration $Configuration `
            --runtime win-x64 `
            --output (Join-Path $PSScriptRoot "artifacts\temp_launcher") `
            -p:PublishSingleFile=true `
            -p:SelfContained=true `
            -p:IncludeNativeLibrariesForSelfExtract=true
    }
    
    $finalSetupPath = Join-Path (Join-Path $PSScriptRoot "artifacts\temp_launcher") "CardioSimulatorSetup.exe"
    if (Test-Path $finalSetupPath) {
        $destPath = Join-Path (Join-Path $PSScriptRoot "artifacts") "CardioSimulatorSetup_AllInOne.exe"
        Copy-Item $finalSetupPath $destPath -Force
        Write-Host "All-in-One Setup (with language picker) created at: $destPath" -ForegroundColor Cyan
        
        # Cleanup temp
        Remove-Item (Join-Path $PSScriptRoot "artifacts\temp_launcher") -Recurse -Force
    } else {
        Write-Warning "Final Setup executable not found at: $finalSetupPath"
    }
}

Write-Host "Build completed successfully!" -ForegroundColor Cyan
