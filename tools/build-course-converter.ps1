<#
.SYNOPSIS
    Builds the self-contained course-converter folder to send to a customer.

.DESCRIPTION
    Stages everything a customer needs to turn their old plaintext Courses ZIP into the encrypted
    course pack (*.pak) the app now reads:

        Convert courses to pak.cmd   - what they double-click or drag a ZIP onto
        convert-courses-to-pak.ps1   - the script behind it
        ContentPacker.exe            - the converter, published SELF-CONTAINED
        README.txt                   - plain-language instructions

    ContentPacker is published self-contained on purpose: a customer will not have the .NET runtime,
    and "the app just says .NET is missing" is the most likely way this fails in the field. The
    trade-off is size (~70 MB) - irrelevant next to shipping something that does not run.

    The staged folder is smoke-tested before you are told it is ready: the published exe is run, and
    if a sample Courses ZIP is available the whole bundle performs a real conversion.

.PARAMETER OutputDir
    Where to stage the folder. Defaults to artifacts\course-converter.

.PARAMETER Runtime
    Target platform for the customer's machine. Defaults to win-x64.

.PARAMETER Zip
    Also produce a .zip of the folder, ready to send.

.PARAMETER SampleZip
    A courses ZIP used to end-to-end test the staged bundle. Auto-detected if not given.

.EXAMPLE
    .\build-course-converter.ps1

.EXAMPLE
    .\build-course-converter.ps1 -Zip -OutputDir D:\to-send
#>
param(
    [string] $OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\course-converter"),
    [ValidateSet("win-x64", "win-x86", "win-arm64")] [string] $Runtime = "win-x64",
    [switch] $Zip,
    [string] $SampleZip
)

$ErrorActionPreference = "Stop"

# This script lives in tools\; the repo root is its parent. The artifacts folder, src\, and
# tools\ContentPacker are resolved against $RepoRoot, while the customer-facing scripts staged into
# the bundle ($scriptPs1 / $scriptCmd below) sit next to this script and stay on $PSScriptRoot.
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Step { param([string]$m) Write-Host "  $m" -ForegroundColor Cyan }
function Good { param([string]$m) Write-Host "  $m" -ForegroundColor Green }
function Warn { param([string]$m) Write-Host "  $m" -ForegroundColor Yellow }

Write-Host ""
Write-Host "=== Building the customer course-converter bundle ===" -ForegroundColor Cyan
Write-Host ""

# ── Preconditions ────────────────────────────────────────────────────────────

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK is required to build this bundle. Install it, then re-run."
}

$packerProject = Join-Path $RepoRoot "tools\ContentPacker\ContentPacker.csproj"
$scriptPs1     = Join-Path $PSScriptRoot "convert-courses-to-pak.ps1"
$scriptCmd     = Join-Path $PSScriptRoot "Convert courses to pak.cmd"

foreach ($required in @($packerProject, $scriptPs1, $scriptCmd)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing required file: $required" }
}

# ── 1. Publish ContentPacker self-contained ──────────────────────────────────

$publishDir = Join-Path $RepoRoot "artifacts\_course-converter-publish"
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }

Step "Publishing ContentPacker ($Runtime, self-contained, single file)..."
# Single file + self-extracting natives so the customer sees exactly one .exe and needs no runtime.
# NOT trimmed: Core pulls in AngleSharp, and trimming reflection-heavy code is how you ship a tool
# that only fails on the customer's machine.
& dotnet publish $packerProject `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -o $publishDir `
    --nologo | Out-Null

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$publishedExe = Join-Path $publishDir "ContentPacker.exe"
if (-not (Test-Path -LiteralPath $publishedExe)) { throw "Publish produced no ContentPacker.exe in $publishDir" }

# ── 2. Stage the folder ──────────────────────────────────────────────────────

