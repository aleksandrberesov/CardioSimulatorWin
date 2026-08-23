param(
    [string]$Configuration = "",
    [string]$Platform = "",
    [switch]$Publish,
    [switch]$List,
    [int]$Select = 0,
    [string]$Path = "",
    [switch]$NoKill,
    [string]$AppArgs = ""
)

$ErrorActionPreference = "Stop"

# Resolve repo root directory (parent of tools directory)
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

# Read shipped brand file name from Directory.Build.props
$brand = ([regex]::Match((Get-Content -Raw (Join-Path $RepoRoot 'Directory.Build.props')), '<AppBrandFileName>\s*([^<]+?)\s*</AppBrandFileName>')).Groups[1].Value
if (-not $brand) { throw "Could not read <AppBrandFileName> from Directory.Build.props" }

Write-Host "=== $brand App Launcher ===" -ForegroundColor Cyan

$targetExe = ""

if ($Path) {
    if (-not (Test-Path $Path)) {
        throw "Specified executable path does not exist: $Path"
    }
    $targetExe = (Resolve-Path $Path).Path
} else {
    $searchPaths = @(
        (Join-Path $RepoRoot "artifacts"),
        (Join-Path $RepoRoot "src\CardioSimulator.App\bin")
    ) | Where-Object { Test-Path $_ }

    if ($searchPaths.Count -eq 0) {
        throw "No build output directories found under artifacts\ or src\CardioSimulator.App\bin\. Please build the app first (e.g. .\tools\build.ps1)."
    }

    $candidates = Get-ChildItem -Path $searchPaths -Recurse -Filter "*.exe" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '\\obj\\' -and
            $_.Name -notmatch 'Setup|createdump|RestartAgent|testhost|singlefilehost|ContentPacker' -and
            ($_.Name -eq "$brand.exe" -or $_.Name -eq "CardioSimulator.exe")
        }

    if ($Publish) {
        $candidates = $candidates | Where-Object { $_.FullName -like "*\artifacts\publish\*" -or $_.FullName -like "*\artifacts\production\*" }
    }

    if ($Configuration) {
        $candidates = $candidates | Where-Object { $_.FullName -like "*\$Configuration\*" }
    }

    if ($Platform) {
        $candidates = $candidates | Where-Object { $_.FullName -like "*\$Platform\*" -or $_.FullName -like "*\win-$Platform\*" }
    }

    $candidates = $candidates | Sort-Object LastWriteTime -Descending

    if (-not $candidates -or $candidates.Count -eq 0) {
        Write-Host "No built app executables found matching criteria." -ForegroundColor Red
        Write-Host "Run .\tools\build.ps1 or .\tools\build-and-run.ps1 to build the app." -ForegroundColor Yellow
        exit 1
    }

    if ($List) {
        Write-Host "Found built app executables (sorted by last modified time):" -ForegroundColor Green
        $i = 1
        foreach ($c in $candidates) {
            $relPath = $c.FullName.Substring($RepoRoot.Length).TrimStart('\')
            $sizeMB = [math]::Round($c.Length / 1MB, 2)
            $lastMod = $c.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
            Write-Host "  [$i] $lastMod ($sizeMB MB) -> $relPath" -ForegroundColor Yellow
            $i++
        }
        return
    }

    if ($Select -gt 0) {
        if ($Select -gt $candidates.Count) {
            throw "Selected index [$Select] is out of range. Only $($candidates.Count) executables found."
        }
        $targetExe = $candidates[$Select - 1].FullName
    } else {
        $targetExe = $candidates[0].FullName
    }
}

$exeDir = Split-Path $targetExe -Parent
$exeName = Split-Path $targetExe -Leaf
$lastWrite = (Get-Item $targetExe).LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
$relExe = if ($targetExe.StartsWith($RepoRoot)) { $targetExe.Substring($RepoRoot.Length).TrimStart('\') } else { $targetExe }

Write-Host "Last built app found:" -ForegroundColor Green
Write-Host "  Path:     $relExe" -ForegroundColor Yellow
Write-Host "  Built At: $lastWrite" -ForegroundColor Yellow

# Stop any running process instance if requested
if (-not $NoKill) {
    $runningProcesses = Get-Process -Name $brand, "CardioSimulator" -ErrorAction SilentlyContinue
    if ($runningProcesses) {
        Write-Host "Stopping running process instance(s)..." -ForegroundColor Yellow
        $runningProcesses | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
}

# Verify WinUI3 resource files (.pri) in output directory
$priFile = Join-Path $exeDir "$brand.pri"
if (-not (Test-Path $priFile)) {
    $altPri = Join-Path $exeDir "CardioSimulator.pri"
    if (-not (Test-Path $altPri)) {
        Write-Host "Warning: PRI file ($brand.pri) not found in $exeDir. If WinUI resources were not published, app launch may fail." -ForegroundColor Warning
    }
}

Write-Host "Launching application..." -ForegroundColor Cyan
if ($AppArgs) {
    Start-Process -FilePath $targetExe -WorkingDirectory $exeDir -ArgumentList $AppArgs
} else {
    Start-Process -FilePath $targetExe -WorkingDirectory $exeDir
}

Write-Host "App launched successfully!" -ForegroundColor Green
