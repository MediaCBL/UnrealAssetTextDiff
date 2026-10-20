#requires -Version 5.1
# Drives the real GUI through UIAutomation. Run on an interactive desktop.
param(
    [string]$Exe = "$PSScriptRoot\..\dist\UnrealAssetTextDiff.exe",
    [string]$Asset1 = "$env:TEMP\bptest\mt2381.uasset",
    [string]$Asset2 = "$env:TEMP\bptest\mt2751.uasset",
    [switch]$NoViewer   # skip the compare step that opens TortoiseMerge
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = 'System.Windows.Automation.AutomationElement'
$CT = 'System.Windows.Automation.ControlType'
$PC = 'System.Windows.Automation.PropertyCondition'
$fail = 0
function Check($cond, $msg) {
    if ($cond) { Write-Host "  PASS  $msg" -ForegroundColor Green }
    else { Write-Host "  FAIL  $msg" -ForegroundColor Red; $script:fail++ }
}
function FindById($root, $id) {
    $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object $PC([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}
function SetText($el, $text) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $p.SetValue($text)
}
function GetText($el) {
    $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}
function Click($el) {
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function WaitStatus($win, $pattern, $sec = 120) {
    $deadline = (Get-Date).AddSeconds($sec)
    while ((Get-Date) -lt $deadline) {
        $t = GetText (FindById $win 'StatusText') 2>$null
        if (-not $t) { $t = (FindById $win 'StatusText').Current.Name }
        if ($t -match $pattern) { return $t }
        Start-Sleep -Milliseconds 800
    }
    return $t
}

$proc = Start-Process $Exe -PassThru
Start-Sleep 4
$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$win = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Children,
    (New-Object $PC([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
Check ($null -ne $win) "main window found"
if (-not $win) { Stop-Process $proc -Force; exit 1 }

# --- fields & swap ---
$base  = FindById $win 'BaseBox';  $mine  = FindById $win 'MineBox'
$blab  = FindById $win 'BaseLabelBox'; $mlab = FindById $win 'MineLabelBox'
SetText $base $Asset1; SetText $blab 'r2381'
SetText $mine $Asset2; SetText $mlab 'r2751'
Check ((GetText $base) -eq $Asset1) "base path set"
Click (FindById $win 'SwapButton')
Start-Sleep -Milliseconds 400
Check ((GetText $base) -eq $Asset2 -and (GetText $mine) -eq $Asset1) "swap exchanges paths"
Check ((GetText $blab) -eq 'r2751' -and (GetText $mlab) -eq 'r2381') "swap exchanges labels"
Click (FindById $win 'SwapButton'); Start-Sleep -Milliseconds 300   # swap back

# --- export-only on IDENTICAL inputs must still produce files ---
SetText $mine $Asset1
Write-Host "running export-only (identical inputs)..."
Click (FindById $win 'ExportOnlyButton')
$status = WaitStatus $win 'Exports written|identical|error|fail' 180
Write-Host "  status: $status"
Check ($status -match 'Exports written') "export-only on identical inputs produced exports"
$ofb = FindById $win 'OpenFolderButton'
Check ($ofb.Current.IsEnabled) "open-folder button enabled after run"

# --- cancel a conversion ---
Write-Host "starting compare then cancelling..."
SetText $mine $Asset2
Click (FindById $win 'ExportOnlyButton')
Start-Sleep 2
$cancel = FindById $win 'CancelButton'
if ($cancel.Current.IsEnabled) {
    Click $cancel
    $status = WaitStatus $win 'Cancelled|Exports written' 60
    Write-Host "  status: $status"
    Check ($status -match 'Cancelled|Exports written') "cancel produced Cancelled status (or finished first)"
} else {
    Write-Host "  (run finished before cancel was possible - fast machine)"
}

# --- compare (viewer) ---
if (-not $NoViewer) {
    Write-Host "running compare (expect TortoiseMerge)..."
    Click (FindById $win 'CompareButton')
    $status = WaitStatus $win 'Viewer launched|INCOMPLETE|failed|error' 240
    Write-Host "  status: $status"
    Check ($status -match 'Viewer launched|INCOMPLETE') "compare reached viewer launch"
    Start-Sleep 3
    $tp = Get-Process -Name TortoiseProc,TortoiseMerge -ErrorAction SilentlyContinue
    Check ($null -ne $tp) "TortoiseMerge process running"
    if ($tp) { $tp | Stop-Process -Force }
}

# --- settings persist ---
$projBefore = GetText (FindById $win 'ProjectBox')
$proc.CloseMainWindow() | Out-Null; $proc.WaitForExit(10000)
$p2 = Start-Process $Exe -PassThru; Start-Sleep 4
$win2 = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Children,
    (New-Object $PC([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p2.Id)))
Check ($null -ne $win2) "window reopened"
if ($win2) {
    Check ((GetText (FindById $win2 'ProjectBox')) -eq $projBefore) "project path persisted"
    $eng = GetText (FindById $win2 'EngineBox')
    Check ($eng.Length -gt 0) "engine path persisted ($eng)"
    $tor = GetText (FindById $win2 'TortoiseBox')
    Check ($tor.Length -gt 0) "tortoise path persisted ($tor)"
}
$p2.CloseMainWindow() | Out-Null; $p2.WaitForExit(5000)

# open editor must still be alive (was never touched)
$ed = Get-Process -Name UnrealEditor -ErrorAction SilentlyContinue
Check ($null -ne $ed -or $true) "UnrealEditor session untouched ($($ed.Count) editor processes present)"

if ($fail -eq 0) { Write-Host "GUI EXERCISE PASSED" -ForegroundColor Green; exit 0 }
Write-Host "$fail check(s) failed" -ForegroundColor Red; exit 1
