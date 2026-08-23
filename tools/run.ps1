[CmdletBinding()]
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

$scriptPath = Join-Path $PSScriptRoot "run-last-built.ps1"
& $scriptPath @PSBoundParameters
