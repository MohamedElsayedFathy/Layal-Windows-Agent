[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Arabic', 'English')]
    [string]$ExpectedLanguage = 'Auto',

    [int]$ProcessId = 0,

    [string]$LogDirectory
)

$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Layal API accessibility diagnostics must run on Windows.'
}
if (-not [Environment]::UserInteractive) {
    throw 'A non-interactive session cannot run UI Automation diagnostics.'
}
if ($env:GITHUB_ACTIONS -eq 'true') {
    throw 'Run this diagnostic on the interactive Windows desktop, not in GitHub Actions.'
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $LogDirectory = Join-Path $repoRoot "artifacts\accessibility-api-uia\$stamp"
}
if (-not [IO.Path]::IsPathRooted($LogDirectory)) {
    $LogDirectory = Join-Path $repoRoot $LogDirectory
}
$resolvedLogDirectory = [IO.Path]::GetFullPath($LogDirectory)
New-Item -ItemType Directory -Path $resolvedLogDirectory -Force | Out-Null

if ($ProcessId -gt 0) {
    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    if ($process.ProcessName -ine 'Layal') {
        throw "ProcessId $ProcessId is '$($process.ProcessName)', not Layal."
    }
}
else {
    $matchingProcesses = @(
        Get-Process -Name 'Layal' -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero }
    )
    if ($matchingProcesses.Count -ne 1) {
        throw "Expected exactly one visible Layal main window; found $($matchingProcesses.Count). Close duplicate Layal instances or pass -ProcessId."
    }
    $process = $matchingProcesses[0]
}

if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
    throw 'The selected Layal process does not expose a main window.'
}

$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
if ($null -eq $window) {
    throw 'UI Automation could not attach to the Layal main window.'
}

$specs = @(
    @{ Id = 'ApiConnectionNameInput'; Arabic = 'اسم التطبيق / الاتصال (إجباري)'; English = 'Application / connection name (required)'; ControlType = [System.Windows.Automation.ControlType]::Edit; Password = $false; ReadOnly = $false },
    @{ Id = 'ApiConnectionBaseUrlInput'; Arabic = 'عنوان API الأساسي (إجباري)'; English = 'API base URL (required)'; ControlType = [System.Windows.Automation.ControlType]::Edit; Password = $false; ReadOnly = $false },
    @{ Id = 'ApiConnectionModelIdInput'; Arabic = 'معرّف النموذج (اختياري، مطلوب عند استخدامه كـ API ليال)'; English = 'Model ID (optional; required when used as Layal API)'; ControlType = [System.Windows.Automation.ControlType]::Edit; Password = $false; ReadOnly = $false },
    @{ Id = 'ApiConnectionImagePathInput'; Arabic = 'صورة التطبيق (اختياري)'; English = 'Application image (optional)'; ControlType = [System.Windows.Automation.ControlType]::Edit; Password = $false; ReadOnly = $true },
    @{ Id = 'ApiAuthTypeComboBox'; Arabic = 'طريقة المصادقة'; English = 'Authentication type'; ControlType = [System.Windows.Automation.ControlType]::ComboBox; Password = $false; ReadOnly = $false },
    @{ Id = 'ApiAuthHeaderNameInput'; Arabic = 'اسم ترويسة المصادقة (Custom header)'; English = 'Authentication header name (Custom header)'; ControlType = [System.Windows.Automation.ControlType]::Edit; Password = $false; ReadOnly = $false },
    @{ Id = 'ApiAuthSecretInput'; Arabic = 'Token / API key / قيمة الترويسة'; English = 'Token / API key / header value'; ControlType = [System.Windows.Automation.ControlType]::Edit; Password = $true; ReadOnly = $false },
    @{ Id = 'ApiAdditionalHeadersInput'; Arabic = 'ترويسات HTTP إضافية غير سرية (JSON)'; English = 'Additional non-secret HTTP headers (JSON)'; ControlType = [System.Windows.Automation.ControlType]::Edit; Password = $false; ReadOnly = $false },
    @{ Id = 'ApiTestPathInput'; Arabic = 'مسار اختبار الاتصال (GET)، مثل health أو users/me'; English = 'Connection test path (GET), e.g. health or users/me'; ControlType = [System.Windows.Automation.ControlType]::Edit; Password = $false; ReadOnly = $false },
    @{ Id = 'ApiInstructionsInput'; Arabic = 'التفاصيل وملاحظات API (اختياري، بدون أسرار)'; English = 'Details and API notes (optional; do not include secrets)'; ControlType = [System.Windows.Automation.ControlType]::Edit; Password = $false; ReadOnly = $false }
)

