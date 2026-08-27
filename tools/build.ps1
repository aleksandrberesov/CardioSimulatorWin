param (
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [switch]$SkipTests,
    [switch]$Publish,
    [switch]$Clean,
    [switch]$Installer,
    # Optional: an encrypted pathology pack to bundle instead of the one in src\Assets. Copied into
    # artifacts\publish\Assets\Pathologies.pak after publish and before the WiX harvest, so the
    # installer ships it. Must be a CSP2 content pack (ideally acronym-tagged).
    [string]$PathologyPak = ""
)

$ErrorActionPreference = "Stop"

# This script lives in tools\; resolve the repo root (its parent) so build outputs, the artifacts
# folder, and the relative project paths below all target the repo itself — not tools\ — regardless
# of the directory the script is launched from.
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

# The shipped file name is defined once in Directory.Build.props (<AppBrandFileName>); read it here so
# the .pri / .msi / setup.exe names below track a rebrand without editing this script. The bootstrapper
# and launcher both emit "$brand-Setup.exe" (see OutputName / AssemblyName = $(AppBrandFileName)-Setup).
$brand = ([regex]::Match((Get-Content -Raw (Join-Path $RepoRoot 'Directory.Build.props')), '<AppBrandFileName>\s*([^<]+?)\s*</AppBrandFileName>')).Groups[1].Value
if (-not $brand) { throw "Could not read <AppBrandFileName> from Directory.Build.props" }

function Exec {
    param ([scriptblock]$ScriptBlock)
    & $ScriptBlock
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE"
    }
}

# Validate the dataset override up front so a bad path fails before any long build.
if ($PathologyPak) {
    if (-not (Test-Path -LiteralPath $PathologyPak -PathType Leaf)) {
        throw "PathologyPak not found: $PathologyPak"
    }
    $PathologyPak = (Resolve-Path -LiteralPath $PathologyPak).Path
    $magicBuf = New-Object byte[] 4
    $fs = [System.IO.File]::OpenRead($PathologyPak)
    try { [void]$fs.Read($magicBuf, 0, 4) } finally { $fs.Dispose() }
    $magic = [System.Text.Encoding]::ASCII.GetString($magicBuf)
    if (-not $magic.StartsWith("CSP")) {
        throw "PathologyPak is not an encrypted content pack (magic '$magic', expected CSP2): $PathologyPak"
    }
    if (-not ($Publish -or $Installer)) {
        Write-Warning "-PathologyPak is only used with -Publish or -Installer; ignoring."
    } else {
        $pakMB = [math]::Round((Get-Item -LiteralPath $PathologyPak).Length / 1MB, 1)
        Write-Host "Dataset to bundle: $PathologyPak ($pakMB MB, $magic)" -ForegroundColor Yellow
    }
}

