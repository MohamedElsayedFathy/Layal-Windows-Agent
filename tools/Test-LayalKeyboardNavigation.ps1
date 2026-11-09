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
    # through the persistent left navigation rail. Verify the active page through the live accessible status
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
    $focusAutomationId = ""
    do {
        Start-Sleep -Milliseconds 100
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        $focusName = if ($null -eq $focused) { "" } else { $focused.Current.Name }
        $focusControlType = if ($null -eq $focused) { "" } else { $focused.Current.ControlType.ProgrammaticName }
        $focusAutomationId = if ($null -eq $focused) { "" } else { $focused.Current.AutomationId }
        $focusReady = ($focusAutomationId -eq "ChatInput") -and
                      ($focusControlType -eq "ControlType.Edit")
    } while (-not $focusReady -and (Get-Date) -lt $focusDeadline)
    # The application exposes the accessible textbox name in localized form as "رسالة المحادثة".
    # Verify the focused element is an Edit control instead of depending on one exact translated label.
    $editCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $editPeers = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
    $editDiagnostics = @()
    foreach ($editPeer in $editPeers) {
        try {
            $editDiagnostics += [pscustomobject]@{
                Name = $editPeer.Current.Name
                AutomationId = $editPeer.Current.AutomationId
                Enabled = [bool]$editPeer.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsEnabledProperty)
                KeyboardFocusable = [bool]$editPeer.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsKeyboardFocusableProperty)
                Offscreen = [bool]$editPeer.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsOffscreenProperty)
                HasKeyboardFocus = [bool]$editPeer.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty)
            }
        } catch {
            $editDiagnostics += [pscustomobject]@{ Name = "stale Edit peer"; AutomationId = ""; Enabled = $false; KeyboardFocusable = $false; Offscreen = $true; HasKeyboardFocus = $false }
        }
    }
    $editDiagnosticText = if ($editDiagnostics.Count -eq 0) {
        "No Edit peers exposed below the main window."
    } else {
        ($editDiagnostics | ConvertTo-Json -Compress -Depth 4)
    }

    $focusSection = Get-CurrentSectionStatus
    $focusOk = ($focusSection -match $expectedSectionPatterns[0]) -and $focusReady
    $results.Add([pscustomobject]@{
        Test = "Ctrl+1 focus target"
        ExpectedSection = "Chat / Home"
        SelectedSection = $focusSection
        Result = if ($focusOk) { "PASS" } else { "FAIL" }
        Details = "Focused element name: $focusName; automation id: $focusAutomationId; control type: $focusControlType; Edit peers: $editDiagnosticText"
    })
    if (-not $focusOk) { throw "Ctrl+1 did not select Chat/Home and focus the accessible message field; page status was '$focusSection', focused element was '$focusName' (AutomationId='$focusAutomationId') with control type '$focusControlType'. UIA Edit peers: $editDiagnosticText" }

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

    # Exercise the persistent left navigation rail through UI Automation and keyboard input.
    # Stable AutomationIds keep this test independent from Arabic/English display text.
    [System.Windows.Forms.SendKeys]::SendWait("^1")
    Start-Sleep -Milliseconds 250
    $toggleCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        "ToggleBottomDockButton")
    $toggleElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $toggleCondition)
    if ($null -eq $toggleElement) {
        throw "The navigation rail toggle is not exposed to UI Automation."
    }

    $initialRailName = $toggleElement.Current.Name
    $initialRailExpanded = $initialRailName -match "(?i)(Collapse navigation rail|طي شريط التنقل الجانبي)"
    $results.Add([pscustomobject]@{
        Test = "Navigation rail starts expanded"
        ExpectedSection = "Chat / Home"
        SelectedSection = (Get-CurrentSectionStatus)
        Result = if ($initialRailExpanded) { "PASS" } else { "FAIL" }
        Details = "Initial navigation toggle name: $initialRailName"
    })
    if (-not $initialRailExpanded) { throw "The navigation rail did not start expanded by default." }

    $toggleElement.SetFocus()
    Start-Sleep -Milliseconds 120
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    $toggleFocused = $null -ne $focused -and $focused.Current.AutomationId -eq "ToggleBottomDockButton"
    $results.Add([pscustomobject]@{
        Test = "Navigation rail toggle focus"
        ExpectedSection = "Chat / Home"
        SelectedSection = (Get-CurrentSectionStatus)
        Result = if ($toggleFocused) { "PASS" } else { "FAIL" }
        Details = "AutomationId=$($toggleElement.Current.AutomationId); focused AutomationId=$(if ($null -eq $focused) { '' } else { $focused.Current.AutomationId })"
    })
    if (-not $toggleFocused) { throw "The navigation rail toggle could not receive keyboard focus." }

    [System.Windows.Forms.SendKeys]::SendWait(" ")
    Start-Sleep -Milliseconds 450
    $toggleElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $toggleCondition)
    $collapsedRailName = if ($null -eq $toggleElement) { "" } else { $toggleElement.Current.Name }
    $railCollapsed = $collapsedRailName -match "(?i)(Expand navigation rail|توسيع شريط التنقل الجانبي)"
    $results.Add([pscustomobject]@{
        Test = "Space collapses left navigation rail"
        ExpectedSection = "Chat / Home"
        SelectedSection = (Get-CurrentSectionStatus)
        Result = if ($railCollapsed) { "PASS" } else { "FAIL" }
        Details = "Navigation toggle name after collapse: $collapsedRailName"
    })
    if (-not $railCollapsed) { throw "Space did not collapse the left navigation rail." }

    $toggleElement.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait(" ")
    Start-Sleep -Milliseconds 450
    $toggleElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $toggleCondition)
    $expandedRailName = if ($null -eq $toggleElement) { "" } else { $toggleElement.Current.Name }
    $railExpanded = $expandedRailName -match "(?i)(Collapse navigation rail|طي شريط التنقل الجانبي)"
    $dockChatCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        "DockChatButton")
    $dockChatElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $dockChatCondition)
    $dockExpanded = $null -ne $dockChatElement -and
        -not [bool]$dockChatElement.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsOffscreenProperty)
    $results.Add([pscustomobject]@{
        Test = "Space expands left navigation rail"
        ExpectedSection = "Chat / Home"
        SelectedSection = (Get-CurrentSectionStatus)
        Result = if ($railExpanded -and $dockExpanded) { "PASS" } else { "FAIL" }
        Details = "Navigation toggle name=$expandedRailName; first destination on-screen=$dockExpanded"
    })
    if (-not ($railExpanded -and $dockExpanded)) { throw "Space did not expand the left navigation rail and expose its destinations." }

    [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
    Start-Sleep -Milliseconds 120
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    $tabEntersDock = $null -ne $focused -and $focused.Current.AutomationId -eq "DockChatButton"
    $results.Add([pscustomobject]@{
        Test = "Tab enters left navigation rail"
        ExpectedSection = "Chat / Home"
        SelectedSection = (Get-CurrentSectionStatus)
        Result = if ($tabEntersDock) { "PASS" } else { "FAIL" }
        Details = "Focused AutomationId=$(if ($null -eq $focused) { '' } else { $focused.Current.AutomationId })"
    })
    if (-not $tabEntersDock) { throw "Tab did not move from the rail toggle to the first navigation destination." }

    [System.Windows.Forms.SendKeys]::SendWait("+{TAB}")
    Start-Sleep -Milliseconds 120
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    $reverseDockFocus = $null -ne $focused -and $focused.Current.AutomationId -eq "ToggleBottomDockButton"
    $results.Add([pscustomobject]@{
        Test = "Shift+Tab returns to rail toggle"
        ExpectedSection = "Chat / Home"
        SelectedSection = (Get-CurrentSectionStatus)
        Result = if ($reverseDockFocus) { "PASS" } else { "FAIL" }
        Details = "Focused AutomationId=$(if ($null -eq $focused) { '' } else { $focused.Current.AutomationId })"
    })
    if (-not $reverseDockFocus) { throw "Shift+Tab did not return focus to the navigation rail toggle." }

    [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
    Start-Sleep -Milliseconds 120
    $expectedDockIds = @(
        "DockChatButton", "DockOverviewButton", "DockTasksButton", "DockSkillsButton",
        "DockCamerasButton", "DockMemoryButton", "DockApiButton", "DockSettingsButton"
    )
    $expectedDockNamePatterns = @(
        "(?i)(Home\\s*-\\s*Chat|الرئيسية\\s*-\\s*المحادثة)",
        "(?i)(Overview|نظرة عامة)",
        "(?i)(Tasks|المهام)",
        "(?i)(Skills|المهارات)",
        "(?i)(Cameras|الكاميرات)",
        "(?i)(Memory|الذاكرة)",
        "(?i)(API integrations|تكاملات API)",
        "(?i)(Settings|الإعدادات)"
    )
    for ($index = 0; $index -lt $expectedDockIds.Count; $index++) {
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        $focusedId = if ($null -eq $focused) { "" } else { $focused.Current.AutomationId }
        $focusedName = if ($null -eq $focused) { "" } else { $focused.Current.Name }
        $expectedId = $expectedDockIds[$index]
        $expectedNamePattern = $expectedDockNamePatterns[$index]
        $dockButtonOk = $focusedId -eq $expectedId -and $focusedName -match $expectedNamePattern
        $results.Add([pscustomobject]@{
            Test = "Tab rail destination $($index + 1)"
            ExpectedSection = $expectedId
            SelectedSection = (Get-CurrentSectionStatus)
            Result = if ($dockButtonOk) { "PASS" } else { "FAIL" }
            Details = "Focused AutomationId=$focusedId; Name=$focusedName; expected=$expectedId"
        })
        if (-not $dockButtonOk) {
            throw "Tab order mismatch in left navigation rail at destination $($index + 1): expected $expectedId, focused $focusedId ('$focusedName')."
        }
        if ($index -lt $expectedDockIds.Count - 1) {
            [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
            Start-Sleep -Milliseconds 120
        }
    }

    [System.Windows.Forms.SendKeys]::SendWait(" ")
    Start-Sleep -Milliseconds 350
    $selectedDockPage = Get-CurrentSectionStatus
    $settingsSelected = $selectedDockPage -match "(?i)(Settings|الإعدادات)"
    $results.Add([pscustomobject]@{
        Test = "Space activates focused rail destination"
        ExpectedSection = "Settings"
        SelectedSection = $selectedDockPage
        Result = if ($settingsSelected) { "PASS" } else { "FAIL" }
        Details = "Focused Settings destination activated through Space."
    })
    if (-not $settingsSelected) { throw "Space did not navigate to the focused Settings destination; current page was '$selectedDockPage'." }

    [System.Windows.Forms.SendKeys]::SendWait("^1")
    Start-Sleep -Milliseconds 250
    $toggleElement = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $toggleCondition)
    if ($null -eq $toggleElement) { throw "The navigation rail toggle disappeared after returning to Chat." }
    $toggleName = $toggleElement.Current.Name
    $railRemainsExpanded = $toggleName -match "(?i)(Collapse navigation rail|طي شريط التنقل الجانبي)"
    $results.Add([pscustomobject]@{
        Test = "Navigation rail remains expanded after page navigation"
        ExpectedSection = "Chat / Home"
        SelectedSection = (Get-CurrentSectionStatus)
        Result = if ($railRemainsExpanded) { "PASS" } else { "FAIL" }
        Details = "Navigation toggle accessible name: $toggleName"
    })
    if (-not $railRemainsExpanded) { throw "Page navigation unexpectedly collapsed the left navigation rail." }

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
