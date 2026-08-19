param(
    [string]$OutputRoot = "G:\My Drive\CardioSim\windowsVersion",
    [string]$FullOutputDir = "",
    [string]$LightOutputDir = "",
    [string]$DemoOutputDir = "",
    [ValidateSet("All", "Full", "Light", "Demo")]
    [string]$Edition = "All",
    # Trial length in days for the time-limited Demo edition (see DemoGuard.cs / build-demo.ps1). The
    # Demo is the Light (Limited) binary with -p:DemoTrialDays baked in, so it locks this many days
    # after its build date. Ignored for the Full / Light editions.
    [ValidateRange(1, 3650)]
    [int]$DemoDays = 7,
    # Optional: an encrypted pathology pack to bundle instead of the one in src\Assets. Bundled into
    # each edition's Assets\Pathologies.pak after publish, so you can ship a bigger/smaller tagged
    # dataset without editing the source tree. Must be a CSP2 content pack (ideally acronym-tagged).
    [string]$PathologyPak = ""
)

$ErrorActionPreference = "Stop"

# This script lives in tools\; resolve the repo root (its parent) so the WinUI resource harvest and
# the relative project paths below target the repo itself — not tools\ — regardless of where it is
# run. (The distribution OutputRoot below is a separate, intentionally external location.)
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

# The shipped file name is defined once in Directory.Build.props (<AppBrandFileName>); read it here so
# the process / .pri names below track a rebrand without editing this script.
$brand = ([regex]::Match((Get-Content -Raw (Join-Path $RepoRoot 'Directory.Build.props')), '<AppBrandFileName>\s*([^<]+?)\s*</AppBrandFileName>')).Groups[1].Value
if (-not $brand) { throw "Could not read <AppBrandFileName> from Directory.Build.props" }

# Production build: the shipping editions in one pass.
#   Full  -> "Release" configuration, everything enabled.
#   Light -> "Limited" configuration, which defines the LIMITED compile symbol (see
#            CardioSimulator.App.csproj) so AppEdition.IsLimited is true: the constructor modes and
#            the data import/export controls are absent from that binary. Directory.Build.props
#            gives the Limited configuration Release-quality output.
#   Demo  -> the Light (Limited) binary with -p:DemoTrialDays=$DemoDays baked in, so it is time-limited
#            and locks $DemoDays days after its build date (see DemoGuard.cs / build-demo.ps1).
# Full and Light build into separate bin\<Configuration> trees, so they never collide. The Demo reuses
# the Limited tree, but it is built and published in its own self-contained step (build immediately
# followed by publish), so it never overlaps the Light build. Any publish folder can be packaged by the
# existing WiX installer as usual (it harvests a publish folder and is edition-agnostic).
$Platform = "x64"

function Exec {
    param ([scriptblock]$ScriptBlock)
    & $ScriptBlock
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE" }
}

