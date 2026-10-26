[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$LayalExePath,
    [string]$LogDirectory = "artifacts/keyboard-acceptance",
    [ValidateRange(10, 60)]
    [int]$StartupTimeoutSeconds = 25
)

$ErrorActionPreference = "Stop"
$resolvedExe = (Resolve-Path -LiteralPath $LayalExePath).Path
if ([IO.Path]::GetFileName($resolvedExe) -ine "Layal.exe") {
    throw "Expected the actual Layal.exe release executable."
}
if (Get-Process -Name "Layal" -ErrorAction SilentlyContinue) {
    throw "A Layal process is already running. Close it before this isolated keyboard test."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$resolvedLogDirectory = [IO.Path]::GetFullPath($LogDirectory)
New-Item -ItemType Directory -Path $resolvedLogDirectory -Force | Out-Null
$csvPath = Join-Path $resolvedLogDirectory "keyboard-shortcuts.csv"
$jsonPath = Join-Path $resolvedLogDirectory "keyboard-summary.json"
Remove-Item -LiteralPath $csvPath, $jsonPath -Force -ErrorAction SilentlyContinue

$nativeCode = @'
using System;
using System.Runtime.InteropServices;
public static class LayalKeyboardNative
{
    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    public const uint KeyUp = 0x0002;
    public const byte Control = 0x11;
    public const byte F1 = 0x70;
    public const byte NumPad1 = 0x61;
}
'@
Add-Type -TypeDefinition $nativeCode -Language CSharp

$results = [System.Collections.Generic.List[object]]::new()
$process = $null
try {
    $process = Start-Process -FilePath $resolvedExe -PassThru
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    $window = $null
    $tabControl = $null
    while ((Get-Date) -lt $deadline -and $null -eq $tabControl) {
        Start-Sleep -Milliseconds 400
        $process.Refresh()
        if ($process.HasExited) { throw "Layal exited before the main window became available." }
        if ($process.MainWindowHandle -eq [IntPtr]::Zero) { continue }
        $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        if ($null -eq $window) { continue }
        $tabCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Tab)
        $tabs = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCondition)
        foreach ($candidate in $tabs) {
            if ($candidate.Current.Name -match "Main navigation|التنقل الرئيسي") {
                $tabControl = $candidate
                break
            }
        }
    }
    if ($null -eq $window -or $null -eq $tabControl) {
        throw "Could not find the main Layal window and its accessible navigation TabControl."
    }

    $tabItemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    $tabItems = $tabControl.FindAll([System.Windows.Automation.TreeScope]::Children, $tabItemCondition)
    if ($tabItems.Count -ne 8) {
        throw "Expected eight navigation tabs, found $($tabItems.Count)."
    }

    function Get-SelectedTabIndex {
        param($Items)
        for ($index = 0; $index -lt $Items.Count; $index++) {
            try {
                if ([bool]$Items[$index].GetCurrentPropertyValue(
                    [System.Windows.Automation.SelectionItemPattern]::IsSelectedProperty)) {
                    return $index + 1
                }
            } catch { }
        }
        return 0
    }

    function Send-NumpadShortcut {
        param([byte]$VirtualKey)
        [LayalKeyboardNative]::keybd_event([LayalKeyboardNative]::Control, 0, 0, [UIntPtr]::Zero)
        [LayalKeyboardNative]::keybd_event($VirtualKey, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        [LayalKeyboardNative]::keybd_event($VirtualKey, 0, [LayalKeyboardNative]::KeyUp, [UIntPtr]::Zero)
        [LayalKeyboardNative]::keybd_event([LayalKeyboardNative]::Control, 0, [LayalKeyboardNative]::KeyUp, [UIntPtr]::Zero)
    }

    [void][LayalKeyboardNative]::SetForegroundWindow($window.Current.NativeWindowHandle)
    $window.SetFocus()
    Start-Sleep -Milliseconds 250

    for ($number = 1; $number -le 8; $number++) {
        [System.Windows.Forms.SendKeys]::SendWait("^$number")
        Start-Sleep -Milliseconds 300
        $selected = Get-SelectedTabIndex -Items $tabItems
        $ok = $selected -eq $number
        $results.Add([pscustomobject]@{
            Test = "Ctrl+$number"
            ExpectedTab = $number
            SelectedTab = $selected
            Result = if ($ok) { "PASS" } else { "FAIL" }
            Details = "Top-row digit shortcut"
        })
        if (-not $ok) { throw "Ctrl+$number selected tab $selected; expected tab $number." }
    }

    for ($number = 1; $number -le 8; $number++) {
        $vk = [byte](0x60 + $number)
        Send-NumpadShortcut -VirtualKey $vk
        Start-Sleep -Milliseconds 300
        $selected = Get-SelectedTabIndex -Items $tabItems
        $ok = $selected -eq $number
        $results.Add([pscustomobject]@{
            Test = "Ctrl+NumPad$number"
            ExpectedTab = $number
            SelectedTab = $selected
            Result = if ($ok) { "PASS" } else { "FAIL" }
            Details = "Numeric keypad shortcut"
        })
        if (-not $ok) { throw "Ctrl+NumPad$number selected tab $selected; expected tab $number." }
    }

    # Ctrl+2 must move keyboard focus into the message field, not merely change tabs.
    [System.Windows.Forms.SendKeys]::SendWait("^2")
    Start-Sleep -Milliseconds 350
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    $focusName = if ($null -eq $focused) { "" } else { $focused.Current.Name }
    $focusOk = $focusName -match "Message|الرسالة"
    $results.Add([pscustomobject]@{
        Test = "Ctrl+2 focus target"
        ExpectedTab = 2
        SelectedTab = (Get-SelectedTabIndex -Items $tabItems)
        Result = if ($focusOk) { "PASS" } else { "FAIL" }
        Details = "Focused element name: $focusName"
    })
    if (-not $focusOk) { throw "Ctrl+2 did not focus the accessible message field; focused element was '$focusName'." }

    # F1 opens a modal help message. Confirm it exists, then close it without triggering another action.
    [System.Windows.Forms.SendKeys]::SendWait("{F1}")
    Start-Sleep -Milliseconds 500
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $windows = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)))
    $helpFound = $false
    foreach ($candidate in $windows) {
        if ($candidate.Current.Name -match "Keyboard shortcuts|اختصارات لوحة المفاتيح") {
            $helpFound = $true
            break
        }
    }
    $results.Add([pscustomobject]@{
        Test = "F1 help dialog"
        ExpectedTab = 2
        SelectedTab = (Get-SelectedTabIndex -Items $tabItems)
        Result = if ($helpFound) { "PASS" } else { "FAIL" }
        Details = "Keyboard help dialog visible: $helpFound"
    })
    if (-not $helpFound) { throw "F1 did not expose an accessible keyboard-help dialog." }
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Start-Sleep -Milliseconds 250

    $results | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
    $summary = [pscustomobject]@{
        Result = "passed"
        ProcessId = $process.Id
        Executable = $resolvedExe
        ShortcutCases = $results.Count
        Passed = @($results | Where-Object Result -eq "PASS").Count
        Failed = @($results | Where-Object Result -eq "FAIL").Count
        ManualScreenReaderAcceptance = "pending"
        ManualHighContrastAcceptance = "pending"
        Tests = @($results)
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    Write-Host "Keyboard shortcuts: PASS ($($summary.Passed)/$($summary.ShortcutCases)); manual screen-reader and High Contrast acceptance remain pending."
}
catch {
    $results | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
    [pscustomobject]@{
        Result = "failed"
        Error = $_.Exception.Message
        Tests = @($results)
        ManualScreenReaderAcceptance = "pending"
        ManualHighContrastAcceptance = "pending"
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    throw
}
finally {
    if ($null -ne $process) {
        try {
            if (-not $process.HasExited) {
                $process.CloseMainWindow() | Out-Null
                if (-not $process.WaitForExit(3000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
            }
        } catch { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
}
