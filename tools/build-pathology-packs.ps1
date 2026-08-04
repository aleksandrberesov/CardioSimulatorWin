<#
.SYNOPSIS
    Build the encrypted pathology content packs (*.pak) from the loose master dataset.

.DESCRIPTION
    New binary-first pipeline (replaces the old zip -> pak flow in pack-data-zips.ps1):

      1. BINARIZE the loose master ONCE: every text <id>.dat is compiled to the compact
         CSD1 delta-binary format, one file at a time, into a parallel ".bin" directory.
         Memory use is constant, so the full ~45k-record dataset is handled comfortably.

      2. For every requested size, build an encrypted *.pak DIRECTLY from the binary
         directory — no intermediate ZIP:
           * a subset size (e.g. 500) selects a group-balanced subset via
             tools/dataset-subset/subset_pathologies.py (which reads only manifest.txt),
             then ContentPacker `pack-dir --manifest` packs exactly those records;
           * the special size "All" packs the whole binary directory.

    The plaintext master ZIPs are no longer needed by this pipeline.

.PARAMETER MasterDir
    Loose text master dataset (manifest.txt + groups.txt + <id>.dat). Default:
    E:\VLN_Project\Data\Pathologies.All.regrouped

.PARAMETER BinDir
    Binary master directory to (re)generate. Default: "<MasterDir>.bin".

.PARAMETER OutDir
    Where the *.pak files are written. Default: the parent of MasterDir.

.PARAMETER Sizes
    Subset record targets to build, plus the literal "All" for the full set.
    Default: 500, 5000, 10000, 30000, All

.PARAMETER SkipBinarize
    Reuse an existing BinDir instead of rebuilding it (e.g. to add another size).

.PARAMETER BalanceClinical
    Pass --balance-clinical to the subsetter so the hand-authored non-clinical records
    stay represented in small subsets. Default: on.

.EXAMPLE
    .\build-pathology-packs.ps1

.EXAMPLE
    .\build-pathology-packs.ps1 -Sizes 500,5000 -SkipBinarize
#>

param(
    [string]$MasterDir = "E:\VLN_Project\Data\Pathologies.All.regrouped",
    [string]$BinDir,
    [string]$OutDir,
    [string[]]$Sizes = @("500", "5000", "10000", "30000", "All"),
    [switch]$SkipBinarize,
    [bool]$BalanceClinical = $true
)

$ErrorActionPreference = "Stop"
# This script lives in tools\; the repo root is its parent, so tools\ContentPacker and
# tools\dataset-subset below resolve correctly regardless of where the script is launched from.
$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path -PathType Container $MasterDir)) {
    Write-Error "Master dataset directory not found: $MasterDir"
    exit 1
}
if (-not $BinDir) { $BinDir = "$($MasterDir.TrimEnd('\','/')).bin" }
if (-not $OutDir) { $OutDir = Split-Path -Parent $MasterDir }
$Subsetter = Join-Path $RepoRoot "tools\dataset-subset\subset_pathologies.py"
$PackerProject = Join-Path $RepoRoot "tools\ContentPacker"

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# ── Resolve how to run ContentPacker (prebuilt exe preferred; else `dotnet run`) ──
$PackerExe = $null
foreach ($p in @(
    (Join-Path $RepoRoot "ContentPacker.exe"),
    (Join-Path $PackerProject "bin\Release\net8.0\ContentPacker.exe"),
    (Join-Path $PackerProject "bin\Debug\net8.0\ContentPacker.exe"))) {
    if (Test-Path $p) { $PackerExe = $p; break }
}
function Invoke-Packer {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$PackerArgs)
    if ($PackerExe) { & $PackerExe @PackerArgs }
    else { & dotnet run --project $PackerProject --no-launch-profile -- @PackerArgs }
    if ($LASTEXITCODE -ne 0) { throw "ContentPacker failed: $($PackerArgs -join ' ')" }
}

# ── Resolve python ──
$Python = Get-Command python -ErrorAction SilentlyContinue
if (-not $Python) { $Python = Get-Command py -ErrorAction SilentlyContinue }
if (-not $Python) { Write-Error "python not found on PATH."; exit 1 }

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "      CardioSimulator Pathology Pack Builder      " -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "Master (text) : $MasterDir" -ForegroundColor Gray
Write-Host "Binary master : $BinDir" -ForegroundColor Gray
Write-Host "Output dir    : $OutDir" -ForegroundColor Gray
Write-Host "Sizes         : $($Sizes -join ', ')" -ForegroundColor Gray
Write-Host "Packer        : $(if ($PackerExe) { $PackerExe } else { 'dotnet run (' + $PackerProject + ')' })" -ForegroundColor Gray
Write-Host ""

# ── 1. Binarize the master (once) ──
if ($SkipBinarize) {
    if (-not (Test-Path -PathType Container $BinDir)) {
        Write-Error "-SkipBinarize set but binary master not found: $BinDir"
        exit 1
    }
    Write-Host "Skipping binarize (reusing $BinDir)" -ForegroundColor Yellow
} else {
    Write-Host "--- Binarizing master -> $BinDir ---" -ForegroundColor Cyan
    Invoke-Packer binarize $MasterDir $BinDir
}
Write-Host ""

# ── 2. Build a pak per requested size ──
$results = @()
$manifestTmp = Join-Path $OutDir "_subset.manifest.tmp"
foreach ($size in $Sizes) {
    $isAll = $size -ieq "All"
    $pakName = if ($isAll) { "Pathologies.All.regrouped.pak" }
               else { "Pathologies.regrouped.subset-$size.pak" }
    $pakPath = Join-Path $OutDir $pakName

    Write-Host "--------------------------------------------------" -ForegroundColor Gray
    Write-Host "Building $pakName" -ForegroundColor Cyan
    try {
        if ($isAll) {
            Invoke-Packer pack-dir $BinDir $pakPath
        } else {
            $subArgs = @("--in-dir", $BinDir, "--out-manifest", $manifestTmp, "--target", $size)
            if ($BalanceClinical) { $subArgs += "--balance-clinical" }
            & $Python.Source $Subsetter @subArgs
            if ($LASTEXITCODE -ne 0) { throw "subsetter failed for size $size" }
            Invoke-Packer pack-dir $BinDir $pakPath --manifest $manifestTmp
        }
        $sizeMb = [Math]::Round((Get-Item $pakPath).Length / 1MB, 2)
        Write-Host "OK: $pakName ($sizeMb MB)" -ForegroundColor Green
        $results += [PSCustomObject]@{ Pack = $pakName; Status = "OK"; SizeMB = $sizeMb }
    } catch {
        Write-Host "FAILED: $pakName -- $($_.Exception.Message)" -ForegroundColor Red
        $results += [PSCustomObject]@{ Pack = $pakName; Status = "FAILED"; SizeMB = 0 }
    }
    Write-Host ""
}
if (Test-Path $manifestTmp) { Remove-Item $manifestTmp -Force }

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "                  Build Summary                   " -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { $_.Status -ne "OK" }).Count
if ($failed -gt 0) { Write-Host "$failed pack(s) failed." -ForegroundColor Red; exit 1 }
Write-Host "All packs built successfully." -ForegroundColor Green
