<#
.SYNOPSIS
    Runs the automated tests.
.PARAMETER Smoke
    Also runs the Windows smoke tests (tests/CalendarBell.Windows.SmokeTests). They need an interactive Windows 11
    session, a published app in dist/CalendarBell-win-x64 (run scripts/publish.ps1 first) and they show real
    notifications (Windows may keep them in Notification Center when Do not disturb is on).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [switch] $Smoke
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if ($Smoke) { $env:CALENDARBELL_SMOKE = '1' } else { Remove-Item Env:CALENDARBELL_SMOKE -ErrorAction SilentlyContinue }
Push-Location $root
try {
    dotnet test -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}
