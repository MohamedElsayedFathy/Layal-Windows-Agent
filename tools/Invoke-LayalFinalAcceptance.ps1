[CmdletBinding()]
param(
    [switch]$RunDesktopIntegration,
    [switch]$RunInstallerLifecycle,
    [switch]$ConfirmDisposableProfile,
    [string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Artifacts = Join-Path $Root 'artifacts'
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $Artifacts "final-acceptance/$Stamp"
}
if (-not [IO.Path]::IsPathRooted($ReportDirectory)) {
    $ReportDirectory = Join-Path $Root $ReportDirectory
}
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null

$script:Checks = [System.Collections.Generic.List[object]]::new()
$script:RequirementRows = @()
$script:AyoRows = @()
$script:StartTime = [DateTimeOffset]::Now
$script:Python = $null
$script:Dotnet = $null
$script:PowerShell = $null
$script:ReleaseExe = $null
$script:TrxDirectory = Join-Path $Artifacts 'final-acceptance-test-results'
$script:CommitSha = 'unavailable'
$script:DotnetSdk = 'unavailable'
$script:PythonVersion = 'unavailable'
$previousInstallerOptIn = $env:LAYAL_ALLOW_INSTALLER_TESTS

# These maps connect each documented requirement/feature to actual automated test assets.
# Presence of a test file is evidence of test coverage intent, never proof that the complete
# acceptance criterion passed; manual and external gates remain explicit in the report.
$script:RequirementSuiteMap = @{
    'LAYAL-001' = @('tests/Layal.Tests/LayalIdentityTests.cs')
    'LAYAL-002' = @('tests/Layal.Tests/MainWindowVisualSmokeTests.cs', 'tests/Layal.Tests/MascotHitTestGeometryTests.cs')
    'LAYAL-003' = @('tests/Layal.Tests/MascotHitTestGeometryTests.cs', 'tools/Test-LayalClickThrough.Native.ps1', 'tools/Test-LayalDisplayDpi.ps1')
    'LAYAL-004' = @('tests/Layal.Tests/MainWindowVisualSmokeTests.cs', 'tests/Layal.Tests/NotificationActionSlotTests.cs')
    'LAYAL-005' = @('tests/Layal.Tests/CharacterExpressionMapTests.cs', 'tests/Layal.Tests/CharacterDisplayModePolicyTests.cs', 'tests/Layal.Tests/MascotEffectStyleResolverTests.cs', 'tests/Layal.Tests/MascotPerformancePolicyTests.cs')
    'LAYAL-006' = @('tests/Layal.Tests/UiLocalizationTests.cs', 'tests/Layal.Tests/BidiTextNormalizerTests.cs', 'tests/Layal.Tests/TextDirectionConverterTests.cs')
    'LAYAL-007' = @('tests/Layal.Tests/SpeechToTextServiceTests.cs', 'tests/Layal.Tests/LocalVoiceIntegrityTests.cs', 'tests/Layal.Tests/LocalVoiceLanguageSegmentationTests.cs')
    'LAYAL-008' = @('tests/Layal.Tests/VoiceConversationServiceTests.cs', 'tests/Layal.Tests/VoiceDeliveryStylePolicyTests.cs', 'tests/Layal.Tests/LayalSpeechServiceTests.cs', 'tests/Layal.Tests/AudioDeviceServiceTests.cs', 'tests/Layal.Tests/AudioDeviceSettingsTests.cs')
    'LAYAL-009' = @('tests/Layal.Tests/PrivacyShieldPolicyTests.cs', 'tests/Layal.Tests/VoiceConversationServiceTests.cs', 'tests/Layal.Tests/AudioDeviceServiceTests.cs')
    'LAYAL-010' = @('tests/Layal.Tests/OpenAiCompatibleClientTests.cs', 'tests/Layal.Tests/LocalAiServiceTests.cs')
    'LAYAL-011' = @('tests/Layal.Tests/OpenAiCompatibleClientTests.cs', 'tests/Layal.Tests/LocalAiServiceTests.cs')
    'LAYAL-012' = @('tests/Layal.Tests/AgentOrchestratorTests.cs', 'tests/Layal.Tests/LocalAiServiceTests.cs')
    'LAYAL-013' = @('tests/Layal.Tests/AgentOrchestratorTests.cs', 'tests/Layal.Tests/TaskHistoryArtifactTests.cs')
    'LAYAL-014' = @('tests/Layal.Tests/TaskApprovalWindowTests.cs', 'tests/Layal.Tests/ApiToolApprovalTests.cs', 'tests/Layal.Tests/AgentToolExecutorTests.cs')
    'LAYAL-015' = @('tests/Layal.Tests/AgentToolExecutorTests.cs', 'tests/Layal.Tests/AgentToolExecutorEditFidelityTests.cs', 'tests/Layal.Tests/WorkspaceArtifactServiceTests.cs', 'tests/Layal.Tests/WorkspaceSnapshotServiceTests.cs')
    'LAYAL-016' = @('tests/Layal.Tests/AgentToolExecutorTests.cs', 'tests/Layal.Tests/AgentOrchestratorTests.cs')
    'LAYAL-017' = @('tests/Layal.Tests/WindowsUiAutomationServiceTests.cs')
    'LAYAL-018' = @('tests/Layal.Tests/BrowserAutomationServiceTests.cs', 'tests/Layal.Tests/BrowserAutomationShutdownTests.cs')
    'LAYAL-019' = @('tests/Layal.Tests/SafeBrowserDownloadServiceTests.cs', 'tests/Layal.Tests/SafeBrowserUploadServiceTests.cs')
    'LAYAL-020' = @('tests/Layal.Tests/SkillPackageServiceTests.cs', 'tests/Layal.Tests/SkillPackageServiceBoundedCopyTests.cs')
    'LAYAL-021' = @()
    'LAYAL-022' = @()
    'LAYAL-023' = @('tests/Layal.Tests/MemoryServiceTests.cs', 'tests/Layal.Tests/MemoryNoteReviewWindowTests.cs')
    'LAYAL-024' = @('tests/Layal.Tests/DpapiSecretStoreTests.cs', 'tests/Layal.Tests/ProtectedJsonStoreTests.cs')
    'LAYAL-025' = @('tests/Layal.Tests/OnvifCameraDiscoveryServiceTests.cs', 'tests/Layal.Tests/PrivacyShieldPolicyTests.cs')
    'LAYAL-026' = @('tests/Layal.Tests/ScreenRegionCaptureServiceTests.cs', 'tests/Layal.Tests/ClipboardImageAttachmentEncoderTests.cs', 'tests/Layal.Tests/OpenAiCompatibleClientTests.cs')
    'LAYAL-027' = @()
    'LAYAL-028' = @()
    'LAYAL-029' = @('tests/Layal.Tests/ProtectedJsonStoreTests.cs', 'tests/Layal.Tests/WorkspaceSnapshotServiceTests.cs', 'tests/Layal.Tests/WorkspaceArtifactServiceTests.cs')
    'LAYAL-030' = @('tests/Layal.Tests/PerformanceMetricsServiceTests.cs', 'tests/Layal.Tests/TaskHistoryArtifactTests.cs')
    'LAYAL-031' = @('tests/Layal.Tests/PerformanceMetricsServiceTests.cs', 'tests/Layal.Tests/MascotPerformancePolicyTests.cs')
    'LAYAL-032' = @('tools/Test-LayalInstaller.ps1', 'tools/Verify-LayalDistributionIntegrity.ps1')
    'LAYAL-033' = @('tests/Layal.Tests/Layal.Tests.csproj', 'tools/validate_test_results.py')
    'LAYAL-034' = @('tools/VERIFY-LAYAL.ps1', 'tools/Build-LayalDistribution.ps1')
    'LAYAL-035' = @('tools/validate_licenses.py', 'tools/audit_nuget.py')
    'LAYAL-036' = @('tests/Layal.Tests/AgentToolExecutorTests.cs', 'tests/Layal.Tests/ApiToolApprovalTests.cs', 'tests/Layal.Tests/SkillPackageServiceTests.cs')
    'LAYAL-037' = @('tests/Layal.Tests/MainWindowVisualSmokeTests.cs', 'tests/Layal.Tests/UiLocalizationTests.cs', 'tools/Test-LayalKeyboardNavigation.ps1')
    'LAYAL-038' = @('tests/Layal.Tests/ApiConnectionServiceTests.cs', 'tests/Layal.Tests/ApiToolApprovalTests.cs')
}
$script:AyoSuiteMap = @{
    'AYO-01' = @('tests/Layal.Tests/SpeechToTextServiceTests.cs', 'tests/Layal.Tests/VoiceConversationServiceTests.cs', 'tests/Layal.Tests/AudioDeviceServiceTests.cs', 'tests/Layal.Tests/LocalVoiceLanguageSegmentationTests.cs')
    'AYO-02' = @('tests/Layal.Tests/QuickAskWindowTests.cs', 'tests/Layal.Tests/QuickAskRequestPolicyTests.cs')
    'AYO-03' = @('tests/Layal.Tests/GlobalHotkeyServiceTests.cs', 'tests/Layal.Tests/QuickAskWindowTests.cs')
    'AYO-04' = @('tests/Layal.Tests/AgentOrchestratorTests.cs', 'tests/Layal.Tests/TaskApprovalWindowTests.cs')
    'AYO-05' = @('tests/Layal.Tests/ScreenRegionCaptureServiceTests.cs', 'tests/Layal.Tests/PrivacyShieldPolicyTests.cs')
    'AYO-06' = @('tests/Layal.Tests/ScreenRegionCaptureServiceTests.cs', 'tests/Layal.Tests/MainWindowVisualSmokeTests.cs')
    'AYO-07' = @('tests/Layal.Tests/WindowsUiAutomationServiceTests.cs')
    'AYO-08' = @('tests/Layal.Tests/DocumentTextExtractionServiceTests.cs')
    'AYO-09' = @('tests/Layal.Tests/DocumentTextExtractionServiceTests.cs')
    'AYO-10' = @('tests/Layal.Tests/BrowserAutomationServiceTests.cs', 'tests/Layal.Tests/OpenAiCompatibleClientTests.cs')
    'AYO-11' = @('tests/Layal.Tests/BrowserAutomationServiceTests.cs', 'tests/Layal.Tests/BrowserAutomationShutdownTests.cs')
    'AYO-12' = @('tests/Layal.Tests/AgentOrchestratorTests.cs', 'tests/Layal.Tests/TaskHistoryArtifactTests.cs')
    'AYO-13' = @('tests/Layal.Tests/AgentToolExecutorTests.cs', 'tests/Layal.Tests/AgentToolExecutorEditFidelityTests.cs', 'tests/Layal.Tests/WorkspaceArtifactServiceTests.cs')
    'AYO-14' = @('tests/Layal.Tests/AgentToolExecutorTests.cs')
    'AYO-15' = @('tests/Layal.Tests/BrowserAutomationServiceTests.cs', 'tests/Layal.Tests/SafeBrowserDownloadServiceTests.cs', 'tests/Layal.Tests/SafeBrowserUploadServiceTests.cs')
    'AYO-16' = @('tests/Layal.Tests/TaskApprovalWindowTests.cs', 'tests/Layal.Tests/ApiToolApprovalTests.cs')
    'AYO-17' = @('tests/Layal.Tests/MemoryServiceTests.cs', 'tests/Layal.Tests/DpapiSecretStoreTests.cs')
    'AYO-18' = @('tests/Layal.Tests/MemoryServiceTests.cs', 'tests/Layal.Tests/MemoryNoteReviewWindowTests.cs')
    'AYO-19' = @('tests/Layal.Tests/LoreWebWindowTests.cs', 'tests/Layal.Tests/GltfSkinnedAnimationRendererTests.cs')
    'AYO-20' = @('tests/Layal.Tests/WorkspaceSnapshotServiceTests.cs', 'tests/Layal.Tests/WorkspaceArtifactServiceTests.cs')
    'AYO-21' = @('tests/Layal.Tests/ReminderServiceTests.cs', 'tests/Layal.Tests/ReminderTaskSchedulerServiceTests.cs', 'tests/Layal.Tests/ReminderNotificationQueueTests.cs')
    'AYO-22' = @('tests/Layal.Tests/SavedRoutineServiceTests.cs', 'tests/Layal.Tests/RoutineToolOutcomePolicyTests.cs')
    'AYO-23' = @('tests/Layal.Tests/ProactiveCheckInPolicyTests.cs', 'tests/Layal.Tests/ReminderTaskSchedulerServiceTests.cs')
    'AYO-24' = @('tests/Layal.Tests/ReminderTaskLinkPolicyTests.cs', 'tests/Layal.Tests/ReminderServiceTests.cs')
    'AYO-25' = @('tests/Layal.Tests/GmailReadOnlyServiceTests.cs', 'tests/Layal.Tests/DocumentTextExtractionServiceTests.cs')
    'AYO-26' = @('tests/Layal.Tests/WeatherServiceTests.cs')
    'AYO-27' = @('tests/Layal.Tests/GameModeHudWindowTests.cs', 'tests/Layal.Tests/GameModeAutoDismissalPolicyTests.cs', 'tests/Layal.Tests/GameModeProcessAllowlistTests.cs')
    'AYO-28' = @('tests/Layal.Tests/CharacterDisplayModePolicyTests.cs', 'tests/Layal.Tests/VoiceDeliveryStylePolicyTests.cs', 'tests/Layal.Tests/CharacterExpressionMapTests.cs')
    'AYO-29' = @('tests/Layal.Tests/UiThemePaletteTests.cs', 'tests/Layal.Tests/DesignTokensTests.cs')
    'AYO-30' = @('tests/Layal.Tests/MascotPerformancePolicyTests.cs', 'tests/Layal.Tests/PerformanceMetricsServiceTests.cs')
    'AYO-31' = @('tests/Layal.Tests/ClipboardReadPolicyTests.cs', 'tests/Layal.Tests/ClipboardImageAttachmentEncoderTests.cs')
    'AYO-32' = @('tests/Layal.Tests/ChatSessionServiceTests.cs', 'tests/Layal.Tests/PrivateChatModePolicyTests.cs')
    'AYO-33' = @('tests/Layal.Tests/PrivacyShieldPolicyTests.cs', 'tests/Layal.Tests/VoiceConversationServiceTests.cs')
}

function Resolve-AutomatedEvidence {
    param([string[]]$Paths)
    $found = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in $Paths) {
        if ([string]::IsNullOrWhiteSpace($relativePath)) { continue }
        $fullPath = Join-Path $Root $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Coverage map points to a missing test/evidence file: $relativePath"
        }
        $found.Add($relativePath) | Out-Null
    }
    return @($found)
}