if ($Clean) {
    Write-Host "Cleaning..." -ForegroundColor Green
    # Clean via dotnet
    Exec { dotnet clean --configuration $Configuration }
    # Deep clean bin/obj folders
    Get-ChildItem -Path $RepoRoot -Include bin,obj -Recurse | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Starting build for $brand ($Configuration|$Platform)..." -ForegroundColor Cyan

# 1. Restore
Write-Host "Restoring dependencies ($Platform)..." -ForegroundColor Green
Exec { dotnet restore --arch $Platform }

# 2. Build
# NOTE: Build self-contained here so the generated runtimeconfig.json matches the
# self-contained publish below. The publish step reuses this output via --no-build;
# if this build is framework-dependent, publish copies the .NET runtime files but keeps
# a framework-dependent runtimeconfig.json -> installed app fails with "No frameworks were found".
Write-Host "Building App ($Platform)..." -ForegroundColor Green
Exec { dotnet build src\CardioSimulator.App\CardioSimulator.App.csproj --configuration $Configuration --arch $Platform --no-restore -p:SelfContained=true }

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
    $outputPath = Join-Path (Join-Path $RepoRoot "artifacts") "publish"
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

    # WinUI3 publish fix: `dotnet publish` for unpackaged WinUI3 apps omits the compiled
    # XAML (.xbf) and the app resource map (.pri). Without them the installed app crashes
    # at startup inside Microsoft.UI.Xaml.dll (0xC000027B). Copy them from the build output.
    $appBuildDir = Join-Path $RepoRoot "src\CardioSimulator.App\bin\$Configuration\net8.0-windows10.0.19041.0\win-$Platform"
    if (-not (Test-Path $appBuildDir)) { throw "App build output not found at: $appBuildDir" }
    Write-Host "Copying WinUI3 XAML resources (.xbf/.pri) into publish output..." -ForegroundColor Green
    Get-ChildItem -Path $appBuildDir -Recurse -Filter *.xbf | ForEach-Object {
        $relative = $_.FullName.Substring($appBuildDir.Length).TrimStart('\')
        $dest = Join-Path $outputPath $relative
        New-Item -ItemType Directory -Path (Split-Path $dest -Parent) -Force | Out-Null
        Copy-Item $_.FullName $dest -Force
    }
    $appPri = Join-Path $appBuildDir "$brand.pri"
    if (Test-Path $appPri) { Copy-Item $appPri $outputPath -Force } else { throw "App PRI not found at: $appPri" }

    # Bundle the chosen dataset over the one published from src\Assets, before the WiX installer
    # harvests this folder. The app loads whatever Assets\Pathologies.pak it finds, so this is a
    # pure file swap.
    if ($PathologyPak) {
        $assetsDir = Join-Path $outputPath "Assets"
        New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
        $destPak = Join-Path $assetsDir "Pathologies.pak"
        Copy-Item -LiteralPath $PathologyPak -Destination $destPak -Force
        $bundledMB = [math]::Round((Get-Item -LiteralPath $destPak).Length / 1MB, 1)
        Write-Host "Bundled dataset: $(Split-Path -Leaf $PathologyPak) -> Assets\Pathologies.pak ($bundledMB MB)" -ForegroundColor Green
    }

    Write-Host "Published to: $outputPath" -ForegroundColor Cyan
}

# 5. Build Installer (Optional)
if ($Installer) {
    Write-Host "Building Universal Multi-language Installer..." -ForegroundColor Green
    
    # 5.1 Build all MSIs (one per culture).
    # Each single-culture build emits bin\Release\$brand.msi (overwriting it each
    # time), so copy the result into the per-culture folder that Bundle.wxs references. We build
    # cultures one at a time here instead of letting the bootstrapper trigger a single
    # multi-culture Installer build, because that multi-culture build fails validating the last
    # culture's MSI (WIX7010 "could not find ...\<culture>\$brand.msi").
    $cultures = @("en-US", "ru-RU", "zh-CN", "es-ES")
    $installerBinDir = Join-Path $RepoRoot "src\CardioSimulator.Installer\bin\$Configuration"
    foreach ($culture in $cultures) {
        Write-Host "Building MSI for $culture..." -ForegroundColor Cyan
        Exec {
            dotnet build src\CardioSimulator.Installer\CardioSimulator.Installer.wixproj `
                --configuration $Configuration `
                --arch $Platform `
                "-p:Cultures=$culture"
        }
        $cultureDir = Join-Path $installerBinDir $culture
        New-Item -ItemType Directory -Path $cultureDir -Force | Out-Null
        Copy-Item (Join-Path $installerBinDir "$brand.msi") (Join-Path $cultureDir "$brand.msi") -Force
    }

    # 5.2 Build the Universal Bootstrapper (Setup.exe).
    # BuildProjectReferences=false stops this from rebuilding the Installer as one multi-culture
    # build (which fails WIX7010); it consumes the per-culture MSIs produced in step 5.1.
    Exec {
        dotnet build src\CardioSimulator.Bootstrapper\CardioSimulator.Bootstrapper.wixproj `
            --configuration $Configuration `
            --arch $Platform `
            "-p:Culture=en-us" `
            "-p:BuildProjectReferences=false"
    }
    
    $wixSetupPath = Join-Path (Join-Path (Join-Path (Join-Path (Join-Path $RepoRoot "src") "CardioSimulator.Bootstrapper") "bin") $Platform) $Configuration
    $wixSetupPath = Join-Path $wixSetupPath "$brand-Setup.exe"
    if (-not (Test-Path $wixSetupPath)) {
         $wixSetupPath = Join-Path (Join-Path (Join-Path (Join-Path (Join-Path $RepoRoot "src") "CardioSimulator.Bootstrapper") "bin") $Configuration) "$brand-Setup.exe"
    }

    if (-not (Test-Path $wixSetupPath)) {
        throw "WiX Setup executable not found at: $wixSetupPath"
    }

    # 5.3 Build the Launcher (Language Picker)
    Write-Host "Building Language Picker Launcher (WinForms)..." -ForegroundColor Green
    
    # Copy the WiX Setup.exe into the Launcher project folder as 'setup.bin'
    $launcherResourcePath = Join-Path (Join-Path (Join-Path $RepoRoot "src") "CardioSimulator.Launcher") "setup.bin"
    Copy-Item $wixSetupPath $launcherResourcePath -Force
    
    # Build Launcher as a single-file, self-contained EXE
    Exec {
        dotnet publish src\CardioSimulator.Launcher\CardioSimulator.Launcher.csproj `
            --configuration $Configuration `
            --runtime win-x64 `
            --output (Join-Path $RepoRoot "artifacts\temp_launcher") `
            -p:PublishSingleFile=true `
            -p:SelfContained=true `
            -p:IncludeNativeLibrariesForSelfExtract=true
    }
    
    $finalSetupPath = Join-Path (Join-Path $RepoRoot "artifacts\temp_launcher") "$brand-Setup.exe"
    if (Test-Path $finalSetupPath) {
        $destPath = Join-Path (Join-Path $RepoRoot "artifacts") "$brand-Setup_AllInOne.exe"
        Copy-Item $finalSetupPath $destPath -Force
        Write-Host "All-in-One Setup (with language picker) created at: $destPath" -ForegroundColor Cyan
        
        # Cleanup temp
        Remove-Item (Join-Path $RepoRoot "artifacts\temp_launcher") -Recurse -Force
    } else {
        Write-Warning "Final Setup executable not found at: $finalSetupPath"
    }
}

Write-Host "Build completed successfully!" -ForegroundColor Cyan
