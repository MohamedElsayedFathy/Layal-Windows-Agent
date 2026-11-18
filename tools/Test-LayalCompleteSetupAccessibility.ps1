[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$SessionDirectory,

    [string]$LogDirectory,

    [ValidateRange(4, 16)]
    [int]$TabSteps = 8,

    [switch]$LaunchNarrator,

    [switch]$ExerciseHighContrast
)

$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Complete Setup accessibility acceptance must run on Windows.'
}
if (-not [Environment]::UserInteractive) {
    throw 'A non-interactive session cannot run UI Automation acceptance.'
}
if ($env:GITHUB_ACTIONS -eq 'true') {
    throw 'This check must run on the interactive Windows desktop, not in GitHub Actions.'
}

$sessionRoot = (Resolve-Path -LiteralPath $SessionDirectory).Path
$isolatedRoot = Join-Path $sessionRoot 'isolated-current-user-profile'
$expectedExe = [IO.Path]::GetFullPath((Join-Path $sessionRoot 'portable-extracted\Layal.exe'))
if (-not (Test-Path -LiteralPath $isolatedRoot -PathType Container)) {
    throw "The isolated acceptance root does not exist: $isolatedRoot"
}
if (-not (Test-Path -LiteralPath $expectedExe -PathType Leaf)) {
    throw "The portable Layal.exe for this acceptance session does not exist: $expectedExe"
}
if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $LogDirectory = Join-Path $sessionRoot 'complete-setup-accessibility'
}
if (-not [IO.Path]::IsPathRooted($LogDirectory)) {
    $LogDirectory = Join-Path $sessionRoot $LogDirectory
}
$resolvedLogDirectory = [IO.Path]::GetFullPath($LogDirectory)
New-Item -ItemType Directory -Path $resolvedLogDirectory -Force | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$nativeCode = @'
using System;
using System.Runtime.InteropServices;

public static class LayalCompleteSetupAccessibilityNative
{
    private const uint SPI_GETHIGHCONTRAST = 0x0042;
    private const uint SPI_SETHIGHCONTRAST = 0x0043;
    private const uint SPIF_SENDCHANGE = 0x0002;
    private const uint HCF_HIGHCONTRASTON = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIGHCONTRAST
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr lpszDefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(
        uint uiAction, uint uiParam, ref HIGHCONTRAST pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static bool IsHighContrastEnabled()
    {
        var state = new HIGHCONTRAST();
        state.cbSize = (uint)Marshal.SizeOf(typeof(HIGHCONTRAST));
        if (!SystemParametersInfo(SPI_GETHIGHCONTRAST, state.cbSize, ref state, 0))
            throw new InvalidOperationException(
                "SPI_GETHIGHCONTRAST failed. Win32 error: " + Marshal.GetLastWin32Error());
        return (state.dwFlags & HCF_HIGHCONTRASTON) != 0;
    }

    public static void SetHighContrastEnabled(bool enabled)
    {
        var state = new HIGHCONTRAST();
        state.cbSize = (uint)Marshal.SizeOf(typeof(HIGHCONTRAST));
        if (!SystemParametersInfo(SPI_GETHIGHCONTRAST, state.cbSize, ref state, 0))
            throw new InvalidOperationException(
                "Could not read the current High Contrast settings. Win32 error: " + Marshal.GetLastWin32Error());

        if (enabled)
            state.dwFlags |= HCF_HIGHCONTRASTON;
        else
            state.dwFlags &= ~HCF_HIGHCONTRASTON;

        // SPIF_SENDCHANGE broadcasts the temporary change without requesting an INI update.
        if (!SystemParametersInfo(SPI_SETHIGHCONTRAST, state.cbSize, ref state, SPIF_SENDCHANGE))
            throw new InvalidOperationException(
                "SPI_SETHIGHCONTRAST failed. Win32 error: " + Marshal.GetLastWin32Error());
    }

    public static void BringToFront(IntPtr hWnd)
    {
        ShowWindowAsync(hWnd, 9); // SW_RESTORE
        SetForegroundWindow(hWnd);
    }
}
'@
Add-Type -TypeDefinition $nativeCode -Language CSharp

function Get-ElementRuntimeId {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element)
    try {
        return [string]::Join('.', $Element.GetRuntimeId())
    }
    catch {
        return ''
    }
}

