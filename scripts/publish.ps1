<#
.SYNOPSIS
    Produces the release: dist/CalendarBell-win-x64 (self-contained, no .NET install needed), a ZIP of it, and
    dist/CalendarBell-Setup.exe when Inno Setup 6 (ISCC.exe) is available.
.DESCRIPTION
    Development helper; the app never runs it. The published CalendarBell.exe is a GUI-subsystem executable
    (checked below) so starting it never opens a console window.
#>
[CmdletBinding()]
param(
    [string] $Output = (Join-Path $PSScriptRoot '..\dist'),
    [switch] $SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Output = Resolve-Path $Output
$appDir = Join-Path $Output 'CalendarBell-win-x64'
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }

dotnet publish (Join-Path $root 'src\CalendarBell.App\CalendarBell.App.csproj') -c Release -r win-x64 --self-contained true `
    -o $appDir -p:WindowsAppSDKSelfContained=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)" }

# --- sanity checks -------------------------------------------------------------------------------------------
$exe = Join-Path $appDir 'CalendarBell.exe'
if (-not (Test-Path $exe)) { throw 'CalendarBell.exe missing from publish output' }
$bytes = [IO.File]::ReadAllBytes($exe)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
$subsystem = [BitConverter]::ToUInt16($bytes, $peOffset + 4 + 20 + 68)
if ($subsystem -ne 2) { throw "CalendarBell.exe is not a GUI-subsystem executable (subsystem=$subsystem)" }
foreach ($required in 'Microsoft.WindowsAppRuntime.dll', 'Microsoft.Windows.AppNotifications.Projection.dll', 'Resources\Icons\calendarbell.png', 'Resources\Icons\calendarbell.ico') {
    if (-not (Test-Path (Join-Path $appDir $required))) { throw "Missing $required in publish output" }
}
Get-ChildItem $appDir -Filter 'CalendarBell.dev.json' | Remove-Item -Force

$version = (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]
$sizeMb = [math]::Round(((Get-ChildItem $appDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Published CalendarBell $version to $appDir ($sizeMb MB, GUI subsystem OK)"

# --- ZIP (contains the built-in per-user installer: run CalendarBell.exe from the extracted folder) ------------
$zip = Join-Path $Output "CalendarBell-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $appDir '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "ZIP: $zip"

# --- Inno Setup (optional) -----------------------------------------------------------------------------------
if (-not $SkipInstaller) {
    $iscc = @(
        (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue)?.Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($iscc) {
        & $iscc "/DAppVersion=$version" "/DSourceDir=$appDir" "/O$Output" (Join-Path $root 'installer\CalendarBell.iss')
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }
        Write-Host "Installer: $(Join-Path $Output 'CalendarBell-Setup.exe')"
    }
    else {
        Write-Warning 'Inno Setup 6 (ISCC.exe) not found: CalendarBell-Setup.exe was not built. The ZIP installs itself per user (run CalendarBell.exe from the extracted folder).'
    }
}