function Build-Edition {
    param (
        [string]$Name,
        [string]$Configuration,
        [string]$OutputPath,
        [string]$PathologyPak = "",
        # 0 = perpetual (Full / Light). A positive value bakes a time-limited demo (see DemoGuard.cs).
        [int]$DemoTrialDays = 0
    )

    Write-Host ""
    Write-Host "=== $brand $Name edition ($Configuration / $Platform) ===" -ForegroundColor Cyan

    Write-Host "Building app..." -ForegroundColor Green
    Exec { dotnet build src\CardioSimulator.App\CardioSimulator.App.csproj `
        --configuration $Configuration --arch $Platform --no-restore -p:SelfContained=true -p:DemoTrialDays=$DemoTrialDays }

    if (Test-Path $OutputPath) { Remove-Item $OutputPath -Recurse -Force }

    Write-Host "Publishing application..." -ForegroundColor Green
    Exec { dotnet publish src\CardioSimulator.App\CardioSimulator.App.csproj `
        --configuration $Configuration --arch $Platform --output $OutputPath --no-build `
        -p:PublishReadyToRun=false -p:PublishSingleFile=false -p:SelfContained=true }

    # Copy WinUI3 XAML resources (.xbf / .pri) — omitted by dotnet publish, required at runtime
    $appBuildDir = Join-Path $RepoRoot "src\CardioSimulator.App\bin\$Configuration\net8.0-windows10.0.19041.0\win-$Platform"
    if (-not (Test-Path $appBuildDir)) { throw "App build output not found at: $appBuildDir" }
    Write-Host "Copying WinUI3 XAML resources..." -ForegroundColor Green
    Get-ChildItem -Path $appBuildDir -Recurse -Filter *.xbf | ForEach-Object {
        $relative = $_.FullName.Substring($appBuildDir.Length).TrimStart('\')
        $dest = Join-Path $OutputPath $relative
        New-Item -ItemType Directory -Path (Split-Path $dest -Parent) -Force | Out-Null
        Copy-Item $_.FullName $dest -Force
    }
    $appPri = Join-Path $appBuildDir "$brand.pri"
    if (Test-Path $appPri) { Copy-Item $appPri $OutputPath -Force } else { throw "App PRI not found at: $appPri" }

    # Bundle the chosen dataset (overriding the pack published from src\Assets). The app loads
    # whatever Assets\Pathologies.pak it finds at runtime, so this is a pure file swap.
    if ($PathologyPak) {
        $assetsDir = Join-Path $OutputPath "Assets"
        New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null
        $destPak = Join-Path $assetsDir "Pathologies.pak"
        Copy-Item -LiteralPath $PathologyPak -Destination $destPak -Force
        $bundledMB = [math]::Round((Get-Item -LiteralPath $destPak).Length / 1MB, 1)
        Write-Host "Bundled dataset into $Name`: $(Split-Path -Leaf $PathologyPak) -> Assets\Pathologies.pak ($bundledMB MB)" -ForegroundColor Green
    }

    Write-Host "$Name edition published to: $OutputPath" -ForegroundColor Cyan
}

$fullPath  = if ($FullOutputDir)  { $FullOutputDir }  else { Join-Path $OutputRoot "Full" }
$lightPath = if ($LightOutputDir) { $LightOutputDir } else { Join-Path $OutputRoot "Light" }
$demoPath  = if ($DemoOutputDir)  { $DemoOutputDir }  else { Join-Path $OutputRoot "Demo" }

# Validate the dataset override up front (before any long build) so a bad path fails fast.
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
    $pakMB = [math]::Round((Get-Item -LiteralPath $PathologyPak).Length / 1MB, 1)
    Write-Host "Dataset to bundle: $PathologyPak ($pakMB MB, $magic)" -ForegroundColor Yellow
}

Write-Host "=== $brand Production Build ($Edition) ===" -ForegroundColor Cyan

# Stop any running instance first: a live app locks native dlls in a publish folder, which makes the
# Remove-Item in Build-Edition fail with "Access denied".
Write-Host "Stopping any running app instances..." -ForegroundColor Green
Get-Process -Name $brand -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Restoring dependencies..." -ForegroundColor Green
Exec { dotnet restore }

if ($Edition -eq "All" -or $Edition -eq "Full")  { Build-Edition -Name "Full"  -Configuration "Release" -OutputPath $fullPath  -PathologyPak $PathologyPak }
if ($Edition -eq "All" -or $Edition -eq "Light") { Build-Edition -Name "Light" -Configuration "Limited" -OutputPath $lightPath -PathologyPak $PathologyPak }
if ($Edition -eq "All" -or $Edition -eq "Demo")  { Build-Edition -Name "Demo ($DemoDays-day)" -Configuration "Limited" -OutputPath $demoPath -PathologyPak $PathologyPak -DemoTrialDays $DemoDays }

Write-Host ""
Write-Host "=== Production build completed successfully! ===" -ForegroundColor Cyan
if ($Edition -eq "All" -or $Edition -eq "Full")  { Write-Host "Full:  $fullPath"  -ForegroundColor Cyan }
if ($Edition -eq "All" -or $Edition -eq "Light") { Write-Host "Light: $lightPath" -ForegroundColor Cyan }
if ($Edition -eq "All" -or $Edition -eq "Demo")  { Write-Host "Demo:  $demoPath ($DemoDays-day trial)" -ForegroundColor Cyan }
