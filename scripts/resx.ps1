<#
.SYNOPSIS
    Development helper: writes Strings.resx / Strings.es-AR.resx from scripts/strings.json.
.DESCRIPTION
    strings.json is the single editable source for UI text: { "Key": { "en": "...", "es-AR": "..." } }.
    The app never runs this script.
#>
[CmdletBinding()]
param(
    [string] $Source = (Join-Path $PSScriptRoot 'strings.json'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\src\CalendarBell.Core\Resources')
)
$ErrorActionPreference = 'Stop'
$strings = Get-Content $Source -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable

function Write-Resx([string]$path, [string]$lang) {
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $w = [System.Xml.XmlWriter]::Create($path, $settings)
    $w.WriteStartDocument()
    $w.WriteStartElement('root')
    foreach ($h in @(@('resmimetype', 'text/microsoft-resx'), @('version', '2.0'),
            @('reader', 'System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'),
            @('writer', 'System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089'))) {
        $w.WriteStartElement('resheader'); $w.WriteAttributeString('name', $h[0]); $w.WriteElementString('value', $h[1]); $w.WriteEndElement()
    }
    foreach ($key in ($strings.Keys | Sort-Object)) {
        $value = $strings[$key][$lang]
        if ($null -eq $value) { continue }
        $w.WriteStartElement('data')
        $w.WriteAttributeString('name', $key)
        $w.WriteAttributeString('xml', 'space', 'http://www.w3.org/XML/1998/namespace', 'preserve')
        $w.WriteElementString('value', $value)
        $w.WriteEndElement()
    }
    $w.WriteEndElement()
    $w.WriteEndDocument()
    $w.Dispose()
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Write-Resx (Join-Path $OutputDirectory 'Strings.resx') 'en'
Write-Resx (Join-Path $OutputDirectory 'Strings.es-AR.resx') 'es-AR'
Write-Host "Wrote $($strings.Count) strings to $OutputDirectory"
