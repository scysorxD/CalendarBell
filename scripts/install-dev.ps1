<#
.SYNOPSIS
    Installs the published build for the current user without UI (same code path as the in-app installer).
    Development helper: run scripts/publish.ps1 first.
#>
[CmdletBinding()]
param([string] $AppDirectory = (Join-Path $PSScriptRoot '..\dist\CalendarBell-win-x64'))

$ErrorActionPreference = 'Stop'
$exe = Join-Path (Resolve-Path $AppDirectory) 'CalendarBell.exe'
$process = Start-Process -FilePath $exe -ArgumentList '--install', '--quiet' -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Install failed ($($process.ExitCode))" }
Write-Host "Installed to $env:LOCALAPPDATA\Programs\CalendarBell and started in the notification area."