function Add-CheckResult {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Category,
        [Parameter(Mandatory = $true)][ValidateSet('PASS', 'FAIL', 'SKIPPED', 'BLOCKED')][string]$Status,
        [string]$Details = '',
        [double]$DurationSeconds = 0
    )
    $script:Checks.Add([pscustomobject]@{
        Name = $Name
        Category = $Category
        Status = $Status
        DurationSeconds = [Math]::Round($DurationSeconds, 2)
        Details = $Details
    }) | Out-Null
    $color = switch ($Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'BLOCKED' { 'Yellow' }
        default { 'DarkYellow' }
    }
    Write-Host "[$Status] $Name — $Details" -ForegroundColor $color
}

function Invoke-Check {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Category,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        $detail = & $Action
        if ($detail -is [bool] -and -not $detail) {
            throw 'The acceptance action returned Boolean false.'
        }
        if ($null -eq $detail) { $detail = 'Completed successfully.' }
        Add-CheckResult -Name $Name -Category $Category -Status 'PASS' -Details ([string]$detail) -DurationSeconds $timer.Elapsed.TotalSeconds
        return $true
    }
    catch {
        Add-CheckResult -Name $Name -Category $Category -Status 'FAIL' -Details $_.Exception.Message -DurationSeconds $timer.Elapsed.TotalSeconds
        return $false
    }
}

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )
    Write-Host "--- $Description ---" -ForegroundColor Cyan
    $output = & $Executable @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    foreach ($line in $output) { Write-Host ([string]$line) }
    if ($exitCode -ne 0) { throw "$Description returned exit code $exitCode." }
    return "$Description passed."
}

