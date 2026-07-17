param(
    [string]$OutputRoot = "G:\My Drive\CardioSim\windowsVersion",
    [string]$FullOutputDir = "",
    [string]$LightOutputDir = "",
    [ValidateSet("All", "Full", "Light")]
    [string]$Edition = "All"
)

$ErrorActionPreference = "Stop"

# Production build: both shipping editions in one pass.
#   Full  -> "Release" configuration, everything enabled.
#   Light -> "Limited" configuration, which defines the LIMITED compile symbol (see
#            CardioSimulator.App.csproj) so AppEdition.IsLimited is true: the constructor modes and
#            the data import/export controls are absent from that binary. Directory.Build.props
#            gives the Limited configuration Release-quality output.
# The two editions build into separate bin\<Configuration> trees, so they never collide. Either
# publish folder can be packaged by the existing WiX installer as usual (it harvests a publish
# folder and is edition-agnostic).
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
        [string]$OutputPath
    )

    Write-Host ""
    Write-Host "=== CardioSimulatorWin $Name edition ($Configuration / $Platform) ===" -ForegroundColor Cyan

    Write-Host "Building app..." -ForegroundColor Green
    Exec { dotnet build src\CardioSimulator.App\CardioSimulator.App.csproj `
        --configuration $Configuration --arch $Platform --no-restore -p:SelfContained=true }

    if (Test-Path $OutputPath) { Remove-Item $OutputPath -Recurse -Force }

    Write-Host "Publishing application..." -ForegroundColor Green
    Exec { dotnet publish src\CardioSimulator.App\CardioSimulator.App.csproj `
        --configuration $Configuration --arch $Platform --output $OutputPath --no-build `
        -p:PublishReadyToRun=false -p:PublishSingleFile=false -p:SelfContained=true }

    # Copy WinUI3 XAML resources (.xbf / .pri) — omitted by dotnet publish, required at runtime
    $appBuildDir = Join-Path $PSScriptRoot "src\CardioSimulator.App\bin\$Configuration\net8.0-windows10.0.19041.0\win-$Platform"
    if (-not (Test-Path $appBuildDir)) { throw "App build output not found at: $appBuildDir" }
    Write-Host "Copying WinUI3 XAML resources..." -ForegroundColor Green
    Get-ChildItem -Path $appBuildDir -Recurse -Filter *.xbf | ForEach-Object {
        $relative = $_.FullName.Substring($appBuildDir.Length).TrimStart('\')
        $dest = Join-Path $OutputPath $relative
        New-Item -ItemType Directory -Path (Split-Path $dest -Parent) -Force | Out-Null
        Copy-Item $_.FullName $dest -Force
    }
    $appPri = Join-Path $appBuildDir "CardioSimulatorWin.pri"
    if (Test-Path $appPri) { Copy-Item $appPri $OutputPath -Force } else { throw "App PRI not found at: $appPri" }

    Write-Host "$Name edition published to: $OutputPath" -ForegroundColor Cyan
}

$fullPath  = if ($FullOutputDir)  { $FullOutputDir }  else { Join-Path $OutputRoot "Full" }
$lightPath = if ($LightOutputDir) { $LightOutputDir } else { Join-Path $OutputRoot "Light" }

Write-Host "=== CardioSimulatorWin Production Build ($Edition) ===" -ForegroundColor Cyan

# Stop any running instance first: a live app locks native dlls in a publish folder, which makes the
# Remove-Item in Build-Edition fail with "Access denied".
Write-Host "Stopping any running app instances..." -ForegroundColor Green
Get-Process -Name "CardioSimulatorWin" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Restoring dependencies..." -ForegroundColor Green
Exec { dotnet restore }

if ($Edition -eq "All" -or $Edition -eq "Full")  { Build-Edition -Name "Full"  -Configuration "Release" -OutputPath $fullPath }
if ($Edition -eq "All" -or $Edition -eq "Light") { Build-Edition -Name "Light" -Configuration "Limited" -OutputPath $lightPath }

Write-Host ""
Write-Host "=== Production build completed successfully! ===" -ForegroundColor Cyan
if ($Edition -eq "All" -or $Edition -eq "Full")  { Write-Host "Full:  $fullPath"  -ForegroundColor Cyan }
if ($Edition -eq "All" -or $Edition -eq "Light") { Write-Host "Light: $lightPath" -ForegroundColor Cyan }