function Find-SetupElement {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Window,

        [Parameter(Mandatory = $true)]
        [string[]]$Names,

        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.ControlType]$ControlType
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        $ControlType)
    $matches = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($candidate in $matches) {
        try {
            if ($Names -contains $candidate.Current.Name) {
                return $candidate
            }
        }
        catch {
            # UIA may invalidate a peer while WPF is updating; ignore that peer and keep searching.
        }
    }
    return $null
}

function Save-SetupWindowScreenshot {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Window,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $rectangle = $Window.Current.BoundingRectangle
    $left = [int][Math]::Floor($rectangle.Left)
    $top = [int][Math]::Floor($rectangle.Top)
    $width = [int][Math]::Ceiling($rectangle.Width)
    $height = [int][Math]::Ceiling($rectangle.Height)
    if ($width -lt 100 -or $height -lt 100) {
        throw "The Complete Setup window has an invalid screenshot rectangle: $rectangle"
    }

    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(
            $left,
            $top,
            0,
            0,
            $bitmap.Size,
            [System.Drawing.CopyPixelOperation]::SourceCopy)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$results = [System.Collections.Generic.List[object]]::new()
$focusResults = [System.Collections.Generic.List[object]]::new()
$process = $null
$window = $null
$narratorStartedByScript = $false
$originalHighContrast = $null
$highContrastChangedByScript = $false
$highContrastStatus = 'NOT RUN'
$screenReaderStatus = 'NOT RUN'

try {
    $matchingProcesses = @(
        Get-CimInstance -ClassName Win32_Process -Filter "Name = 'Layal.exe'" |
            Where-Object {
                $_.ExecutablePath -and
                [IO.Path]::GetFullPath($_.ExecutablePath).Equals(
                    $expectedExe,
                    [StringComparison]::OrdinalIgnoreCase)
            }
    )
    if ($matchingProcesses.Count -ne 1) {
        throw "Expected exactly one Layal process running from this isolated session. Found: $($matchingProcesses.Count). Close old Layal windows and start the session executable with the documented acceptance environment variables."
    }

    $process = Get-Process -Id ([int]$matchingProcesses[0].ProcessId) -ErrorAction Stop
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'The isolated Layal process does not currently expose a main window handle.'
    }

    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    if ($null -eq $window) {
        throw 'UI Automation could not attach to the Layal Complete Setup window.'
    }
    if ($window.Current.Name -notmatch 'Layal complete setup|إعداد ليال الكامل') {
        throw "The attached window is not Complete Setup. Window title: '$($window.Current.Name)'"
    }

    $controlSpecs = @(
        @{
            Id = 'consent'
            Names = @('Approve local resource setup', 'الموافقة على إعداد الموارد المحلية')
            Type = [System.Windows.Automation.ControlType]::CheckBox
            Required = $true
        },
        @{
            Id = 'start-full-setup'
            Names = @('Start full Layal resource setup', 'بدء الإعداد الكامل لموارد ليال')
            Type = [System.Windows.Automation.ControlType]::Button
            Required = $true
        },
        @{
            Id = 'limited-mode'
            Names = @('Continue temporarily in limited mode', 'المتابعة مؤقتًا بالوضع المحدود')
            Type = [System.Windows.Automation.ControlType]::Button
            Required = $true
        },
        @{
            Id = 'close'
            Names = @('Close setup wizard', 'إغلاق معالج الإعداد')
            Type = [System.Windows.Automation.ControlType]::Button
            Required = $true
        },
        @{
            Id = 'choose-resource-folder'
            Names = @('Choose an alternative local resource folder', 'اختيار مجلد موارد محلي بديل')
            Type = [System.Windows.Automation.ControlType]::Button
            Required = $true
        },
        @{
            Id = 'resource-folder-path'
            Names = @('Layal local resource storage path', 'مسار تخزين موارد ليال المحلية')
            Type = [System.Windows.Automation.ControlType]::Text
            Required = $true
        },
        @{
            Id = 'connectivity-status'
            Names = @('Official download source connectivity', 'حالة الاتصال بمصادر التنزيل')
            Type = [System.Windows.Automation.ControlType]::Text
            Required = $true
        },
        @{
            Id = 'setup-status'
            Names = @('Layal setup status', 'حالة إعداد ليال')
            Type = [System.Windows.Automation.ControlType]::Text
            Required = $true
        },
        @{
            Id = 'progress'
            Names = @('Setup progress', 'تقدم الإعداد')
            Type = [System.Windows.Automation.ControlType]::ProgressBar
            Required = $false
        },
        @{
            Id = 'diagnostics'
            Names = @('Resource setup diagnostics', 'سجل تشخيص إعداد الموارد')
            Type = [System.Windows.Automation.ControlType]::Edit
            Required = $true
        }
    )

    $uiElements = @($window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition))

    $controlReport = [System.Collections.Generic.List[object]]::new()
    $foundById = @{}
    foreach ($spec in $controlSpecs) {
        $element = Find-SetupElement -Window $window -Names $spec.Names -ControlType $spec.Type
        $found = $null -ne $element
        $name = if ($found) { $element.Current.Name } else { '' }
        $enabled = if ($found) { [bool]$element.Current.IsEnabled } else { $false }
        $offscreen = if ($found) { [bool]$element.Current.IsOffscreen } else { $true }
        $controlReport.Add([pscustomobject]@{
            Id = $spec.Id
            Required = [bool]$spec.Required
            Found = $found
            Name = $name
            ExpectedControlType = $spec.Type.ProgrammaticName
            Enabled = $enabled
            Offscreen = $offscreen
            Result = if ($found -or -not $spec.Required) { 'PASS' } else { 'FAIL' }
        })
        if ($found) {
            $foundById[$spec.Id] = $element
        }
        elseif ($spec.Required) {
            throw "Required accessible UIA element '$($spec.Id)' was not found with an expected name and control type."
        }
    }

    # The first-run safety gate must remain untouched during this test.
    $consentPattern = $foundById['consent'].GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $consentState = $consentPattern.Current.ToggleState.ToString()
    $startEnabled = [bool]$foundById['start-full-setup'].Current.IsEnabled
    $safetyOk = ($consentState -eq 'Off' -and -not $startEnabled)
    $results.Add([pscustomobject]@{
        Test = 'No setup consent or download action'
        Result = if ($safetyOk) { 'PASS' } else { 'FAIL' }
        ConsentState = $consentState
        StartButtonEnabled = $startEnabled
        Details = 'The consent checkbox must remain unchecked and Start full setup must remain disabled.'
    })
    if (-not $safetyOk) {
        throw "Safety precondition failed. Consent=$consentState; StartEnabled=$startEnabled. Do not continue the test until setup is stopped and consent is unchecked."
    }

    $results.Add([pscustomobject]@{
        Test = 'Required UI Automation names and control types'
        Result = 'PASS'
        RequiredElements = @($controlReport | Where-Object Required).Count
        FoundRequiredElements = @($controlReport | Where-Object { $_.Required -and $_.Found }).Count
        Details = 'All required controls and status elements expose the expected localized UIA names and control types.'
    })

    if ($LaunchNarrator) {
        $narratorPath = Join-Path $env:WINDIR 'System32\Narrator.exe'
        if (-not (Test-Path -LiteralPath $narratorPath -PathType Leaf)) {
            $screenReaderStatus = 'BLOCKED: Narrator.exe not found.'
        }
        else {
            $existingNarrator = Get-Process -Name 'Narrator' -ErrorAction SilentlyContinue
            if (-not $existingNarrator) {
                Start-Process -FilePath $narratorPath | Out-Null
                $narratorStartedByScript = $true
                Start-Sleep -Seconds 2
            }
            $screenReaderStatus = 'Narrator process is running; spoken output is NOT VERIFIED by PowerShell.'
        }
    }
    else {
        $screenReaderStatus = 'UIA metadata checked; Narrator was not launched.'
    }

    # Capture the standard appearance before the optional high-contrast exercise.
    $normalScreenshot = Join-Path $resolvedLogDirectory 'complete-setup-standard.png'
    try {
        Save-SetupWindowScreenshot -Window $window -Path $normalScreenshot
    }
    catch {
        $normalScreenshot = ''
        $results.Add([pscustomobject]@{
            Test = 'Standard appearance screenshot'
            Result = 'BLOCKED'
            Details = $_.Exception.Message
        })
    }

    # Exercise a real Windows High Contrast state only when explicitly requested.
    # The original flag is restored in finally; SPIF_SENDCHANGE is used without INI persistence.
    if ($ExerciseHighContrast) {
        try {
            $originalHighContrast = [LayalCompleteSetupAccessibilityNative]::IsHighContrastEnabled()
            if (-not $originalHighContrast) {
                [LayalCompleteSetupAccessibilityNative]::SetHighContrastEnabled($true)
                $highContrastChangedByScript = $true
            }

            $deadline = (Get-Date).AddSeconds(5)
            while (-not [LayalCompleteSetupAccessibilityNative]::IsHighContrastEnabled() -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 200
            }

            if (-not [LayalCompleteSetupAccessibilityNative]::IsHighContrastEnabled()) {
                throw 'Windows did not report High Contrast as enabled after the temporary switch.'
            }

            Start-Sleep -Seconds 2
            [LayalCompleteSetupAccessibilityNative]::BringToFront($window.Current.NativeWindowHandle)
            $highContrastScreenshot = Join-Path $resolvedLogDirectory 'complete-setup-high-contrast.png'
            Save-SetupWindowScreenshot -Window $window -Path $highContrastScreenshot

            # Re-query every required element after the live theme change.
            foreach ($spec in ($controlSpecs | Where-Object Required)) {
                $element = Find-SetupElement -Window $window -Names $spec.Names -ControlType $spec.Type
                if ($null -eq $element) {
                    throw "UIA element '$($spec.Id)' disappeared while High Contrast was enabled."
                }
            }

            $highContrastStatus = 'PASS: Windows High Contrast enabled; required UIA elements remain present; screenshot captured. Visual legibility still requires review.'
            $results.Add([pscustomobject]@{
                Test = 'High Contrast state and UIA continuity'
                Result = 'PASS'
                Details = 'Windows reports High Contrast enabled and all required UIA elements remain discoverable. Screenshot is saved for visual review.'
                Screenshot = $highContrastScreenshot
                VisualReview = 'PENDING'
            })
        }
        catch {
            $highContrastStatus = 'BLOCKED: ' + $_.Exception.Message
            $results.Add([pscustomobject]@{
                Test = 'High Contrast state and UIA continuity'
                Result = 'BLOCKED'
                Details = $_.Exception.Message
                VisualReview = 'PENDING'
            })
        }
        finally {
            if ($highContrastChangedByScript -and $null -ne $originalHighContrast) {
                try {
                    [LayalCompleteSetupAccessibilityNative]::SetHighContrastEnabled([bool]$originalHighContrast)
                    Start-Sleep -Seconds 2
                }
                catch {
                    $highContrastStatus = 'RESTORE ERROR: ' + $_.Exception.Message
                    Write-Warning $highContrastStatus
                }
            }
        }
    }
    else {
        $highContrastStatus = 'NOT RUN: pass -ExerciseHighContrast to temporarily toggle Windows High Contrast and capture evidence.'
    }

    # Return focus to the target window and verify forward/reverse keyboard traversal.
    [LayalCompleteSetupAccessibilityNative]::BringToFront($window.Current.NativeWindowHandle)
    $window.SetFocus()
    Start-Sleep -Milliseconds 350

    for ($index = 1; $index -le $TabSteps; $index++) {
        [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
        Start-Sleep -Milliseconds 180

        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        if ($null -eq $focused) {
            throw "Tab traversal step $index did not expose a focused UIA element."
        }
        $focusedProcessId = [int]$focused.Current.ProcessId
        if ($focusedProcessId -ne $process.Id) {
            throw "Tab traversal step $index moved focus outside Layal (process $focusedProcessId)."
        }

        $name = [string]$focused.Current.Name
        $controlType = [string]$focused.Current.ControlType.ProgrammaticName
        $runtimeId = Get-ElementRuntimeId -Element $focused
        $keyboardFocusable = [bool]$focused.Current.IsKeyboardFocusable
        $isEnabled = [bool]$focused.Current.IsEnabled

        $focusResults.Add([pscustomobject]@{
            Step = $index
            Key = 'Tab'
            Name = $name
            ControlType = $controlType
            AutomationId = [string]$focused.Current.AutomationId
            RuntimeId = $runtimeId
            KeyboardFocusable = $keyboardFocusable
            Enabled = $isEnabled
            Result = if (-not [string]::IsNullOrWhiteSpace($name) -and $keyboardFocusable -and $isEnabled) { 'PASS' } else { 'FAIL' }
        })

        if ([string]::IsNullOrWhiteSpace($name) -or -not $keyboardFocusable -or -not $isEnabled) {
            throw "Tab traversal step $index reached an unnamed, disabled, or non-focusable element (Name='$name', Type='$controlType')."
        }
    }

    $distinctFocusTargets = @(
        $focusResults |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_.RuntimeId) } |
            Select-Object -ExpandProperty RuntimeId -Unique
    )
    if ($distinctFocusTargets.Count -lt 3) {
        throw "Tab traversal reached only $($distinctFocusTargets.Count) distinct focus targets; at least three are required."
    }

    # One reverse step must return to the element immediately before the last forward Tab.
    $expectedReverseRuntimeId = $focusResults[$focusResults.Count - 2].RuntimeId
    [System.Windows.Forms.SendKeys]::SendWait('+{TAB}')
    Start-Sleep -Milliseconds 180
    $reverseFocused = [System.Windows.Automation.AutomationElement]::FocusedElement
    $actualReverseRuntimeId = if ($null -eq $reverseFocused) { '' } else { Get-ElementRuntimeId -Element $reverseFocused }
    $reverseOk = (
        $null -ne $reverseFocused -and
        [int]$reverseFocused.Current.ProcessId -eq $process.Id -and
        $actualReverseRuntimeId -eq $expectedReverseRuntimeId
    )
    $focusResults.Add([pscustomobject]@{
        Step = $TabSteps + 1
        Key = 'Shift+Tab'
        Name = if ($null -eq $reverseFocused) { '' } else { [string]$reverseFocused.Current.Name }
        ControlType = if ($null -eq $reverseFocused) { '' } else { [string]$reverseFocused.Current.ControlType.ProgrammaticName }
        AutomationId = if ($null -eq $reverseFocused) { '' } else { [string]$reverseFocused.Current.AutomationId }
        RuntimeId = $actualReverseRuntimeId
        KeyboardFocusable = if ($null -eq $reverseFocused) { $false } else { [bool]$reverseFocused.Current.IsKeyboardFocusable }
        Enabled = if ($null -eq $reverseFocused) { $false } else { [bool]$reverseFocused.Current.IsEnabled }
        Result = if ($reverseOk) { 'PASS' } else { 'FAIL' }
    })
    if (-not $reverseOk) {
        throw "Shift+Tab did not return to the previous focus target. Expected RuntimeId '$expectedReverseRuntimeId'; actual '$actualReverseRuntimeId'."
    }

    $results.Add([pscustomobject]@{
        Test = 'Tab and Shift+Tab traversal'
        Result = 'PASS'
        DistinctFocusTargets = $distinctFocusTargets.Count
        ForwardSteps = $TabSteps
        ReverseStepReturnedToPreviousTarget = $true
        Details = 'UIA focus stayed inside Layal, each reached target had a name, and Shift+Tab reversed the last Tab move.'
    })

    $focusCsv = Join-Path $resolvedLogDirectory 'focus-traversal.csv'
    $controlCsv = Join-Path $resolvedLogDirectory 'ui-automation-controls.csv'
    $reportPath = Join-Path $resolvedLogDirectory 'complete-setup-accessibility-report.json'
    $results | Export-Csv -LiteralPath (Join-Path $resolvedLogDirectory 'test-results.csv') -NoTypeInformation -Encoding UTF8
    $focusResults | Export-Csv -LiteralPath $focusCsv -NoTypeInformation -Encoding UTF8
    $controlReport | Export-Csv -LiteralPath $controlCsv -NoTypeInformation -Encoding UTF8

    $report = [pscustomobject]@{
        SchemaVersion = 1
        CreatedAtLocal = [DateTimeOffset]::Now.ToString('o')
        Result = 'PASS'
        SessionDirectory = $sessionRoot
        Executable = $expectedExe
        ProcessId = $process.Id
        WindowTitle = $window.Current.Name
        ConsentState = $consentState
        StartButtonEnabled = $startEnabled
        UiAutomationRequiredElements = @($controlReport)
        FocusTraversal = @($focusResults)
        Tests = @($results)
        NarratorProcess = $screenReaderStatus
        NarratorSpokenOutput = 'NOT TESTED: PowerShell verifies UIA names and can launch Narrator, but cannot determine whether the spoken announcement is correct or understandable.'
        HighContrastAutomation = $highContrastStatus
        HighContrastVisualAssessment = 'PENDING: inspect complete-setup-high-contrast.png; screenshots and UIA presence do not prove visual contrast quality.'
        ManualAcceptance = [pscustomobject]@{
            A11Y06NarratorSpokenOutput = 'PENDING'
            A11Y07NvdaSpokenOutput = 'PENDING'
            A11Y08HighContrastVisualLegibility = 'PENDING'
            A11Y13CompleteSetupScreenReaderAndKeyboard = 'PENDING HUMAN REVIEW'
            A11Y14CompleteSetupHighContrastVisualLegibility = 'PENDING HUMAN REVIEW'
        }
        Evidence = [pscustomobject]@{
            StandardScreenshot = $normalScreenshot
            FocusCsv = $focusCsv
            ControlsCsv = $controlCsv
            ReportJson = $reportPath
        }
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8

    Write-Host ''
    Write-Host 'Complete Setup PowerShell accessibility check: PASS' -ForegroundColor Green
    Write-Host "UIA required elements: $(@($controlReport | Where-Object { $_.Required -and $_.Found }).Count)/$(@($controlReport | Where-Object Required).Count)"
    Write-Host "Keyboard focus targets: $($distinctFocusTargets.Count) distinct; Tab=$TabSteps; Shift+Tab=PASS"
    Write-Host "Consent: $consentState; Start full setup enabled: $startEnabled"
    Write-Host "Narrator: $screenReaderStatus"
    Write-Host "High Contrast: $highContrastStatus"
    Write-Host "Report: $reportPath"
    Write-Host 'Spoken screen-reader output and visual High Contrast legibility remain pending human review.'
}
catch {
    $reportPath = Join-Path $resolvedLogDirectory 'complete-setup-accessibility-report.json'
    $failureReport = [pscustomobject]@{
        SchemaVersion = 1
        CreatedAtLocal = [DateTimeOffset]::Now.ToString('o')
        Result = 'FAIL'
        Error = $_.Exception.Message
        SessionDirectory = $sessionRoot
        Executable = $expectedExe
        ProcessId = if ($null -eq $process) { $null } else { $process.Id }
        Tests = @($results)
        UiAutomationRequiredElements = @($controlReport)
        FocusTraversal = @($focusResults)
        NarratorProcess = $screenReaderStatus
        HighContrastAutomation = $highContrastStatus
        ManualAcceptance = [pscustomobject]@{
            A11Y06NarratorSpokenOutput = 'PENDING'
            A11Y07NvdaSpokenOutput = 'PENDING'
            A11Y08HighContrastVisualLegibility = 'PENDING'
            A11Y13CompleteSetupScreenReaderAndKeyboard = 'PENDING HUMAN REVIEW'
            A11Y14CompleteSetupHighContrastVisualLegibility = 'PENDING HUMAN REVIEW'
        }
    }
    $failureReport | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    throw
}
finally {
    # Restore the original Windows High Contrast flag if this script changed it.
    if ($highContrastChangedByScript -and $null -ne $originalHighContrast) {
        try {
            $currentHighContrast = [LayalCompleteSetupAccessibilityNative]::IsHighContrastEnabled()
            if ($currentHighContrast) {
                [LayalCompleteSetupAccessibilityNative]::SetHighContrastEnabled([bool]$originalHighContrast)
                Start-Sleep -Seconds 1
            }
        }
        catch {
            Write-Warning ("High Contrast restore attempt failed: " + $_.Exception.Message)
        }
    }
}
