param()

$ErrorActionPreference = "Stop"

# This script lives in tools\; resolve the repo root (its parent) so the Assets folder and the
# ContentPacker project below are located relative to the repo, not the tools\ subfolder.
$RepoRoot = Split-Path -Parent $PSScriptRoot

# Regenerates the encrypted content packs (Assets\*.pak) from the plaintext content ZIPs
# (Assets\Pathologies.zip / Courses.zip) using tools/ContentPacker.
#
# The packs are what the app ships and reads in memory (see ContentCrypto / EncryptedArchive); the
# plaintext ZIPs are the source and are intentionally NOT copied into the build output. Run this
# whenever the dataset changes, before producing a distribution build. For a real student
# distribution, first replace Assets\Pathologies.zip / Courses.zip with the full dataset, then run
# this, then build.

$assets = Join-Path $RepoRoot "src\CardioSimulator.App\Assets"
$packer = Join-Path $RepoRoot "tools\ContentPacker"

function Pack-One {
    param([string]$Zip, [string]$Pak)
    $zipPath = Join-Path $assets $Zip
    $pakPath = Join-Path $assets $Pak
    if (-not (Test-Path $zipPath)) {
        Write-Host "skip $Zip (not present in Assets)" -ForegroundColor Yellow
        return
    }
    Write-Host "Packing $Zip -> $Pak ..." -ForegroundColor Green
    dotnet run --project $packer -- pack $zipPath $pakPath
    if ($LASTEXITCODE -ne 0) { throw "packing $Zip failed" }
}

Write-Host "=== Regenerating encrypted content packs ===" -ForegroundColor Cyan
Pack-One -Zip "Pathologies.zip" -Pak "Pathologies.pak"
Pack-One -Zip "Courses.zip" -Pak "Courses.pak"
Write-Host "=== Content packs are up to date ===" -ForegroundColor Cyan