Step "Staging $OutputDir ..."
if (Test-Path -LiteralPath $OutputDir) { Remove-Item -LiteralPath $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# Only these three files are copied, so nothing else can end up in the customer's folder — no
# cleanup pass is needed, and DebugType=None means the publish emits no .pdb to begin with.
Copy-Item -LiteralPath $publishedExe -Destination $OutputDir
Copy-Item -LiteralPath $scriptPs1    -Destination $OutputDir
Copy-Item -LiteralPath $scriptCmd    -Destination $OutputDir

$readme = @'
Cardio Simulator - converting your courses
==========================================

Newer versions of Cardio Simulator read courses from a single protected file
(ending in .pak) instead of a ZIP. This folder converts your existing courses
ZIP into that file. It only needs to be done once.

WHAT TO DO
----------
  1. Find your existing courses ZIP (often called Courses.zip).
  2. Drag it onto "Convert courses to pak.cmd" in this folder.
     (Or just double-click that file and it will ask you for the ZIP.)
  3. Wait for it to say "Done". It creates a .pak file next to your ZIP.

THEN, IN THE APP
----------------
  1. Open Cardio Simulator.
  2. Go to Settings.
  3. Under "Course Data", choose "Change course pack".
  4. Pick the .pak file the converter created.

GOOD TO KNOW
------------
  * Your original ZIP is not changed or deleted. Nothing is lost.
  * Your courses are not altered - every lecture and picture is copied across
    exactly, and the converter checks the result before finishing.
  * Nothing is sent anywhere. The conversion happens on your computer.
  * If you already have a .pak file, you do not need to do any of this.
  * Keep all the files in this folder together, and run it from this folder.

IF SOMETHING GOES WRONG
-----------------------
  The converter explains what it found and what to try. If it still will not
  work, send that message to your supplier.
'@
Set-Content -Path (Join-Path $OutputDir "README.txt") -Value $readme -Encoding UTF8

# ── 3. Smoke-test the staged bundle ──────────────────────────────────────────

Step "Smoke-testing the staged bundle..."

$stagedExe = Join-Path $OutputDir "ContentPacker.exe"

# The packer prints its usage on stderr and returns 2 when asked for help. Redirecting a native
# command's stderr in PowerShell 5.1 turns each line into an error record, which would be fatal here
# under ErrorActionPreference='Stop' even though the exe worked - so drop the preference for the call.
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try { & $stagedExe --help 2>&1 | Out-Null }
finally { $ErrorActionPreference = $previous }

# Exit 2 means it started and parsed args; anything else means the published exe does not actually
# run, which is the entire reason for publishing self-contained.
if ($LASTEXITCODE -ne 2) { throw "The published ContentPacker.exe did not run (exit $LASTEXITCODE)." }
Good "ContentPacker.exe runs."

if (-not $SampleZip) {
    foreach ($candidate in @(
        (Join-Path $RepoRoot "src\CardioSimulator.App\Assets\Courses.zip"),
        "E:\VLN_Project\Data\Courses.zip")) {
        if (Test-Path -LiteralPath $candidate) { $SampleZip = $candidate; break }
    }
}

if ($SampleZip -and (Test-Path -LiteralPath $SampleZip)) {
    # Prove the bundle end-to-end, exactly as the customer will run it: the staged script, driving
    # the staged exe, on a real courses ZIP, in a throwaway folder.
    $testDir = Join-Path ([System.IO.Path]::GetTempPath()) ("cc-test-" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $testDir -Force | Out-Null
    try {
        Copy-Item -LiteralPath $SampleZip -Destination (Join-Path $testDir "Courses.zip")
        # 6>&1 redirects the information stream: the converter talks via Write-Host, so without this
        # its output cannot be captured (it would print into this build log and $out would be empty).
        $out = & (Join-Path $OutputDir "convert-courses-to-pak.ps1") -Zip (Join-Path $testDir "Courses.zip") -Force 6>&1
        $made = Join-Path $testDir "Courses.pak"
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $made)) {
            $out | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            throw "The staged bundle failed to convert a sample courses ZIP."
        }
        $checked = ($out | Where-Object { "$_" -match 'courses:\s+\d+' } | Select-Object -First 1)
        Good "End-to-end conversion works."
        if ($checked) { Write-Host ("    " + ("$checked" -replace '\s+', ' ').Trim()) -ForegroundColor DarkGray }
    }
    finally {
        Remove-Item -LiteralPath $testDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
else {
    Warn "No sample courses ZIP found - skipped the end-to-end test."
    Warn "Pass -SampleZip <path> to test the bundle against real course data."
}

# ── 4. Optional zip ──────────────────────────────────────────────────────────

$zipPath = $null
if ($Zip) {
    $zipPath = "$OutputDir.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Step "Compressing to $zipPath ..."
    Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $zipPath
}

Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue

# ── Done ─────────────────────────────────────────────────────────────────────

$sizeMb = [math]::Round(((Get-ChildItem -LiteralPath $OutputDir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB), 1)

Write-Host ""
Good "Bundle ready: $OutputDir  ($sizeMb MB)"
Get-ChildItem -LiteralPath $OutputDir | ForEach-Object {
    Write-Host ("    {0,-32} {1,10:N0} bytes" -f $_.Name, $_.Length)
}
if ($zipPath) { Write-Host ""; Good "Sendable archive: $zipPath" }
Write-Host ""
Write-Host "  Send the whole folder (or the .zip) to the customer. It needs no .NET runtime." -ForegroundColor Gray
Write-Host ""
