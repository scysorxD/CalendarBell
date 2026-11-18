param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $DataDirectory,
    [switch] $Link
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, PresentationCore, PresentationFramework, System.Windows.Forms
$env:CALENDARBELL_DATA_DIR = $DataDirectory
$process = Start-Process $Exe -ArgumentList '--settings' -PassThru
function Find-Control($root, $property, $value) {
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
        [Windows.Automation.PropertyCondition]::new($property, $value))
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Wait-Control([scriptblock] $get) {
    for ($i = 0; $i -lt 35; $i++) {
        $control = & $get
        if ($null -ne $control) { return $control }
        Start-Sleep -Milliseconds 200
    }
    throw "Control unavailable: $get"
}
try {
    $ae = [Windows.Automation.AutomationElement]
    $root = $ae::RootElement
    $add = Wait-Control { Find-Control $root $ae::NameProperty 'Add calendar' }
    $add.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $password = Wait-Control { Find-Control $root $ae::AutomationIdProperty 'UrlPassword' }
    if (!$password.Current.IsPassword) { throw 'URL field is not masked' }
    $shell = New-Object -ComObject WScript.Shell
    [void] $shell.AppActivate($process.Id)
    $password.SetFocus()
    $original = [Windows.Clipboard]::GetDataObject()
    $sample = 'https://calendar-a.example/calendar.ics'
    try {
        if ($Link) {
            $data = [Windows.DataObject]::new()
            $data.SetData('UniformResourceLocatorW', [IO.MemoryStream]::new([Text.Encoding]::Unicode.GetBytes($sample + [char]0)))
            [Windows.Clipboard]::SetDataObject($data)
        } else {
            [Windows.Clipboard]::SetText($sample)
        }
        if (!$password.Current.HasKeyboardFocus) { throw 'URL field did not receive keyboard focus' }
        [Windows.Forms.SendKeys]::SendWait('^v')
        Start-Sleep -Milliseconds 400
        $show = Wait-Control { Find-Control $root $ae::NameProperty 'Show URL' }
        $show.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $url = Wait-Control {
            $control = Find-Control $root $ae::AutomationIdProperty 'UrlText'
            if ($control -and !$control.Current.IsOffscreen) { $control }
        }
        if ($url.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -cne $sample) {
            throw 'Ctrl+V did not populate masked URL field'
        }
        'Ctrl+V populated masked URL field'
    } finally {
        if ($original) { [Windows.Clipboard]::SetDataObject($original, $true) } else { [Windows.Clipboard]::Clear() }
    }
} finally {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
}