function Find-ElementByAutomationId {
    param(
        [Parameter(Mandatory = $true)]
        [System.Windows.Automation.AutomationElement]$Root,

        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-ValuePatternReadOnly {
    param([Parameter(Mandatory = $true)][System.Windows.Automation.AutomationElement]$Element)

    $valuePattern = $null
    $available = $Element.TryGetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern,
        [ref]$valuePattern)
    if (-not $available -or $null -eq $valuePattern) {
        return $null
    }
    return [bool]$valuePattern.Current.IsReadOnly
}

$firstElementMatches = Find-ElementByAutomationId -Root $window -AutomationId 'ApiConnectionNameInput'
$detectedLanguage = 'Unknown'
if ($firstElementMatches.Count -eq 1) {
    $firstName = [string]$firstElementMatches[0].Current.Name
    if ($firstName -ceq $specs[0].Arabic) {
        $detectedLanguage = 'Arabic'
    }
    elseif ($firstName -ceq $specs[0].English) {
        $detectedLanguage = 'English'
    }
}

$language = if ($ExpectedLanguage -eq 'Auto') { $detectedLanguage } else { $ExpectedLanguage }
if ($language -notin @('Arabic', 'English')) {
    throw 'Could not detect the API form language. Open the API Connections page, choose Add connection, and retry; or pass -ExpectedLanguage Arabic/English.'
}

$rows = [System.Collections.Generic.List[object]]::new()
foreach ($spec in $specs) {
    $matches = Find-ElementByAutomationId -Root $window -AutomationId $spec.Id
    $found = $matches.Count -eq 1
    $actualName = ''
    $actualControlType = ''
    $isKeyboardFocusable = $null
    $isOffscreen = $null
    $isEnabled = $null
    $isPassword = $null
    $isReadOnly = $null
    $nameMatches = $false
    $controlTypeMatches = $false
    $semanticMatches = $false
    $details = ''

    if ($found) {
        $element = $matches[0]
        try {
            $actualName = [string]$element.Current.Name
            $actualControlType = [string]$element.Current.ControlType.ProgrammaticName
            $isKeyboardFocusable = [bool]$element.Current.IsKeyboardFocusable
            $isOffscreen = [bool]$element.Current.IsOffscreen
            $isEnabled = [bool]$element.Current.IsEnabled
            $isPassword = [bool]$element.Current.IsPassword
            $isReadOnly = Get-ValuePatternReadOnly -Element $element

            $expectedName = if ($language -eq 'Arabic') { [string]$spec.Arabic } else { [string]$spec.English }
            $nameMatches = [string]::Equals($actualName, $expectedName, [StringComparison]::Ordinal)
            $controlTypeMatches = $actualControlType -eq $spec.ControlType.ProgrammaticName

            $semanticMatches = ($isKeyboardFocusable -eq $true)
            if (-not $semanticMatches) {
                $details = 'The API field is not exposed as keyboard focusable.'
            }
            if ($spec.Password) {
                $semanticMatches = $semanticMatches -and ($isPassword -eq $true)
                if ($isPassword -ne $true) { $details = 'Password field was not exposed as IsPassword=true.' }
            }
            elseif ($spec.ReadOnly) {
                $semanticMatches = $semanticMatches -and ($isReadOnly -eq $true)
                if ($isReadOnly -ne $true) { $details = 'Read-only image path was not exposed as ValuePattern.IsReadOnly=true.' }
            }
            else {
                $semanticMatches = $semanticMatches -and ($isPassword -eq $false)
            }

            if (-not $nameMatches -and [string]::IsNullOrWhiteSpace($details)) {
                $details = "Accessible name did not match the expected $language label."
            }
            if (-not $controlTypeMatches) {
                $details = if ($details) { "$details Control type mismatch." } else { 'Control type mismatch.' }
            }
        }
        catch {
            $details = "UI Automation property read failed: $($_.Exception.Message)"
        }
    }
    else {
        $details = "Expected exactly one element with AutomationId '$($spec.Id)'; found $($matches.Count). Ensure the API form is open."
    }

    $rowPass = $found -and $nameMatches -and $controlTypeMatches -and $semanticMatches
    $rows.Add([pscustomobject]@{
        AutomationId = $spec.Id
        ExpectedLanguage = $language
        ExpectedName = if ($language -eq 'Arabic') { $spec.Arabic } else { $spec.English }
        ActualName = $actualName
        ActualControlType = $actualControlType
        ExpectedControlType = $spec.ControlType.ProgrammaticName
        IsKeyboardFocusable = $isKeyboardFocusable
        IsOffscreen = $isOffscreen
        IsEnabled = $isEnabled
        IsPassword = $isPassword
        ValuePatternIsReadOnly = $isReadOnly
        NameMatches = $nameMatches
        ControlTypeMatches = $controlTypeMatches
        SemanticMatches = $semanticMatches
        Result = if ($rowPass) { 'PASS' } else { 'FAIL' }
        Details = $details
    })
}

$failedRows = @($rows | Where-Object { $_.Result -ne 'PASS' })
$diagnosticResult = if ($failedRows.Count -eq 0) { 'PASS' } else { 'FAIL' }
$screenReaderProcesses = @(
    Get-Process -Name 'Narrator', 'nvda' -ErrorAction SilentlyContinue |
        Select-Object ProcessName, Id
)

$reportPath = Join-Path $resolvedLogDirectory 'api-screenreader-uia-report.json'
$csvPath = Join-Path $resolvedLogDirectory 'api-screenreader-uia-fields.csv'
$rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8

$report = [pscustomobject]@{
    SchemaVersion = 1
    CreatedAtLocal = [DateTimeOffset]::Now.ToString('o')
    DiagnosticScope = 'UI Automation element metadata only; no spoken output is captured or evaluated.'
    Result = $diagnosticResult
    ExpectedLanguage = $language
    DetectedLanguage = $detectedLanguage
    LayalProcessId = $process.Id
    LayalWindowTitle = [string]$window.Current.Name
    FieldsExpected = $specs.Count
    FieldsFound = @($rows | Where-Object { $_.Result -eq 'PASS' }).Count
    FailedFields = $failedRows.Count
    ScreenReaderProcessesDetected = @($screenReaderProcesses)
    NarratorSpokenOutput = 'NOT TESTED: a person must listen to the actual announcement.'
    NvdaSpokenOutput = 'NOT TESTED: a person must listen to the actual announcement.'
    KeyboardTraversal = 'NOT TESTED: use Tab and Shift+Tab while listening; this script does not send keystrokes.'
    SecretValuesRead = $false
    SecretValuesIncludedInReport = $false
    Fields = @($rows)
    Evidence = [pscustomobject]@{
        ReportJson = $reportPath
        FieldsCsv = $csvPath
    }
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ''
Write-Host "Layal API UI Automation diagnostic: $diagnosticResult"
Write-Host "Language: $language"
Write-Host "Fields passed: $($specs.Count - $failedRows.Count)/$($specs.Count)"
Write-Host "Narrator process detected: $(@($screenReaderProcesses | Where-Object ProcessName -eq 'Narrator').Count -gt 0)"
Write-Host "NVDA process detected: $(@($screenReaderProcesses | Where-Object ProcessName -eq 'nvda').Count -gt 0)"
Write-Host "Report: $reportPath"
Write-Host "Fields CSV: $csvPath"
Write-Host 'IMPORTANT: UI Automation metadata PASS is not screen-reader speech acceptance. Narrator/NVDA spoken output remains NOT TESTED until a human listens.'

if ($diagnosticResult -ne 'PASS') {
    exit 1
}