function Invoke-PowerShellChild {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$LogDirectory
    )
    $resolvedScript = (Resolve-Path -LiteralPath $ScriptPath).Path
    New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
    $stdoutPath = Join-Path $LogDirectory 'stdout.log'
    $stderrPath = Join-Path $LogDirectory 'stderr.log'

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:PowerShell.Source
    $startInfo.WorkingDirectory = $Root
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in (@('-NoLogo', '-NoProfile', '-File', $resolvedScript) + $Arguments)) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Could not start $Name." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($stdoutPath, $stdout, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($stderrPath, $stderr, [Text.UTF8Encoding]::new($false))
        if (-not [string]::IsNullOrWhiteSpace($stdout)) { Write-Host $stdout }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) { Write-Host $stderr }
        if ($process.ExitCode -ne 0) {
            throw "$Name failed with exit code $($process.ExitCode). See $stderrPath and $stdoutPath."
        }
    }
    finally {
        $process.Dispose()
    }
    return [pscustomobject]@{ StdoutPath = $stdoutPath; StderrPath = $stderrPath; ExitCode = 0 }
}

function Invoke-DesktopDiagnostic {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ScriptName,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$SummaryRelativePath,
        [Parameter(Mandatory = $true)][scriptblock]$ValidateSummary
    )
    $runDirectory = Join-Path $ReportDirectory $Name
    $scriptPath = Join-Path $Root "tools/$ScriptName"
    $child = Invoke-PowerShellChild -Name $Name -ScriptPath $scriptPath -Arguments $Arguments -LogDirectory $runDirectory
    $summaryPath = Join-Path $runDirectory $SummaryRelativePath
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        throw "$Name did not create its expected summary: $summaryPath"
    }
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    & $ValidateSummary $summary
    return "Summary: $summaryPath; stdout: $($child.StdoutPath)."
}

function Get-RequirementInventories {
    $traceabilityPath = Join-Path $Root 'docs/REQUIREMENT_TRACEABILITY.csv'
    $parityPath = Join-Path $Root 'docs/AYO_FEATURE_PARITY_AR.md'
    if (-not (Test-Path -LiteralPath $traceabilityPath -PathType Leaf)) { throw "Requirement CSV missing: $traceabilityPath" }
    if (-not (Test-Path -LiteralPath $parityPath -PathType Leaf)) { throw "AYO parity register missing: $parityPath" }

    $requirements = @(Import-Csv -LiteralPath $traceabilityPath -Encoding UTF8)
    if ($requirements.Count -eq 0) { throw 'Requirement traceability CSV contains no rows.' }
    $seen = @{}
    foreach ($row in $requirements) {
        $id = ([string]$row.id).Trim()
        if ($id -notmatch '^LAYAL-\d{3}$') { throw "Invalid requirement ID: '$id'." }
        if ($seen.ContainsKey($id)) { throw "Duplicate requirement ID: $id." }
        $seen[$id] = $true
    }

    $parityText = Get-Content -LiteralPath $parityPath -Raw -Encoding UTF8
    $ayoRows = [System.Collections.Generic.List[object]]::new()
    foreach ($line in ($parityText -split "\r?\n")) {
        if ($line -match '^\|\s*(AYO-\d{2})\s*\|') {
            $cells = @($line.Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() })
            if ($cells.Count -lt 4) { throw "Malformed AYO parity row: $($cells[0])" }
            $ayoRows.Add([pscustomobject]@{
                Id = $cells[0]
                Feature = $cells[1]
                SourceStatus = $cells[2]
                AcceptanceCriteria = $cells[3]
            }) | Out-Null
        }
    }
    if ($ayoRows.Count -ne 33) { throw "Expected 33 AYO rows; found $($ayoRows.Count)." }
    for ($index = 1; $index -le 33; $index++) {
        $expectedId = 'AYO-{0:D2}' -f $index
        if ($ayoRows[$index - 1].Id -ne $expectedId) {
            throw "AYO order/coverage error at row $index; expected $expectedId."
        }
    }

    $script:RequirementRows = @($requirements | ForEach-Object {
        $id = [string]$_.id
        $evidenceFiles = @(Resolve-AutomatedEvidence -Paths $script:RequirementSuiteMap[$id])
        [pscustomobject]@{
            Id = $id
            Area = [string]$_.area
            Requirement = [string]$_.requirement
            SourceStatus = [string]$_.status_0_5_2
            AcceptanceEvidence = [string]$_.acceptance_evidence
            Priority = [string]$_.priority
            AutomatedTestSuites = ($evidenceFiles -join '; ')
            AutomatedCoverageStatus = if ($evidenceFiles.Count -gt 0) { 'TEST ASSETS PRESENT — criterion-level acceptance remains pending' } else { 'NO DIRECT AUTOMATED TEST ASSET — acceptance remains pending' }
            RequirementDecision = 'PENDING — test asset presence is not criterion-level acceptance'
        }
    })
    $script:AyoRows = @($ayoRows | ForEach-Object {
        $id = [string]$_.Id
        $evidenceFiles = @(Resolve-AutomatedEvidence -Paths $script:AyoSuiteMap[$id])
        [pscustomobject]@{
            Id = $id
            Feature = $_.Feature
            SourceStatus = $_.SourceStatus
            AcceptanceCriteria = $_.AcceptanceCriteria
            AutomatedTestSuites = ($evidenceFiles -join '; ')
            AutomatedCoverageStatus = if ($evidenceFiles.Count -gt 0) { 'TEST ASSETS PRESENT — criterion-level acceptance remains pending' } else { 'NO DIRECT AUTOMATED TEST ASSET — acceptance remains pending' }
            RequirementDecision = 'PENDING — test asset presence is not criterion-level acceptance'
        }
    })
    return "Loaded $($requirements.Count) LAYAL requirements and $($ayoRows.Count) AYO features."
}

