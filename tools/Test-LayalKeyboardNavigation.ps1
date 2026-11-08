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

    # The redesigned shell intentionally hides the TabControl header and exposes destinations
    # through the compact bottom dock. Verify the active page through the live accessible status
    # on MainTabs instead of expecting visual TabItem peers that no longer exist in the UI tree.
    function Get-CurrentSectionStatus {
        return [string]$tabControl.GetCurrentPropertyValue(
            [System.Windows.Automation.AutomationElement]::HelpTextProperty)
    }

    $expectedSectionLabels = @(
        "Chat / Home", "Overview", "Tasks", "Skills",
        "Cameras", "Memory", "API integrations", "Settings"
    )
    $expectedSectionPatterns = @(
        "(?i)(Home\s*\(Chat\)|Chat|المحادثة|الرئيسية)",
        "(?i)(Overview|نظرة عامة)",
        "(?i)(Tasks|المهام)",
        "(?i)(Skills|المهارات)",
        "(?i)(Cameras|الكاميرات)",
        "(?i)(Memory|الذاكرة)",
        "(?i)(API integrations|تكاملات API)",
        "(?i)(Settings|الإعدادات)"
    )
    if ([string]::IsNullOrWhiteSpace((Get-CurrentSectionStatus))) {
        throw "The main navigation does not expose the current page to UI Automation."
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
        $expectedSection = $expectedSectionLabels[$number - 1]
        $expectedPattern = $expectedSectionPatterns[$number - 1]
        [System.Windows.Forms.SendKeys]::SendWait("^$number")
        Start-Sleep -Milliseconds 300
        $selected = Get-CurrentSectionStatus
        $ok = $selected -match $expectedPattern
        $results.Add([pscustomobject]@{
            Test = "Ctrl+$number"
            ExpectedSection = $expectedSection
            SelectedSection = $selected
            Result = if ($ok) { "PASS" } else { "FAIL" }
            Details = "Top-row digit shortcut; verified through MainTabs UI Automation HelpText"
        })
        if (-not $ok) { throw "Ctrl+$number selected '$selected'; expected section '$expectedSection'." }
    }

    for ($number = 1; $number -le 8; $number++) {
        $expectedSection = $expectedSectionLabels[$number - 1]
        $expectedPattern = $expectedSectionPatterns[$number - 1]
        $vk = [byte](0x60 + $number)
        Send-NumpadShortcut -VirtualKey $vk
        Start-Sleep -Milliseconds 300
        $selected = Get-CurrentSectionStatus
        $ok = $selected -match $expectedPattern
        $results.Add([pscustomobject]@{
            Test = "Ctrl+NumPad$number"
            ExpectedSection = $expectedSection
            SelectedSection = $selected
            Result = if ($ok) { "PASS" } else { "FAIL" }
            Details = "Numeric keypad shortcut; verified through MainTabs UI Automation HelpText"
        })
        if (-not $ok) { throw "Ctrl+NumPad$number selected '$selected'; expected section '$expectedSection'." }
    }

    # Ctrl+1 is the real Home route and must move focus into the message field.
    [System.Windows.Forms.SendKeys]::SendWait("^1")
    # UIA focus notifications can arrive after the WPF tab content has completed its layout.
    # Poll the actual focused peer instead of sampling once during that transition.
    $focusDeadline = (Get-Date).AddSeconds(3)
    $focused = $null
    $focusName = ""
    $focusControlType = ""
    do {
        Start-Sleep -Milliseconds 100
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        $focusName = if ($null -eq $focused) { "" } else { $focused.Current.Name }
        $focusControlType = if ($null -eq $focused) { "" } else { $focused.Current.ControlType.ProgrammaticName }
        $focusReady = ($focusName -match "Message|الرسالة|رسالة المحادثة") -and
                      ($focusControlType -eq "ControlType.Edit")
    } while (-not $focusReady -and (Get-Date) -lt $focusDeadline) }
    # The application exposes the accessible textbox name in localized form as "رسالة المحادثة".
    # Verify the focused element is an Edit control instead of depending on one exact translated label.
    $focusSection = Get-CurrentSectionStatus
    $focusOk = ($focusSection -match $expectedSectionPatterns[0]) -and $focusReady
    $results.Add([pscustomobject]@{
        Test = "Ctrl+1 focus target"
        ExpectedSection = "Chat / Home"
        SelectedSection = $focusSection
        Result = if ($focusOk) { "PASS" } else { "FAIL" }
        Details = "Focused element name: $focusName; control type: $focusControlType"
    })
    if (-not $focusOk) { throw "Ctrl+1 did not select Chat/Home and focus the accessible message field; page status was '$focusSection', focused element was '$focusName' with control type '$focusControlType'." }

    # F1 opens a modal help message. Confirm it exists, then close it without triggering another action.
    [System.Windows.Forms.SendKeys]::SendWait("{F1}")
    Start-Sleep -Milliseconds 500
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    # Native MessageBox dialogs may be exposed as top-level windows with a different
    # AutomationElement process association. Search desktop descendants for a Window
    # with the localized dialog title; do not count the always-present help button.
    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $windowCondition)
    $helpFound = $false
    foreach ($candidate in $windows) {
        try {
            if ($candidate.Current.Name -match "Keyboard shortcuts|اختصارات لوحة المفاتيح") {
                $helpFound = $true
                break
            }
        } catch {
            # Ignore stale UIA elements; the acceptance result remains fail-closed if no dialog is found.
        }
    }
    $results.Add([pscustomobject]@{
        Test = "F1 help dialog"
        ExpectedSection = "Chat / Home"
        SelectedSection = (Get-CurrentSectionStatus)
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
