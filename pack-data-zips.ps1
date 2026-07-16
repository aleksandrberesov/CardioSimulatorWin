<#
.SYNOPSIS
    A utility script to pack content ZIP files into encrypted PAK files.
.DESCRIPTION
    This script automates the generation of encrypted content packs (*.pak)
    from plaintext content ZIPs using the CardioSimulator ContentPacker tool.
    It can be run independently of building the application.

    DEPRECATED for the pathology dataset: the binary-first pipeline in
    build-pathology-packs.ps1 (binarize the loose master once, then pack each
    size straight from it — no intermediate ZIP) replaces this ZIP -> pak flow
    and handles the full multi-GB dataset without loading it into memory. This
    script remains for one-off small ZIPs such as Courses.zip.
.PARAMETER SourceDir
    The directory containing the source .zip files to pack. Defaults to "E:\VLN_Project\Data".
.PARAMETER OutputDir
    The directory where the resulting .pak files will be saved. Defaults to the SourceDir.
.PARAMETER Clean
    If specified, removes existing .pak files in the OutputDir before packing.
.EXAMPLE
    .\pack-data-zips.ps1
.EXAMPLE
    .\pack-data-zips.ps1 -SourceDir "C:\MyData" -OutputDir "C:\MyData\Packed"
#>

param(
    [string]$SourceDir = "E:\VLN_Project\Data",
    [string]$OutputDir,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

# Default OutputDir to SourceDir if not specified
if (-not $OutputDir) {
    $OutputDir = $SourceDir
}

$RepoRoot = $PSScriptRoot
$PackerProject = Join-Path $RepoRoot "tools\ContentPacker"

# Print banner
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "      CardioSimulator Content Pack Generator      " -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "Source Directory: $SourceDir" -ForegroundColor Gray
Write-Host "Output Directory: $OutputDir" -ForegroundColor Gray
Write-Host ""

# Ensure source directory exists
if (-not (Test-Path $SourceDir)) {
    Write-Error "Source directory not found: $SourceDir"
    exit 1
}

# Ensure output directory exists, create if not
if (-not (Test-Path $OutputDir)) {
    Write-Host "Creating output directory: $OutputDir..." -ForegroundColor Gray
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# 1. Resolve how we run ContentPacker
$PackerExe = $null
$localExe = Join-Path $RepoRoot "ContentPacker.exe"

if (Test-Path $localExe) {
    $PackerExe = $localExe
    Write-Host "Using local executable: $PackerExe" -ForegroundColor Gray
} else {
    $binPaths = @(
        (Join-Path $PackerProject "bin\Release\net8.0\ContentPacker.exe"),
        (Join-Path $PackerProject "bin\Debug\net8.0\ContentPacker.exe"),
        (Join-Path $PackerProject "bin\Release\net8.0\publish\ContentPacker.exe")
    )
    foreach ($path in $binPaths) {
        if (Test-Path $path) {
            $PackerExe = $path
            break
        }
    }
}

if ($PackerExe) {
    Write-Host "Using prebuilt packer executable: $PackerExe" -ForegroundColor Gray
} else {
    # Fallback to dotnet run project
    if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
        Write-Error "Neither a prebuilt ContentPacker.exe was found, nor is the .NET SDK ('dotnet') installed. Please ensure dotnet is in your PATH or place ContentPacker.exe next to this script."
        exit 1
    }
    if (-not (Test-Path $PackerProject)) {
        Write-Error "ContentPacker tool project not found at: $PackerProject. Cannot pack files."
        exit 1
    }
    Write-Host "No prebuilt executable found. Using dotnet project to compile and run: $PackerProject" -ForegroundColor Gray
}
Write-Host ""

# Find all zip files in the source directory
$zipFiles = Get-ChildItem -Path $SourceDir -Filter "*.zip"
if ($zipFiles.Count -eq 0) {
    Write-Host "No .zip files found in $SourceDir." -ForegroundColor Yellow
    exit 0
}

Write-Host "Found $($zipFiles.Count) ZIP file(s) to process." -ForegroundColor Gray
Write-Host ""

# Optional clean step
if ($Clean) {
    Write-Host "Cleaning existing .pak files in output directory..." -ForegroundColor Yellow
    Get-ChildItem -Path $OutputDir -Filter "*.pak" | Remove-Item -Force
}

$successCount = 0
$failCount = 0
$results = @()

foreach ($zipFile in $zipFiles) {
    $zipName = $zipFile.Name
    $pakName = [System.IO.Path]::ChangeExtension($zipName, ".pak")
    $pakPath = Join-Path $OutputDir $pakName
    
    Write-Host "--------------------------------------------------" -ForegroundColor Gray
    Write-Host "Processing: $zipName" -ForegroundColor Cyan
    Write-Host "Packing..." -ForegroundColor Gray
    
    # Run the pack command
    if ($PackerExe) {
        & $PackerExe pack $zipFile.FullName $pakPath
    } else {
        & dotnet run --project $PackerProject --no-launch-profile -- pack $zipFile.FullName $pakPath
    }
    $exitCode = $LASTEXITCODE
    
    if ($exitCode -eq 0) {
        Write-Host "Verifying packed archive..." -ForegroundColor Gray
        if ($PackerExe) {
            & $PackerExe verify $pakPath
        } else {
            & dotnet run --project $PackerProject --no-launch-profile -- verify $pakPath
        }
        $verifyExitCode = $LASTEXITCODE
        
        if ($verifyExitCode -eq 0) {
            Write-Host "Success: packed and verified $pakName" -ForegroundColor Green
            $successCount++
            $results += [PSCustomObject]@{
                FileName = $zipName
                Status   = "Success"
                Size     = "$([Math]::Round($zipFile.Length / 1MB, 2)) MB"
            }
        } else {
            Write-Host "Verification failed for: $pakName" -ForegroundColor Red
            $failCount++
            $results += [PSCustomObject]@{
                FileName = $zipName
                Status   = "Verification Failed"
                Size     = "$([Math]::Round($zipFile.Length / 1MB, 2)) MB"
            }
        }
    } else {
        Write-Host "Packing failed for: $zipName" -ForegroundColor Red
        $failCount++
        $results += [PSCustomObject]@{
            FileName = $zipName
            Status   = "Packing Failed"
            Size     = "$([Math]::Round($zipFile.Length / 1MB, 2)) MB"
        }
    }
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "                 Packing Summary                  " -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
$results | Format-Table -AutoSize
Write-Host "Successfully Packed: $successCount" -ForegroundColor Green
if ($failCount -gt 0) {
    Write-Host "Failed:              $failCount" -ForegroundColor Red
    exit 1
} else {
    Write-Host "All files packed successfully!" -ForegroundColor Green
}
