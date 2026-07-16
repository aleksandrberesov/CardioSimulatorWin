<#
.SYNOPSIS
    Converts an old plaintext Courses ZIP into the encrypted course pack (.pak) that Cardio
    Simulator now reads.

.DESCRIPTION
    The app used to accept a plain Courses.zip. It now only accepts encrypted content packs
    (*.pak), so an existing ZIP has to be converted once. This script does that conversion and
    then verifies the result by opening it exactly the way the app does.

    It also accepts an older (CSP1) course pack and upgrades it to the current format, so whatever
    shape your old courses are in, this brings them up to date.

    Nothing is changed inside the courses themselves: every lecture, image and manifest is copied
    across byte-for-byte. The original file is left untouched.

    Intended to be run by a customer, not a developer. For the friendliest route, use
    "Convert courses to pak.cmd" instead - drag a file onto it, or just double-click it.

.PARAMETER Zip
    The Courses ZIP (or older course pack) to convert. If omitted, the script asks for it.

.PARAMETER Output
    Where to write the .pak. Defaults to the same folder and name as the ZIP.

.PARAMETER Force
    Overwrite the output file if it already exists.

.EXAMPLE
    .\convert-courses-to-pak.ps1 "C:\Users\me\Downloads\Courses.zip"

.EXAMPLE
    .\convert-courses-to-pak.ps1 -Zip D:\Courses.zip -Output D:\MyCourses.pak -Force
#>
param(
    [Parameter(Position = 0)] [string] $Zip,
    [Parameter(Position = 1)] [string] $Output,
    [switch] $Force
)

$ErrorActionPreference = "Stop"

function Say  { param([string]$m) Write-Host $m }
function Good { param([string]$m) Write-Host $m -ForegroundColor Green }
function Warn { param([string]$m) Write-Host $m -ForegroundColor Yellow }