function ConvertTo-MarkdownCell {
    param([AllowNull()][string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return ($Value -replace '\|', '\|' -replace '\r?\n', ' ')
}

function Write-AcceptanceReports {
    $finishedAt = [DateTimeOffset]::Now
    $failed = @($script:Checks | Where-Object Status -eq 'FAIL')
    $blocked = @($script:Checks | Where-Object Status -eq 'BLOCKED')
    $skipped = @($script:Checks | Where-Object Status -eq 'SKIPPED')
    $passed = @($script:Checks | Where-Object Status -eq 'PASS')
    $automatedStatus = if ($failed.Count -gt 0) { 'FAIL' } elseif ($blocked.Count -gt 0) { 'BLOCKED' } elseif ($skipped.Count -gt 0) { 'PARTIAL — acceptance stages were skipped' } elseif ($passed.Count -gt 0) { 'PASS' } else { 'INCOMPLETE' }
    $manualGates = @(
        [pscustomobject]@{ Gate = 'الميكروفون والتعرف على الكلام والرد الصوتي'; Status = 'PENDING'; Reason = 'يتطلب أجهزة فعلية وعينات صوتية مرجعية وقياسًا بشريًا للجودة والتأخير.' }
        [pscustomobject]@{ Gate = 'الكاميرا وONVIF وRTSP/RTSPS'; Status = 'PENDING'; Reason = 'يتطلب كاميرا أو جهاز بث وشبكة محلية وبيانات اعتماد تجريبية وموافقة المستخدم.' }
        [pscustomobject]@{ Gate = 'مزودات AI وOllama وواجهات API الخارجية'; Status = 'PENDING'; Reason = 'يتطلب خدمات ومفاتيح اختبار أو حسابات فعلية؛ لا تستخدم أسرار حقيقية في CI.' }
        [pscustomobject]@{ Gate = 'Gmail وOAuth والتكاملات المرتبطة بالحسابات'; Status = 'PENDING'; Reason = 'يتطلب حسابًا تجريبيًا وتفويض OAuth حقيقيًا والتحقق من الصلاحيات.' }
        [pscustomobject]@{ Gate = 'Narrator/NVDA والتباين العالي وRTL/LTR البصري'; Status = 'PENDING'; Reason = 'يتطلب مراجعة بشرية على Windows؛ لا تثبت UI Automation وحدها تجربة الوصول كاملة.' }
        [pscustomobject]@{ Gate = 'تعدد الشاشات وانتقالات DPI الفعلية'; Status = 'PENDING'; Reason = 'يتطلب شاشتين فعليتين على الأقل بمقياسي تحجيم مختلفين واختبار سحب.' }
        [pscustomobject]@{ Gate = 'الأداء والذاكرة والاستقرار طويل المدة'; Status = 'PENDING'; Reason = 'يتطلب قياسًا على مواصفات جهاز موثقة وتحت حمل استخدام واقعي.' }
        [pscustomobject]@{ Gate = 'الآثار الواقعية لكل أدوات الوكيل'; Status = 'PENDING'; Reason = 'يتطلب سيناريو لكل نوع أداة مع قبول/رفض الموافقات والتحقق من الأثر.' }
    )
    $decision = if ($failed.Count -gt 0) {
        'FAIL — one or more automated acceptance checks failed'
    } elseif ($blocked.Count -gt 0) {
        'BLOCKED — one or more requested acceptance stages could not run'
    } elseif ($skipped.Count -gt 0) {
        'PARTIAL — one or more acceptance stages were intentionally skipped; rerun with the full-acceptance switches'
    } else {
        'AUTOMATED PASS — FINAL ACCEPTANCE PENDING MANUAL / EXTERNAL GATES'
    }

    $report = [pscustomobject]@{
        SchemaVersion = 1
        Project = 'Layal Windows Agent'
        StartedAtLocal = $script:StartTime.ToString('o')
        CompletedAtLocal = $finishedAt.ToString('o')
        CommitSha = $script:CommitSha
        Environment = [pscustomobject]@{
            OS = [Environment]::OSVersion.VersionString
            PowerShell = [string]$PSVersionTable.PSVersion
            DotnetSdk = $script:DotnetSdk
            Python = $script:PythonVersion
            IsGitHubActions = ($env:GITHUB_ACTIONS -eq 'true')
            InteractiveSession = [bool][Environment]::UserInteractive
        }
        Decision = $decision
        AutomatedStatus = $automatedStatus
        Counts = [pscustomobject]@{
            Passed = $passed.Count
            Failed = $failed.Count
            Skipped = $skipped.Count
            Blocked = $blocked.Count
            LayalRequirements = $script:RequirementRows.Count
            AyoFeatures = $script:AyoRows.Count
            ManualGatesPending = $manualGates.Count
        }
        Checks = @($script:Checks)
        RequirementTraceability = @($script:RequirementRows)
        AyoFeatureParity = @($script:AyoRows)
        ManualGates = $manualGates
        Limitations = @(
            'A passing aggregate suite does not automatically mark every individual requirement or AYO feature as accepted.',
            'Each requirement stays pending until its acceptance criterion is connected to dedicated passing evidence.',
            'Provider credentials, physical devices, screen-reader review, mixed-DPI checks, and long-duration performance tests are not silently marked as passed.'
        )
    }

    $jsonPath = Join-Path $ReportDirectory 'final-acceptance-report.json'
    $markdownPath = Join-Path $ReportDirectory 'final-acceptance-report.md'
    $requirementsCsv = Join-Path $ReportDirectory 'requirement-acceptance-matrix.csv'
    $ayoCsv = Join-Path $ReportDirectory 'ayo-feature-acceptance-matrix.csv'
    [IO.File]::WriteAllText($jsonPath, (ConvertTo-Json -InputObject $report -Depth 12), [Text.UTF8Encoding]::new($false))
    $script:RequirementRows | Export-Csv -LiteralPath $requirementsCsv -NoTypeInformation -Encoding UTF8
    $script:AyoRows | Export-Csv -LiteralPath $ayoCsv -NoTypeInformation -Encoding UTF8

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('# تقرير القبول النهائي الآلي — Layal')
    $lines.Add('')
    $lines.Add("- **القرار:** $decision")
    $lines.Add("- **الحالة الآلية:** $automatedStatus")
    $lines.Add("- **Commit:** $($script:CommitSha)")
    $lines.Add("- **بدء التشغيل:** $($report.StartedAtLocal)")
    $lines.Add("- **انتهاء التشغيل:** $($report.CompletedAtLocal)")
    $lines.Add("- **PowerShell:** $($report.Environment.PowerShell)")
    $lines.Add("- **.NET SDK:** $($report.Environment.DotnetSdk)")
    $lines.Add("- **Python:** $($report.Environment.Python)")
    $lines.Add('')
    $lines.Add('## ملخص التنفيذ')
    $lines.Add('')
    $lines.Add('| النتيجة | العدد |')
    $lines.Add('|---|---:|')
    $lines.Add("| اجتاز | $($passed.Count) |")
    $lines.Add("| فشل | $($failed.Count) |")
    $lines.Add("| لم يُشغّل | $($skipped.Count) |")
    $lines.Add("| محجوب | $($blocked.Count) |")
    $lines.Add("| متطلبات LAYAL المفهرسة | $($script:RequirementRows.Count) |")
    $lines.Add("| وظائف AYO المفهرسة | $($script:AyoRows.Count) |")
    $lines.Add('')
    $lines.Add('## نتائج الفحوص')
    $lines.Add('')
    $lines.Add('| الفحص | الفئة | النتيجة | الزمن (ث) | التفاصيل |')
    $lines.Add('|---|---|---:|---:|---|')
    foreach ($check in $script:Checks) {
        $lines.Add("| $(ConvertTo-MarkdownCell $check.Name) | $(ConvertTo-MarkdownCell $check.Category) | $($check.Status) | $($check.DurationSeconds) | $(ConvertTo-MarkdownCell $check.Details) |")
    }
    $lines.Add('')
    $lines.Add('## بوابات القبول غير الآلية')
    $lines.Add('')
    $lines.Add('| البوابة | الحالة | سبب الحاجة إلى قبول فعلي |')
    $lines.Add('|---|---|---|')
    foreach ($gate in $manualGates) {
        $lines.Add("| $(ConvertTo-MarkdownCell $gate.Gate) | $($gate.Status) | $(ConvertTo-MarkdownCell $gate.Reason) |")
    }
    $lines.Add('')
    $lines.Add('## مصفوفة المتطلبات')
    $lines.Add('')
    $lines.Add('نجاح الاختبارات العامة لا يحوّل المتطلب المنفرد إلى مقبول دون دليل يطابق معيار قبوله.')
    $lines.Add('')
    $lines.Add('| المعرّف | المجال | المتطلب | حالة المصدر | ملفات الاختبار المرتبطة | حالة دليل الاختبار | قرار القبول |')
    $lines.Add('|---|---|---|---|---|---|---|')
    foreach ($row in $script:RequirementRows) {
        $lines.Add("| $($row.Id) | $(ConvertTo-MarkdownCell $row.Area) | $(ConvertTo-MarkdownCell $row.Requirement) | $(ConvertTo-MarkdownCell $row.SourceStatus) | $(ConvertTo-MarkdownCell $row.AutomatedTestSuites) | $(ConvertTo-MarkdownCell $row.AutomatedCoverageStatus) | PENDING |")
    }
    $lines.Add('')
    $lines.Add('## وظائف AYO الـ33')
    $lines.Add('')
    $lines.Add('| المعرّف | الوظيفة | الحالة المسجلة | ملفات الاختبار المرتبطة | حالة دليل الاختبار | قرار القبول |')
    $lines.Add('|---|---|---|---|---|---|')
    foreach ($row in $script:AyoRows) {
        $lines.Add("| $($row.Id) | $(ConvertTo-MarkdownCell $row.Feature) | $(ConvertTo-MarkdownCell $row.SourceStatus) | $(ConvertTo-MarkdownCell $row.AutomatedTestSuites) | $(ConvertTo-MarkdownCell $row.AutomatedCoverageStatus) | PENDING |")
    }
    $lines.Add('')
    $lines.Add('## ملفات الدليل')
    $lines.Add('')
    $lines.Add('- final-acceptance-report.json: التقرير الكامل وبيئة التشغيل.')
    $lines.Add('- requirement-acceptance-matrix.csv: سجل LAYAL-### القابل للفرز.')
    $lines.Add('- ayo-feature-acceptance-matrix.csv: سجل AYO-## القابل للفرز.')
    $lines.Add('- المجلدات الفرعية: سجلات الأوامر وملفات JSON/CSV الناتجة.')
    $lines.Add('')
    $lines.Add('**تنبيه اعتماد:** نجاح التشغيل الآلي لا يمثل قرار قبول نهائيًا أو تصريح إصدار قبل إغلاق بوابات الأجهزة والخدمات والمراجعة البشرية وتسجيل أدلتها.')
    [IO.File]::WriteAllLines($markdownPath, $lines, [Text.UTF8Encoding]::new($false))

    Write-Host ''
    Write-Host "Automated acceptance: $automatedStatus" -ForegroundColor $(if ($automatedStatus -eq 'PASS') { 'Green' } else { 'Red' })
    Write-Host "Final decision: $decision" -ForegroundColor Yellow
    Write-Host "JSON: $jsonPath"
    Write-Host "Markdown: $markdownPath"
    Write-Host "Requirements matrix: $requirementsCsv"
    Write-Host "AYO matrix: $ayoCsv"
    return [pscustomobject]@{ Decision = $decision; AutomatedStatus = $automatedStatus; FailedCount = $failed.Count; BlockedCount = $blocked.Count }
}

try {
    Set-Location $Root
    Write-Host '=== Layal PowerShell final acceptance ===' -ForegroundColor Cyan
    Write-Host "Project root: $Root"
    Write-Host "Report directory: $ReportDirectory"
    Write-Host "Started: $($script:StartTime.ToString('o'))"

    $script:PowerShell = Get-Command 'pwsh' -ErrorAction SilentlyContinue
    if ($null -eq $script:PowerShell) { $script:PowerShell = Get-Command 'powershell' -ErrorAction SilentlyContinue }
    $script:Dotnet = Get-Command 'dotnet' -ErrorAction SilentlyContinue
    $script:Python = Get-Command 'python' -ErrorAction SilentlyContinue
    if ($null -eq $script:Python) { $script:Python = Get-Command 'python3' -ErrorAction SilentlyContinue }

    $preflight = Invoke-Check -Name 'Windows 10/11 environment' -Category 'Environment' -Action {
        if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This final acceptance runner is Windows-only.' }
        return [Environment]::OSVersion.VersionString
    }
    $preflight = (Invoke-Check -Name 'PowerShell 7 runtime' -Category 'Environment' -Action {
        if ($null -eq $script:PowerShell) { throw 'PowerShell executable was not found.' }
        if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
        return "PowerShell $($PSVersionTable.PSVersion)."
    }) -and $preflight
    $preflight = (Invoke-Check -Name '.NET 10 SDK' -Category 'Environment' -Action {
        if ($null -eq $script:Dotnet) { throw '.NET SDK was not found.' }
        $sdkOutput = & $script:Dotnet.Source --list-sdks 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'dotnet --list-sdks failed.' }
        $sdkText = $sdkOutput -join [Environment]::NewLine
        if ($sdkText -notmatch '(^|\s)10\.') { throw '.NET 10 SDK is required.' }
        $sdkLine = $sdkOutput | Where-Object { $_ -match '^\s*10\.' } | Select-Object -Last 1
        $script:DotnetSdk = ([string]$sdkLine).Trim()
        return $script:DotnetSdk
    }) -and $preflight
    $preflight = (Invoke-Check -Name 'Python 3 runtime' -Category 'Environment' -Action {
        if ($null -eq $script:Python) { throw 'Python 3 executable was not found.' }
        $versionOutput = & $script:Python.Source --version 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'python --version failed.' }
        $script:PythonVersion = ($versionOutput -join ' ').Trim()
        if ($script:PythonVersion -notmatch 'Python 3\.') { throw 'Python 3 is required.' }
        return $script:PythonVersion
    }) -and $preflight
    if (-not $preflight) { throw 'Environment preflight failed; see the checks above.' }

    $gitCommand = Get-Command 'git' -ErrorAction SilentlyContinue
    Invoke-Check -Name 'Source commit identity' -Category 'Environment' -Action {
        if ($null -eq $gitCommand) { throw 'Git was not found; commit identity cannot be recorded.' }
        $sha = & $gitCommand.Source -C $Root rev-parse HEAD 2>&1
        if ($LASTEXITCODE -ne 0 -or ([string]$sha).Trim() -notmatch '^[0-9a-fA-F]{40}$') { throw 'Could not resolve source commit SHA.' }
        $script:CommitSha = ([string]$sha).Trim()
        return $script:CommitSha
    } | Out-Null

    Invoke-Check -Name 'Requirement inventory completeness' -Category 'Traceability' -Action { Get-RequirementInventories } | Out-Null

    Invoke-Check -Name 'PowerShell parser — all repository scripts' -Category 'Static analysis' -Action {
        $scripts = @(Get-ChildItem -LiteralPath $Root -Filter '*.ps1' -File -Recurse | Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj|artifacts|\.git)[\\/]'
        })
        if ($scripts.Count -eq 0) { throw 'No PowerShell scripts were found.' }
        $errors = [System.Collections.Generic.List[string]]::new()
        foreach ($file in $scripts) {
            $tokens = $null
            $parseErrors = $null
            [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors) | Out-Null
            foreach ($parseError in $parseErrors) {
                $errors.Add("$($file.FullName):$($parseError.Extent.StartLineNumber): $($parseError.Message)") | Out-Null
            }
        }
        if ($errors.Count -gt 0) { throw ($errors -join [Environment]::NewLine) }
        return "Parsed $($scripts.Count) PowerShell scripts with zero syntax errors."
    } | Out-Null

    Invoke-Check -Name 'PowerShell acceptance primitive tests' -Category 'Static analysis' -Action {
        $primitiveTest = Join-Path $Root 'tools/Test-LayalAcceptancePrimitives.ps1'
        $primitiveLog = Join-Path $ReportDirectory 'powershell-acceptance-primitives'
        $child = Invoke-PowerShellChild -Name 'Test-LayalAcceptancePrimitives' -ScriptPath $primitiveTest -Arguments @() -LogDirectory $primitiveLog
        return "Acceptance result semantics passed. Log: $($child.StdoutPath)"
    } | Out-Null

    $pythonChecks = @(
        @{ Name = 'Static project validation'; Script = 'tools/validate_project.py'; Args = @() }
        @{ Name = 'Distribution source validation'; Script = 'tools/validate_distribution.py'; Args = @() }
        @{ Name = 'AYO feature parity validation'; Script = 'tools/validate_ayo_parity.py'; Args = @() }
        @{ Name = 'Source/release hygiene validation'; Script = 'tools/validate_release.py'; Args = @() }
        @{ Name = 'Third-party license manifest validation'; Script = 'tools/validate_licenses.py'; Args = @('--manifest-only', '--report', (Join-Path $ReportDirectory 'license-manifest-validation.md')) }
    )
    foreach ($item in $pythonChecks) {
        $current = $item
        Invoke-Check -Name $current.Name -Category 'Static analysis' -Action {
            Invoke-ExternalCommand -Executable $script:Python.Source -Arguments (@((Join-Path $Root $current.Script)) + $current.Args) -Description $current.Name
        } | Out-Null
    }

    Invoke-Check -Name 'Python unit tests' -Category 'Automated tests' -Action {
        Invoke-ExternalCommand -Executable $script:Python.Source -Arguments @('-m', 'unittest', 'discover', '-s', 'tests/python', '-p', 'test_*.py') -Description 'Python unit tests'
    } | Out-Null

    $ocrModelsPassed = Invoke-Check -Name 'Install pinned offline Arabic and English OCR models' -Category 'Runtime assets' -Action {
        $modelInstaller = Join-Path $Root 'tools/Install-LayalOcrModels.ps1'
        $modelLog = Join-Path $ReportDirectory 'ocr-model-install'
        $child = Invoke-PowerShellChild -Name 'Install-LayalOcrModels' -ScriptPath $modelInstaller -Arguments @('-Destination', (Join-Path $Root 'src/Layal.Desktop/tessdata')) -LogDirectory $modelLog
        foreach ($name in @('ara.traineddata', 'eng.traineddata')) {
            $modelPath = Join-Path $Root "src/Layal.Desktop/tessdata/$name"
            if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf)) { throw "Pinned OCR model is missing after installation: $modelPath" }
        }
        return "Pinned local OCR models verified before build/test. Log: $($child.StdoutPath)."
    }

    $restorePassed = Invoke-Check -Name 'NuGet restore' -Category 'Build' -Action {
        Invoke-ExternalCommand -Executable $script:Dotnet.Source -Arguments @('restore', 'Layal.sln', '--nologo') -Description 'dotnet restore Layal.sln'
    }
    if ($restorePassed) {
        Invoke-Check -Name 'NuGet dependency vulnerability audit' -Category 'Security' -Action {
            Invoke-ExternalCommand -Executable $script:Python.Source -Arguments @('tools/audit_nuget.py', '--solution', 'Layal.sln', '--output', (Join-Path $ReportDirectory 'nuget-vulnerability-audit.json')) -Description 'NuGet dependency vulnerability audit'
        } | Out-Null
        $buildPassed = Invoke-Check -Name 'Release build' -Category 'Build' -Action {
            Invoke-ExternalCommand -Executable $script:Dotnet.Source -Arguments @('build', 'Layal.sln', '--configuration', 'Release', '--no-restore', '--nologo') -Description 'dotnet build Layal.sln -c Release'
        }
    } else {
        Add-CheckResult -Name 'NuGet dependency vulnerability audit' -Category 'Security' -Status 'SKIPPED' -Details 'Restore did not pass.'
        Add-CheckResult -Name 'Release build' -Category 'Build' -Status 'SKIPPED' -Details 'Restore did not pass.'
        $buildPassed = $false
    }

    if ($buildPassed) {
        $exeCandidates = @(Get-ChildItem -LiteralPath (Join-Path $Root 'src/Layal.Desktop/bin/Release') -Filter 'Layal.exe' -File -Recurse | Where-Object {
            $_.FullName -match '[\\/]net10\.0-windows10\.0\.19041\.0[\\/]Layal\.exe$'
        })
        if ($exeCandidates.Count -gt 0) {
            $script:ReleaseExe = $exeCandidates[0].FullName
            Invoke-Check -Name 'Release executable output' -Category 'Build' -Action {
                $hash = (Get-FileHash -LiteralPath $script:ReleaseExe -Algorithm SHA256).Hash
                return "Found $script:ReleaseExe; SHA-256 $hash."
            } | Out-Null
        } else {
            Add-CheckResult -Name 'Release executable output' -Category 'Build' -Status 'FAIL' -Details 'Release build passed but Layal.exe was not found.'
            $buildPassed = $false
        }
    }

    if ($buildPassed) {
        New-Item -ItemType Directory -Path $script:TrxDirectory -Force | Out-Null
        if ($ocrModelsPassed) {
            $testPassed = Invoke-Check -Name 'C# automated test suite' -Category 'Automated tests' -Action {
                Invoke-ExternalCommand -Executable $script:Dotnet.Source -Arguments @(
                    'test', 'Layal.sln', '--configuration', 'Release', '--no-build', '--no-restore', '--nologo',
                    '--logger', 'trx;LogFileName=Layal.Tests.trx', '--results-directory', $script:TrxDirectory
                ) -Description 'dotnet test Layal.sln -c Release'
            }
        } else {
            Add-CheckResult -Name 'C# automated test suite' -Category 'Automated tests' -Status 'SKIPPED' -Details 'Pinned OCR models failed installation; OCR-dependent tests are not run against an incomplete runtime asset set.'
            $testPassed = $false
        }
        if ($testPassed) {
            Invoke-Check -Name 'TRX executed-test validation' -Category 'Automated tests' -Action {
                Invoke-ExternalCommand -Executable $script:Python.Source -Arguments @('tools/validate_test_results.py', $script:TrxDirectory) -Description 'TRX executed-test validation'
            } | Out-Null
        } else {
            Add-CheckResult -Name 'TRX executed-test validation' -Category 'Automated tests' -Status 'SKIPPED' -Details 'C# test suite did not pass.'
        }
    } else {
        Add-CheckResult -Name 'C# automated test suite' -Category 'Automated tests' -Status 'SKIPPED' -Details 'Release build did not pass.'
        Add-CheckResult -Name 'TRX executed-test validation' -Category 'Automated tests' -Status 'SKIPPED' -Details 'Release build did not pass.'
    }

    if ($RunDesktopIntegration) {
        $desktopReady = $true
        if ($env:GITHUB_ACTIONS -ne 'true' -and -not $ConfirmDisposableProfile) {
            Add-CheckResult -Name 'Desktop safety acknowledgement' -Category 'Safety' -Status 'BLOCKED' -Details 'Use -ConfirmDisposableProfile only from a disposable Windows account; tests manipulate desktop input and launch Layal.'
            $desktopReady = $false
        }
        if (-not [Environment]::UserInteractive) {
            Add-CheckResult -Name 'Interactive desktop session' -Category 'Desktop integration' -Status 'BLOCKED' -Details 'A visible Windows session is required.'
            $desktopReady = $false
        }
        if (-not $script:ReleaseExe -or -not $buildPassed) {
            Add-CheckResult -Name 'Desktop test executable' -Category 'Desktop integration' -Status 'BLOCKED' -Details 'A passing Release build is required.'
            $desktopReady = $false
        }
        if (Get-Process -Name 'Layal' -ErrorAction SilentlyContinue) {
            Add-CheckResult -Name 'Desktop process isolation' -Category 'Desktop integration' -Status 'BLOCKED' -Details 'Close existing Layal processes before isolated UI diagnostics.'
            $desktopReady = $false
        }
        if ($desktopReady) {
            $common = @('-LayalExePath', $script:ReleaseExe)
            Invoke-Check -Name 'Native click-through acceptance' -Category 'Desktop integration' -Action {
                Invoke-DesktopDiagnostic -Name 'click-through' -ScriptName 'Test-LayalClickThrough.Native.ps1' -Arguments ($common + @('-ClickThroughOnly', '-LogDirectory', (Join-Path $ReportDirectory 'click-through'))) -SummaryRelativePath 'native-click-through-summary.json' -ValidateSummary {
                    param($summary)
                    if ($summary.Result -ne 'passed' -or $summary.Failed -ne 0 -or $summary.Passed -lt 4) { throw 'Click-through failed its 4-check acceptance criteria.' }
                }
            } | Out-Null
            Invoke-Check -Name 'Native mouse-drag acceptance' -Category 'Desktop integration' -Action {
                Invoke-DesktopDiagnostic -Name 'mouse-drag' -ScriptName 'Test-LayalClickThrough.Native.ps1' -Arguments ($common + @('-DragOnly', '-LogDirectory', (Join-Path $ReportDirectory 'mouse-drag'))) -SummaryRelativePath 'native-click-through-summary.json' -ValidateSummary {
                    param($summary)
                    if ($summary.Result -ne 'passed' -or $summary.Failed -ne 0 -or $summary.Passed -lt 6) { throw 'Mouse drag failed its 6-check acceptance criteria.' }
                }
            } | Out-Null
            Invoke-Check -Name 'Keyboard navigation and shortcut acceptance' -Category 'Desktop integration' -Action {
                Invoke-DesktopDiagnostic -Name 'keyboard-navigation' -ScriptName 'Test-LayalKeyboardNavigation.ps1' -Arguments ($common + @('-LogDirectory', (Join-Path $ReportDirectory 'keyboard-navigation'))) -SummaryRelativePath 'keyboard-summary.json' -ValidateSummary {
                    param($summary)
                    if ($summary.Result -ne 'passed' -or $summary.Failed -ne 0 -or $summary.ShortcutCases -lt 18) { throw 'Keyboard navigation did not pass all expected cases.' }
                    if ($summary.ManualScreenReaderAcceptance -ne 'pending' -or $summary.ManualHighContrastAcceptance -ne 'pending') { throw 'Automated keyboard tests must not claim manual accessibility acceptance.' }
                }
            } | Out-Null
            Invoke-Check -Name 'Animated 3D GLB preview acceptance' -Category 'Desktop integration' -Action {
                Invoke-DesktopDiagnostic -Name 'glb-preview' -ScriptName 'Test-LayalGlbPreview.ps1' -Arguments ($common + @('-LogDirectory', (Join-Path $ReportDirectory 'glb-preview'))) -SummaryRelativePath 'glb-preview-summary.json' -ValidateSummary {
                    param($summary)
                    if ($summary.Result -ne 'passed' -or $summary.FramesAfter -le $summary.FramesBefore) { throw 'GLB preview render loop did not advance.' }
                    if ($summary.ManualScreenReaderAcceptance -ne 'pending' -or $summary.ManualHighContrastAcceptance -ne 'pending') { throw 'Automated preview tests must not claim manual accessibility acceptance.' }
                }
            } | Out-Null
            Invoke-Check -Name 'Display and DPI topology smoke diagnostic' -Category 'Desktop integration' -Action {
                Invoke-DesktopDiagnostic -Name 'display-dpi' -ScriptName 'Test-LayalDisplayDpi.ps1' -Arguments ($common + @('-ObserveSeconds', '3', '-LogDirectory', (Join-Path $ReportDirectory 'display-dpi'))) -SummaryRelativePath 'display-dpi-summary.json' -ValidateSummary {
                    param($summary)
                    if ($summary.Result -ne 'passed') { throw 'Display/DPI smoke diagnostic failed.' }
                    if ($summary.ManualVisualAcceptanceStatus -ne 'pending') { throw 'Automation must not claim manual visual acceptance.' }
                    if ($summary.MonitorCount -lt 2 -and $summary.TopologyDpiGateStatus -eq 'passed') { throw 'A single-monitor smoke run cannot certify mixed-DPI acceptance.' }
                }
            } | Out-Null
        } else {
            Add-CheckResult -Name 'Native desktop diagnostics' -Category 'Desktop integration' -Status 'SKIPPED' -Details 'Desktop preconditions were not met.'
        }
    } else {
        Add-CheckResult -Name 'Native desktop diagnostics' -Category 'Desktop integration' -Status 'SKIPPED' -Details 'Pass -RunDesktopIntegration to run mouse, keyboard, display/DPI, and GLB checks.'
    }

    if ($RunInstallerLifecycle) {
        if ($env:GITHUB_ACTIONS -ne 'true' -and -not $ConfirmDisposableProfile) {
            Add-CheckResult -Name 'Installer lifecycle safety acknowledgement' -Category 'Installer' -Status 'BLOCKED' -Details 'Install/upgrade/rollback/uninstall tests change installation files, registry entries, shortcuts, and scheduled tasks. Use CI or a disposable VM.'
        } else {
            if ($env:GITHUB_ACTIONS -ne 'true') { $env:LAYAL_ALLOW_INSTALLER_TESTS = '1' }
            Invoke-Check -Name 'Distribution build and installer lifecycle' -Category 'Installer' -Action {
                $builder = Join-Path $Root 'tools/Build-LayalDistribution.ps1'
                $childLog = Join-Path $ReportDirectory 'distribution-build'
                $null = Invoke-PowerShellChild -Name 'Build-LayalDistribution' -ScriptPath $builder -Arguments @('-RunInstallerLifecycleTests') -LogDirectory $childLog
                $distPath = Join-Path $Root 'artifacts/distribution'
                if (-not (Test-Path -LiteralPath $distPath -PathType Container)) { throw 'Distribution build did not create artifacts/distribution.' }
                Invoke-ExternalCommand -Executable $script:Python.Source -Arguments @('tools/validate_distribution.py', '--artifacts') -Description 'Validate generated distribution'
                $verify = Join-Path $Root 'tools/Verify-LayalDistributionIntegrity.ps1'
                $verifyLog = Join-Path $ReportDirectory 'distribution-integrity'
                $verifyChild = Invoke-PowerShellChild -Name 'Verify-LayalDistributionIntegrity' -ScriptPath $verify -Arguments @('-DistributionDirectory', $distPath) -LogDirectory $verifyLog
                return "Package integrity and installer lifecycle passed. Builder log: $($childLog); integrity log: $($verifyChild.StdoutPath)."
            } | Out-Null
        }
    } else {
        Add-CheckResult -Name 'Distribution build and installer lifecycle' -Category 'Installer' -Status 'SKIPPED' -Details 'Pass -RunInstallerLifecycle on CI or a disposable Windows VM to verify packaging, install, upgrade, rollback, and uninstall.'
    }

    $result = Write-AcceptanceReports
    if ($result.FailedCount -gt 0 -or $result.BlockedCount -gt 0) { exit 1 }
    exit 0
}
catch {
    Add-CheckResult -Name 'Acceptance runner execution' -Category 'Runner' -Status 'FAIL' -Details $_.Exception.Message
    try { $null = Write-AcceptanceReports } catch { Write-Host "Could not write final report: $($_.Exception.Message)" -ForegroundColor Red }
    exit 1
}
finally {
    if ($null -ne $previousInstallerOptIn) {
        $env:LAYAL_ALLOW_INSTALLER_TESTS = $previousInstallerOptIn
    } elseif ($env:GITHUB_ACTIONS -ne 'true') {
        Remove-Item Env:LAYAL_ALLOW_INSTALLER_TESTS -ErrorAction SilentlyContinue
    }
}
