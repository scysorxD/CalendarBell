<#
.SYNOPSIS
    Builds the whole solution (development helper; the app never needs it to run).
#>
[CmdletBinding()]
param([ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
dotnet build (Join-Path $root 'CalendarBell.sln') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