# Every failure the customer can actually cause funnels through here: one plain-English sentence
# saying what is wrong and what to do, never a PowerShell stack trace.
function Fail {
    param([string]$Problem, [string]$Fix)
    Write-Host ""
    Write-Host "  Could not convert the courses." -ForegroundColor Red
    Write-Host ""
    Write-Host "  Problem: $Problem"
    if ($Fix) { Write-Host "  Try:     $Fix" }
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Host "  Cardio Simulator - convert courses to the new format" -ForegroundColor Cyan
Write-Host "  ----------------------------------------------------"
Write-Host ""

# ── 1. Which file? ───────────────────────────────────────────────────────────

if (-not $Zip) {
    Say "  Which courses file do you want to convert?"
    Say "  Drag the ZIP file into this window and press Enter (or type its full path)."
    Write-Host ""
    $Zip = Read-Host "  Courses ZIP"
    Write-Host ""
}

# Dragging a file into a console wraps it in quotes; strip them so the path resolves.
$Zip = $Zip.Trim().Trim('"').Trim("'")
if (-not $Zip) { Fail "No file was given." "Run it again and drag your Courses ZIP onto the window." }

if (-not (Test-Path -LiteralPath $Zip -PathType Leaf)) {
    Fail "There is no file at: $Zip" "Check the path, or drag the file in instead of typing it."
}
$Zip = (Resolve-Path -LiteralPath $Zip).Path

# ── 2. Find the converter (needed to identify packs, so resolve it first) ────
#
# Prefer a ContentPacker.exe sitting next to this script (that is how it is handed to a customer).
# Fall back to the developer build outputs, then to `dotnet run` on a machine with the SDK.

$packerExe = $null
foreach ($candidate in @(
    (Join-Path $PSScriptRoot "ContentPacker.exe"),
    (Join-Path $PSScriptRoot "tools\ContentPacker\bin\Release\net8.0\ContentPacker.exe"),
    (Join-Path $PSScriptRoot "tools\ContentPacker\bin\Debug\net8.0\ContentPacker.exe"))) {
    if (Test-Path -LiteralPath $candidate) { $packerExe = $candidate; break }
}

$packerProject = Join-Path $PSScriptRoot "tools\ContentPacker\ContentPacker.csproj"
$useDotnet = $false
if (-not $packerExe) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet -and (Test-Path -LiteralPath $packerProject)) { $useDotnet = $true }
    else {
        Fail "The converter program (ContentPacker.exe) is not next to this script." `
             "Keep this script in the folder your supplier sent, and run it from there."
    }
}

function Invoke-Packer {
    param([string[]] $PackerArgs)
    # The converter reports problems on stderr. PowerShell 5.1 turns a redirected native command's
    # stderr into error records, which under ErrorActionPreference='Stop' becomes a terminating
    # error - so a failed conversion would blow up with a PowerShell stack trace instead of the
    # plain-English message below. Capture output with stderr merged, but not fatally.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($useDotnet) { & dotnet run --project $packerProject --no-launch-profile -- @PackerArgs 2>&1 }
        else            { & $packerExe @PackerArgs 2>&1 }
    }
    finally { $ErrorActionPreference = $previous }
}

# True only for a pack that really holds courses. A pack of ECG recordings has a manifest.txt too,
# so it can parse as a course manifest and report zero courses - hence the count check, not just
# the exit code. Without this, pointing at the ECG pack is reported as "nothing to convert".
function Test-CoursePack {
    param([string] $Path)
    $out = Invoke-Packer @("inspect-courses", $Path)
    if ($LASTEXITCODE -ne 0) { return $false }
    $line = $out | Where-Object { $_ -match '^\s*courses:\s+(\d+)' } | Select-Object -First 1
    if (-not $line) { return $false }
    return ([int]$Matches[1]) -gt 0
}

# ── 3. What kind of file is it? ──────────────────────────────────────────────

$head = [byte[]]::new(4)
$fs = [System.IO.File]::OpenRead($Zip)
try { $null = $fs.Read($head, 0, 4) } finally { $fs.Dispose() }
$magic = [System.Text.Encoding]::ASCII.GetString($head)

$mode = ""
$courseCount = 0

if ($magic -eq "CSP2") {
    if (Test-CoursePack $Zip) {
        Good "  That file is already a course pack in the current format."
        Say  "  There is nothing to convert - you can use it as it is."
        Write-Host ""
        exit 0
    }
    Fail "That file is a content pack, but it does not contain courses (it is probably your ECG data)." `
         "Pick your Courses ZIP instead. Your ECG data does not need converting."
}
elseif ($magic -eq "CSP1") {
    # An older course pack: still readable by the app, but upgrading it gives the new format's
    # much lower memory use. Worth doing, so convert rather than turning them away.
    if (-not (Test-CoursePack $Zip)) {
        Fail "That file is an older content pack, but it does not contain courses (it is probably your ECG data)." `
             "Pick your Courses ZIP instead."
    }
    Good "  Found an older course pack - it will be upgraded to the current format."
    $mode = "repack"
}
elseif ($head[0] -eq 0x50 -and $head[1] -eq 0x4B) {   # "PK"
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $names = @()
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($Zip)
        try { $names = $archive.Entries | ForEach-Object { $_.FullName } } finally { $archive.Dispose() }
    }
    catch {
        Fail "That ZIP file could not be opened - it may be damaged." "Try re-downloading or re-exporting it."
    }

    if ($names.Count -eq 0) { Fail "That ZIP is empty." "Pick the ZIP that contains your courses." }

    # Courses are identified by a manifest plus per-course course.txt files. Checking now means a
    # wrong file is caught here, in plain language, rather than as an empty course list in the app.
    $hasManifest = $names -contains "manifest.txt"
    $courseFiles = @($names | Where-Object { $_ -like "*/course.txt" })
    $datFiles    = @($names | Where-Object { $_ -like "*.dat" })

    if ($datFiles.Count -gt 0 -and $courseFiles.Count -eq 0) {
        Fail "That ZIP contains ECG recordings, not courses." `
             "This script converts courses. Your ECG data is handled separately."
    }
    if (-not $hasManifest -or $courseFiles.Count -eq 0) {
        Fail "That ZIP does not look like a courses archive (no manifest.txt / course.txt inside)." `
             "Pick the original Courses ZIP."
    }
    $courseCount = $courseFiles.Count
    $mode = "pack"
}
else {
    Fail "That file is not a ZIP archive." "Pick the Courses ZIP that came with your course data."
}

$srcSizeMb = [math]::Round((Get-Item -LiteralPath $Zip).Length / 1MB, 2)
if ($courseCount -gt 0) {
    Good "  Found $courseCount course(s) in $(Split-Path $Zip -Leaf) ($srcSizeMb MB)."
}

# ── 4. Where to? ─────────────────────────────────────────────────────────────

if (-not $Output) {
    $Output = [System.IO.Path]::ChangeExtension($Zip, ".pak")
}
$Output = $Output.Trim().Trim('"').Trim("'")

# Upgrading an old .pak defaults the output onto the input itself. That is the sensible thing to do,
# but it must be said out loud - "a file already exists, replace it?" about their own source file
# would be baffling.
$inPlace = ($Output -eq $Zip)

if ($inPlace -and -not $Force) {
    Warn "  This will replace the file with an upgraded copy:"
    Say  "    $Zip"
    $answer = Read-Host "  Go ahead? (y/N)"
    if ($answer -notmatch '^(y|yes)$') {
        Say "  Cancelled - nothing was changed."
        Write-Host ""
        exit 0
    }
}
elseif ((Test-Path -LiteralPath $Output) -and -not $Force) {
    Warn "  A file already exists at: $Output"
    $answer = Read-Host "  Replace it? (y/N)"
    if ($answer -notmatch '^(y|yes)$') {
        Say "  Cancelled - nothing was changed."
        Write-Host ""
        exit 0
    }
}

# ── 5. Convert ───────────────────────────────────────────────────────────────

Write-Host ""
Say "  Converting... (large course sets can take a minute)"
Write-Host ""

$tmp = "$Output.partial"
if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }

# Convert to a temporary name, and only put it in place once it verifies. A half-written .pak must
# never be left sitting at the destination looking like a usable file.
$packerOutput = Invoke-Packer @($mode, $Zip, $tmp)
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tmp)) {
    $packerOutput | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    Fail "The converter reported an error (shown above)." "Send that message to your supplier."
}

# ── 6. Verify it the way the app will read it ────────────────────────────────

$verify = Invoke-Packer @("inspect-courses", $tmp)
if ($LASTEXITCODE -ne 0) {
    $verify | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    Remove-Item -LiteralPath $tmp -Force
    Fail "The converted file could not be read back." "Send the message above to your supplier."
}

$courseLine = $verify | Where-Object { $_ -match '^courses:' } | Select-Object -First 1

if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Force }
Move-Item -LiteralPath $tmp -Destination $Output

$outSizeMb = [math]::Round((Get-Item -LiteralPath $Output).Length / 1MB, 2)

Write-Host ""
Good "  Done."
Write-Host ""
Say   "  Created: $Output  ($outSizeMb MB)"
if ($courseLine) { Say "  Checked: $courseLine" }
if ($inPlace) { Say "  The file was upgraded in place." }
else          { Say "  Your original file was not changed." }
Write-Host ""
Say   "  To use it:"
Say   "    1. Open Cardio Simulator."
Say   "    2. Go to Settings."
Say   "    3. Under Course Data choose 'Change course pack'."
Say   "    4. Pick the file above."
Write-Host ""
