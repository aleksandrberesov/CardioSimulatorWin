param(
    # Not allowed to be empty: the per-edition folders below are derived from it with
    # [System.IO.Path]::Combine, which would turn "" into a path relative to the repo root - and every
    # edition folder is wiped before it is published into.
    [ValidateNotNullOrEmpty()]
    [string]$OutputRoot = "G:\My Drive\CardioSim\windowsVersion",
    [string]$FullOutputDir = "",
    [string]$LightOutputDir = "",
    [string]$DemoOutputDir = "",
    [string]$FullDemoOutputDir = "",
    [ValidateSet("All", "Full", "Light", "Demo", "FullDemo")]
    [string]$Edition = "All",
    # Trial length in days for the time-limited editions (see DemoGuard.cs / build-demo.ps1). A demo is an
    # ordinary edition binary with -p:DemoTrialDays baked in, so it stays usable through its build date
    # plus this many days and blocks the day after. Applies to both demo deliverables - Demo is the Light
    # (Limited) one, FullDemo the complete one - and is ignored for the perpetual Full / Light editions.
    # To give the two demos different windows, run the script once per edition (-Edition Demo
    # -DemoDays 30, then -Edition FullDemo -DemoDays 10).
    [ValidateRange(1, 3650)]
    [int]$DemoDays = 10,
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

# Production build: the shipping deliverables in one pass.
#   Full     -> "Release" configuration, everything enabled.
#   Light    -> "Limited" configuration, which defines the LIMITED compile symbol (see
#               CardioSimulator.App.csproj) so AppEdition.IsLimited is true: the constructor modes and
#               the data import/export controls are absent from that binary. Directory.Build.props
#               gives the Limited configuration Release-quality output.
#   Demo     -> the Light (Limited) binary with -p:DemoTrialDays=$DemoDays baked in, so it is time-limited:
#               usable through its build date + $DemoDays days (see DemoGuard.cs / build-demo.ps1).
#   FullDemo -> the same treatment for the complete edition: the Full (Release) binary with
#               -p:DemoTrialDays=$DemoDays baked in, for a prospect who should evaluate everything, but
#               only for a while. Same steps as build-demo.ps1 -Full, with this script's defaults
#               ($DemoDays days rather than that script's 30) and its -PathologyPak dataset override.
# Within a pair only that one property differs, so the four deliverables share two bin\<Configuration>
# trees: Full + FullDemo build into bin\Release, Light + Demo into bin\Limited. That is safe because each
# deliverable is a self-contained step - its build is immediately followed by its own publish (--no-build)
# and resource harvest before the next one starts - so no deliverable can pick up another one's output.
# Do not "optimize" this into all-builds-then-all-publishes.
# Within each pair the time-limited build runs FIRST and the perpetual one LAST, so a COMPLETE run leaves
# a perpetual binary in both trees. Nothing re-stamps an already-built tree, so otherwise a later
# `dotnet publish --no-build`, a hand-zipped bin\Release or tools\run-last-built.ps1 (it launches the
# newest app exe under artifacts\ or bin\) would pick up a trial-stamped binary. A demo-only run (-Edition Demo /
# -Edition FullDemo) has no paired perpetual build and does leave its tree trial-stamped - rebuild before
# reusing it. Visual Studio is unaffected either way: the solution maps this project to x86, so F5 builds
# into bin\x86\<Configuration>, a tree this script never touches.
# The publish folders are packaged by the existing WiX installer, which is edition-agnostic - but it
# harvests artifacts\publish (hardcoded in CardioSimulator.Installer.wixproj), so copy the deliverable
# you want to package in there first rather than pointing the installer at an OutputRoot folder.
$Platform = "x64"

function Exec {
    param ([scriptblock]$ScriptBlock)
    & $ScriptBlock
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE" }
}

# Filled in by Build-Edition (keyed by output folder) with the trial window each demo deliverable
# actually carries, so the summary below reports the stamped dates rather than a recomputed guess.
$script:TrialWindows = @{}

function Format-TrialSuffix {
    param ([string]$OutputPath)
    if ($script:TrialWindows.ContainsKey($OutputPath)) { return " ($($script:TrialWindows[$OutputPath]))" }
    return ""
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
    $buildStartedUtc = (Get-Date).ToUniversalTime()
    Exec { dotnet build src\CardioSimulator.App\CardioSimulator.App.csproj `
        --configuration $Configuration -r win-$Platform --no-restore -p:SelfContained=true -p:DemoTrialDays=$DemoTrialDays }

    if ($DemoTrialDays -gt 0) {
        # Report the window this demo actually carries. DemoGuard counts from BuildInfo.BuildDate - the UTC
        # date Version.targets stamped during the build - and expires on effectiveToday > BuildDate +
        # TrialDays, so the day computed here is the LAST one the app still starts on, and the clock starts
        # at the build rather than at hand-over.
        # The stamped date is read back from the generated obj\BuildInfo.g.cs, which is shared by every
        # build context: a design-time build from an open Visual Studio rewrites it with DemoTrialDays = 0,
        # and so does the publish step below (it re-runs the stamping target without the property, which is
        # why this read has to happen here and not after the publish). A mismatch therefore says the file
        # was overwritten, not that the binary is wrong - report it and fall back to the clock, never abort
        # a multi-minute run over it.
        $stampedDate = $buildStartedUtc.ToString('yyyy-MM-dd', [cultureinfo]::InvariantCulture)
        $buildInfoFile = Join-Path $RepoRoot "src\CardioSimulator.App\obj\BuildInfo.g.cs"
        if (Test-Path $buildInfoFile) {
            $buildInfoText = Get-Content -Raw $buildInfoFile
            $daysMatch = [regex]::Match($buildInfoText, 'DemoTrialDays\s*=\s*(\d+)\s*;')
            $dateMatch = [regex]::Match($buildInfoText, 'BuildDate\s*=\s*"(\d{4}-\d{2}-\d{2})"\s*;')
            if ($daysMatch.Success -and [int]$daysMatch.Groups[1].Value -ne $DemoTrialDays) {
                Write-Host "Warning: $buildInfoFile now reads DemoTrialDays=$($daysMatch.Groups[1].Value), not the $DemoTrialDays just built - another build (Visual Studio?) rewrote it. Check the published binary if that is unexpected." -ForegroundColor Yellow
            } elseif ($dateMatch.Success) {
                $stampedDate = $dateMatch.Groups[1].Value
            }
        }
        $lastDay = [datetime]::ParseExact($stampedDate, 'yyyy-MM-dd', [cultureinfo]::InvariantCulture).AddDays($DemoTrialDays).ToString('yyyy-MM-dd', [cultureinfo]::InvariantCulture)
        $script:TrialWindows[$OutputPath] = "$DemoTrialDays-day trial, built $stampedDate, usable through $lastDay"
        Write-Host "Trial window: $($script:TrialWindows[$OutputPath])" -ForegroundColor Yellow
    }

    if (Test-Path $OutputPath) { Remove-Item $OutputPath -Recurse -Force }

    Write-Host "Publishing application..." -ForegroundColor Green
    Exec { dotnet publish src\CardioSimulator.App\CardioSimulator.App.csproj `
        --configuration $Configuration -r win-$Platform --output $OutputPath --no-build `
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

# [System.IO.Path]::Combine rather than Join-Path: Join-Path validates the drive, so on a machine where
# OutputRoot's drive is not mounted (the default is a personal Drive letter) these defaults would throw
# even for a run that overrides every folder it actually writes.
$fullPath     = if ($FullOutputDir)     { $FullOutputDir }     else { [System.IO.Path]::Combine($OutputRoot, "Full") }
$lightPath    = if ($LightOutputDir)    { $LightOutputDir }    else { [System.IO.Path]::Combine($OutputRoot, "Light") }
$demoPath     = if ($DemoOutputDir)     { $DemoOutputDir }     else { [System.IO.Path]::Combine($OutputRoot, "Demo") }
$fullDemoPath = if ($FullDemoOutputDir) { $FullDemoOutputDir } else { [System.IO.Path]::Combine($OutputRoot, "FullDemo") }

# Build-Edition wipes a deliverable's output folder before publishing into it, so a folder that is the
# same as - or nested inside - another edition's folder means one delivery silently deletes another (and
# the default OutputRoot is a synced Drive folder, so it goes to Drive's trash, not the Recycle Bin).
# Check up front, like the dataset check below, so a bad -*OutputDir fails before any long build rather
# than after one. ALL FOUR folders take part even when -Edition builds just one: the deliveries at risk
# are the ones an earlier run left on disk. The folders this run does not write are advisory only, so one
# that cannot be resolved at all - OutputRoot on a drive that is not mounted here - is skipped instead of
# fatal, while a folder this run does write must resolve.
$editionPaths = [ordered]@{ Full = $fullPath; Light = $lightPath; Demo = $demoPath; FullDemo = $fullDemoPath }
$builtHere = @{}
if ($Edition -eq "All" -or $Edition -eq "Full")     { $builtHere["Full"] = $true }
if ($Edition -eq "All" -or $Edition -eq "Light")    { $builtHere["Light"] = $true }
if ($Edition -eq "All" -or $Edition -eq "Demo")     { $builtHere["Demo"] = $true }
if ($Edition -eq "All" -or $Edition -eq "FullDemo") { $builtHere["FullDemo"] = $true }

function Resolve-OutputPath {
    param ([string]$Path)
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path).TrimEnd('\').ToLowerInvariant()
}

$resolvedPaths = [ordered]@{}
foreach ($entry in $editionPaths.GetEnumerator()) {
    try {
        $resolvedPaths[$entry.Key] = Resolve-OutputPath $entry.Value
    } catch {
        if ($builtHere.ContainsKey($entry.Key)) {
            throw "Cannot resolve the output folder for the $($entry.Key) edition ('$($entry.Value)'): $($_.Exception.Message)"
        }
    }
}
$resolvedRoot = ""
try { $resolvedRoot = Resolve-OutputPath $OutputRoot } catch { }

foreach ($target in $resolvedPaths.GetEnumerator()) {
    if (-not $builtHere.ContainsKey($target.Key)) { continue }
    foreach ($other in $resolvedPaths.GetEnumerator()) {
        if ($other.Key -eq $target.Key) { continue }
        if ($target.Value -eq $other.Value -or $target.Value.StartsWith($other.Value + '\') -or $other.Value.StartsWith($target.Value + '\')) {
            throw "Output folders overlap: the $($target.Key) edition publishes to '$($target.Value)' and the $($other.Key) edition to '$($other.Value)'. Publishing wipes the folder first, so each edition needs its own."
        }
    }
    if ($resolvedRoot -and ($target.Value -eq $resolvedRoot -or $resolvedRoot.StartsWith($target.Value + '\'))) {
        throw "The $($target.Key) edition would publish to '$($target.Value)', which contains the distribution root '$resolvedRoot'. Publishing wipes that folder first, taking the other editions with it."
    }
}

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
Exec { dotnet restore -r win-$Platform }

# Time-limited first, perpetual last within each configuration pair (see the note at the top of this
# file): FullDemo + Full share bin\Release, Demo + Light share bin\Limited.
if ($Edition -eq "All" -or $Edition -eq "FullDemo") { Build-Edition -Name "Full Demo ($DemoDays-day)" -Configuration "Release" -OutputPath $fullDemoPath -PathologyPak $PathologyPak -DemoTrialDays $DemoDays }
if ($Edition -eq "All" -or $Edition -eq "Full")     { Build-Edition -Name "Full"  -Configuration "Release" -OutputPath $fullPath  -PathologyPak $PathologyPak }
if ($Edition -eq "All" -or $Edition -eq "Demo")     { Build-Edition -Name "Demo ($DemoDays-day)" -Configuration "Limited" -OutputPath $demoPath -PathologyPak $PathologyPak -DemoTrialDays $DemoDays }
if ($Edition -eq "All" -or $Edition -eq "Light")    { Build-Edition -Name "Light" -Configuration "Limited" -OutputPath $lightPath -PathologyPak $PathologyPak }

# Each demo line carries the window its own binary was stamped with (see Build-Edition): a trial counts
# from the BUILD date, not from the day the folder is handed over, so a demo shipped a week after the run
# has already spent a week of it. "usable through" is the last day it still starts.
Write-Host ""
Write-Host "=== Production build completed successfully! ===" -ForegroundColor Cyan
if ($Edition -eq "All" -or $Edition -eq "Full")     { Write-Host "Full:      $fullPath"  -ForegroundColor Cyan }
if ($Edition -eq "All" -or $Edition -eq "Light")    { Write-Host "Light:     $lightPath" -ForegroundColor Cyan }
if ($Edition -eq "All" -or $Edition -eq "Demo")     { Write-Host "Demo:      $demoPath$(Format-TrialSuffix $demoPath)" -ForegroundColor Cyan }
if ($Edition -eq "All" -or $Edition -eq "FullDemo") { Write-Host "Full Demo: $fullDemoPath$(Format-TrialSuffix $fullDemoPath)" -ForegroundColor Cyan }
