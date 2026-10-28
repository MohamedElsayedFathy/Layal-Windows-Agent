[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PortableArchivePath,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ChecksumManifestPath,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$')]
    [string]$DeviceId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$TestedCommitSha,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactReference,

    [string]$LogDirectory,

    [ValidateRange(30, 3600)]
    [int]$DisplayObserveSeconds = 90,

    [switch]$RequireMixedDpiTransition,

    [switch]$ConfirmDisposableProfile
)

$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Live acceptance must run on Windows.'
}
if ($env:GITHUB_ACTIONS -eq 'true') {
    throw 'This runner is for a real, interactive target PC. GitHub Actions results cannot replace live Windows acceptance.'
}
if (-not [Environment]::UserInteractive) {
    throw 'A non-interactive Windows session cannot be used for live acceptance.'
}
if ($ArtifactReference -notmatch '^https://\S+$') {
    throw 'ArtifactReference must be an HTTPS URL for the exact tested build or workflow run.'
}
if (Get-Process -Name 'Layal' -ErrorAction SilentlyContinue) {
    throw 'Close all existing Layal processes before running the isolated acceptance suite.'
}

$resolvedArchive = (Resolve-Path -LiteralPath $PortableArchivePath).Path
if ([IO.Path]::GetExtension($resolvedArchive) -ine '.zip') {
    throw 'PortableArchivePath must point to the published portable ZIP.'
}
$resolvedManifest = (Resolve-Path -LiteralPath $ChecksumManifestPath).Path
if ([IO.Path]::GetDirectoryName($resolvedArchive) -ine [IO.Path]::GetDirectoryName($resolvedManifest)) {
    throw 'Keep the portable ZIP and SHA256SUMS.txt from the same downloaded distribution artifact directory.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$gitHead = (& git -C $repoRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace([string]$gitHead)) {
    throw "Could not read the repository HEAD. Run this script from a full Git checkout at commit $TestedCommitSha."
}
$gitHead = ([string]$gitHead).Trim()
if ($gitHead -ine $TestedCommitSha) {
    throw "The checkout HEAD ($gitHead) does not match TestedCommitSha ($TestedCommitSha). Check out the exact source commit associated with the downloaded artifact."
}

$artifactUri = $null
if (-not [Uri]::TryCreate($ArtifactReference, [UriKind]::Absolute, [ref]$artifactUri)) {
    throw 'ArtifactReference must be an absolute HTTPS URL.'
}
if ($artifactUri.Scheme -ne 'https') {
    throw 'ArtifactReference must use HTTPS.'
}

if (-not $ConfirmDisposableProfile) {
    throw 'Use a disposable Windows test account with no personal Layal data or secrets, then pass -ConfirmDisposableProfile to acknowledge that requirement.'
}
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'LOCALAPPDATA is unavailable; cannot verify a clean per-user Layal data area.'
}
$localLayalData = Join-Path $env:LOCALAPPDATA 'Layal'
if (Test-Path -LiteralPath $localLayalData) {
    throw "Refusing to run against an account that already has Layal local data at $localLayalData. Use a fresh disposable Windows account."
}
if (Get-Process -Name 'Layal' -ErrorAction SilentlyContinue) {
    throw 'Close all existing Layal processes before running the isolated acceptance suite.'
}

$pwshCommand = Get-Command 'pwsh' -ErrorAction SilentlyContinue
if ($null -eq $pwshCommand -or [string]::IsNullOrWhiteSpace($pwshCommand.Source)) {
    throw 'PowerShell 7 (pwsh) is required to execute the isolated Windows acceptance scripts.'
}
$pwshPath = $pwshCommand.Source

$sessionStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $LogDirectory = Join-Path $repoRoot "artifacts\windows-live-acceptance\$sessionStamp-$DeviceId"
}
if (-not [IO.Path]::IsPathRooted($LogDirectory)) {
    $LogDirectory = Join-Path $repoRoot $LogDirectory
}
$sessionDirectory = [IO.Path]::GetFullPath($LogDirectory)
if (Test-Path -LiteralPath $sessionDirectory) {
    throw "Refusing to overwrite existing acceptance evidence: $sessionDirectory"
}
New-Item -ItemType Directory -Path $sessionDirectory -Force | Out-Null
$startedAtLocal = [DateTimeOffset]::Now.ToString('o')

$archiveHash = (Get-FileHash -LiteralPath $resolvedArchive -Algorithm SHA256).Hash.ToUpperInvariant()
$archiveFileName = [IO.Path]::GetFileName($resolvedArchive)
$manifestHashes = @()
foreach ($line in (Get-Content -LiteralPath $resolvedManifest)) {
    if ($line -match '^\s*([0-9a-fA-F]{64})\s+(.+?)\s*$') {
        $relativeName = $matches[2].Trim().Replace('\', '/').TrimStart('*')
        if ([IO.Path]::GetFileName($relativeName) -ieq $archiveFileName) {
            $manifestHashes += $matches[1].ToUpperInvariant()
        }
    }
}
if ($manifestHashes.Count -ne 1) {
    throw "Expected exactly one SHA-256 entry for $archiveFileName in the adjacent checksum manifest; found $($manifestHashes.Count)."
}
if ($archiveHash -ine $manifestHashes[0]) {
    throw "Portable ZIP SHA-256 mismatch. Actual=$archiveHash, manifest=$($manifestHashes[0])."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipArchive = [IO.Compression.ZipFile]::OpenRead($resolvedArchive)
try {
    $rootExecutableCount = 0
    foreach ($entry in $zipArchive.Entries) {
        $entryName = $entry.FullName.Replace('\', '/')
        if ($entryName.StartsWith('/') -or $entryName -match '^[A-Za-z]:' -or @($entryName.Split('/')) -contains '..') {
            throw "Unsafe path in portable ZIP: $entryName"
        }
        if ($entryName -ceq 'Layal.exe') {
            $rootExecutableCount++
        }
    }
    if ($rootExecutableCount -ne 1) {
        throw "Portable ZIP must contain exactly one Layal.exe at its root; found $rootExecutableCount."
    }
}
finally {
    $zipArchive.Dispose()
}

$extractionDirectory = Join-Path $sessionDirectory 'portable-extracted'
New-Item -ItemType Directory -Path $extractionDirectory -Force | Out-Null
Expand-Archive -LiteralPath $resolvedArchive -DestinationPath $extractionDirectory -Force
$resolvedExe = Join-Path $extractionDirectory 'Layal.exe'
if (-not (Test-Path -LiteralPath $resolvedExe -PathType Leaf)) {
    throw 'Extracted portable artifact does not contain Layal.exe at its root.'
}
$executableHash = (Get-FileHash -LiteralPath $resolvedExe -Algorithm SHA256).Hash.ToUpperInvariant()
$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedExe)
Add-Type -AssemblyName System.Windows.Forms
$screens = @(
    [System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
        [pscustomobject]@{
            DeviceName = $_.DeviceName
            Primary = [bool]$_.Primary
            Bounds = [pscustomobject]@{
                X = $_.Bounds.X
                Y = $_.Bounds.Y
                Width = $_.Bounds.Width
                Height = $_.Bounds.Height
            }
            WorkArea = [pscustomobject]@{
                X = $_.WorkingArea.X
                Y = $_.WorkingArea.Y
                Width = $_.WorkingArea.Width
                Height = $_.WorkingArea.Height
            }
            DpiStatus = 'See display-dpi-summary.json; screen bounds alone do not certify DPI.'
        }
    }
)

Write-Host ''
Write-Host 'Layal live Windows acceptance'
Write-Host "Device ID: $DeviceId"
Write-Host "Tested commit: $gitHead"
Write-Host "Executable SHA-256: $executableHash"
Write-Host "Evidence folder: $sessionDirectory"
Write-Host 'The suite runs native UI tests and may move the Layal window or mouse. Do not use the desktop during the automated checks.'
Write-Host 'Do not use real provider keys, private messages, or personal data during acceptance.'
[void](Read-Host 'Close important desktop applications, ensure the correct test monitors are active, and press Enter to start')

function ConvertTo-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Invoke-WindowAcceptanceTool {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ScriptName,
        [Parameter(Mandatory = $true)][string]$SummaryName,
        [Parameter(Mandatory = $true)][string[]]$ToolArguments
    )

    $scriptPath = Join-Path $resolvedScriptDirectory $ScriptName
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "Required acceptance script is missing: $scriptPath"
    }

    $runDirectory = Join-Path $sessionDirectory $Name
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    $stdoutPath = Join-Path $runDirectory 'console.stdout.log'
    $stderrPath = Join-Path $runDirectory 'console.stderr.log'
    $summaryPath = Join-Path $runDirectory $SummaryName
    $exitCode = 1
    $launchError = $null

    if ($Name -eq 'display-dpi') {
        Write-Host ''
        Write-Host "Display/DPI capture lasts $DisplayObserveSeconds seconds."
        if ($RequireMixedDpiTransition) {
            Write-Host 'When Layal appears, physically drag its floating mascot between two active monitors set to different Windows scale factors.'
        } else {
            Write-Host 'If a second monitor is available, move Layal between displays during the capture. Without observed transitions this test cannot certify the multi-monitor DPI gate.'
        }
    }

    $childArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptPath) + $ToolArguments
    $quotedArguments = @($childArguments | ForEach-Object { ConvertTo-ProcessArgument -Value ([string]$_) }) -join ' '
    try {
        $process = Start-Process -FilePath $pwshPath -ArgumentList $quotedArguments -Wait -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        $exitCode = [int]$process.ExitCode
    }
    catch {
        $launchError = $_.Exception.Message
        [IO.File]::WriteAllText($stderrPath, $launchError, [Text.UTF8Encoding]::new($false))
    }

    $summary = $null
    if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
        try {
            $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        }
        catch {
            $launchError = "Could not parse summary file: $($_.Exception.Message)"
        }
    }

    $reportedResult = if ($null -eq $summary) { 'missing' } else { [string]$summary.Result }
    $topologyStatus = if ($Name -eq 'display-dpi' -and $null -ne $summary) {
        [string]$summary.TopologyDpiGateStatus
    } else {
        'not-applicable'
    }

    $passed = ($exitCode -eq 0 -and $reportedResult -eq 'passed')
    if ($Name -eq 'display-dpi' -and $RequireMixedDpiTransition -and $topologyStatus -ne 'passed') {
        $passed = $false
        if ([string]::IsNullOrWhiteSpace($launchError)) {
            $launchError = "Mixed-DPI transition was required but the diagnostic reported '$topologyStatus'."
        }
    }

    return [pscustomobject]@{
        Name = $Name
        Status = if ($passed) { 'PASS' } else { 'FAIL' }
        ProcessExitCode = $exitCode
        ReportedResult = $reportedResult
        TopologyDpiGateStatus = $topologyStatus
        ManualVisualAcceptanceStatus = if ($Name -eq 'display-dpi' -and $null -ne $summary) {
            [string]$summary.ManualVisualAcceptanceStatus
        } elseif ($Name -eq 'keyboard-navigation' -and $null -ne $summary) {
            [string]$summary.ManualScreenReaderAcceptance
        } elseif ($Name -eq 'glb-preview' -and $null -ne $summary) {
            [string]$summary.ManualVisualAcceptance
        } else {
            'not-applicable'
        }
        SummaryPath = if ($null -ne $summary) { Join-Path $Name $SummaryName } else { '' }
        StdoutPath = Join-Path $Name 'console.stdout.log'
        StderrPath = Join-Path $Name 'console.stderr.log'
        Error = $launchError
    }
}

$testRuns = [System.Collections.Generic.List[object]]::new()
$commonArguments = @('-LayalExePath', $resolvedExe)

$testRuns.Add((Invoke-WindowAcceptanceTool -Name 'click-through' -ScriptName 'Test-LayalClickThrough.Native.ps1' -SummaryName 'native-click-through-summary.json' -ToolArguments ($commonArguments + @('-LogDirectory', (Join-Path $sessionDirectory 'click-through'), '-ClickThroughOnly'))))

$testRuns.Add((Invoke-WindowAcceptanceTool -Name 'mouse-drag' -ScriptName 'Test-LayalClickThrough.Native.ps1' -SummaryName 'native-click-through-summary.json' -ToolArguments ($commonArguments + @('-LogDirectory', (Join-Path $sessionDirectory 'mouse-drag'), '-DragOnly'))))

$testRuns.Add((Invoke-WindowAcceptanceTool -Name 'keyboard-navigation' -ScriptName 'Test-LayalKeyboardNavigation.ps1' -SummaryName 'keyboard-summary.json' -ToolArguments ($commonArguments + @('-LogDirectory', (Join-Path $sessionDirectory 'keyboard-navigation')))))

$testRuns.Add((Invoke-WindowAcceptanceTool -Name 'glb-preview' -ScriptName 'Test-LayalGlbPreview.ps1' -SummaryName 'glb-preview-summary.json' -ToolArguments ($commonArguments + @('-LogDirectory', (Join-Path $sessionDirectory 'glb-preview')))))

$displayArguments = $commonArguments + @('-LogDirectory', (Join-Path $sessionDirectory 'display-dpi'), '-ObserveSeconds', [string]$DisplayObserveSeconds)
if ($RequireMixedDpiTransition) {
    $displayArguments += @('-RequireMultipleMonitors', '-RequireMonitorTransition', '-RequireDpiTransition')
}
$testRuns.Add((Invoke-WindowAcceptanceTool -Name 'display-dpi' -ScriptName 'Test-LayalDisplayDpi.ps1' -SummaryName 'display-dpi-summary.json' -ToolArguments $displayArguments))

$checklistRows = @(
    @{ Id = 'WIN-01'; Area = 'Release artifact identity and clean environment' },
    @{ Id = 'WIN-02'; Area = 'Startup, normal exit, and restart' },
    @{ Id = 'WIN-03'; Area = 'Tray, show/hide, and process cleanup' },
    @{ Id = 'WIN-04'; Area = 'Single click, double click, and context menu' },
    @{ Id = 'WIN-05'; Area = 'Visible mascot hit testing and transparent click-through' },
    @{ Id = 'WIN-06'; Area = 'Dragging across workspace and persisted position' },
    @{ Id = 'WIN-07'; Area = 'Visual checks at available Windows DPI scales' },
    @{ Id = 'WIN-08'; Area = 'Two monitors at different DPI scales' },
    @{ Id = 'WIN-09'; Area = 'Arabic/English RTL/LTR layout and mixed text' },
    @{ Id = 'WIN-10'; Area = 'Light/dark theme, focus, and contrast' },
    @{ Id = 'WIN-11'; Area = 'Settings persisted across a full restart' },
    @{ Id = 'WIN-12'; Area = 'Privacy default and explicit opt-in behavior' },
    @{ Id = 'WIN-13'; Area = 'Invalid settings rejected without partial changes' },
    @{ Id = 'WIN-14'; Area = 'Synthetic local data and Windows DPAPI persistence' },
    @{ Id = 'WIN-15'; Area = 'Provider connection and tool approval/denial' },
    @{ Id = 'WIN-16'; Area = 'Installer, upgrade, rollback, and uninstall on a disposable profile' },
    @{ Id = 'WIN-17'; Area = 'Visual stability during idle and a longer task' },
    @{ Id = 'WIN-18'; Area = 'Evidence completeness, failures, and final reviewer decision' },
    @{ Id = 'WIN-19'; Area = 'Initial window sizing and work-area boundaries' }
)
$checklistPath = Join-Path $sessionDirectory 'manual-checklist.csv'
$checklistRows | ForEach-Object {
    [pscustomobject]@{
        Id = $_.Id
        Area = $_.Area
        Status = 'NOT TESTED'
        ObservedResult = ''
        EvidencePath = ''
        ReviewedBy = ''
        ReviewedOn = ''
    }
} | Export-Csv -LiteralPath $checklistPath -NoTypeInformation -Encoding UTF8

$manualGates = [ordered]@{
    windowsDesktopAndSessions = [pscustomobject]@{ Status = 'PENDING'; ReviewedBy = ''; ReviewedOn = ''; Evidence = '' }
    screenReaderAndHighContrast = [pscustomobject]@{ Status = 'PENDING'; ReviewedBy = ''; ReviewedOn = ''; Evidence = '' }
    rtlLtrDisplayScaling = [pscustomobject]@{ Status = 'PENDING'; ReviewedBy = ''; ReviewedOn = ''; Evidence = '' }
    multiMonitorDpi = [pscustomobject]@{ Status = 'PENDING'; ReviewedBy = ''; ReviewedOn = ''; Evidence = '' }
}

$failedRuns = @($testRuns | Where-Object { $_.Status -ne 'PASS' })
$session = [pscustomobject]@{
    SchemaVersion = 1
    StartedAtLocal = $startedAtLocal
    CompletedAtLocal = [DateTimeOffset]::Now.ToString('o')
    Decision = 'NO-GO'
    AutomatedDiagnosticsStatus = if ($failedRuns.Count -eq 0) { 'PASS' } else { 'FAIL' }
    ManualAcceptanceStatus = 'PENDING'
    DeviceId = $DeviceId
    TestedCommitSha = $gitHead
    ArtifactReference = $ArtifactReference
    PortableArtifact = [pscustomobject]@{
        ArchiveFileName = $archiveFileName
        ArchiveSHA256 = $archiveHash
        ChecksumManifestFileName = [IO.Path]::GetFileName($resolvedManifest)
        ArchiveHashMatchedManifest = $true
        ExtractedExecutableSHA256 = $executableHash
    }
    Environment = [pscustomobject]@{
        WindowsCaption = [string]$operatingSystem.Caption
        WindowsVersion = [string]$operatingSystem.Version
        WindowsBuild = [string]$operatingSystem.BuildNumber
        WindowsArchitecture = [string]$operatingSystem.OSArchitecture
        PowerShellVersion = [string]$PSVersionTable.PSVersion
        InteractiveSession = [bool][Environment]::UserInteractive
        MonitorCount = $screens.Count
        Screens = $screens
    }
    Executable = [pscustomobject]@{
        FileName = [IO.Path]::GetFileName($resolvedExe)
        ProductVersion = [string]$fileVersion.ProductVersion
        FileVersion = [string]$fileVersion.FileVersion
        SHA256 = $executableHash
    }
    DiagnosticRuns = @($testRuns)
    ManualChecklistPath = 'manual-checklist.csv'
    ManualReleaseGates = $manualGates
    Notes = @(
        'Passing diagnostics are limited to the checks and desktop session recorded by the scripts.',
        'Screen reader, High Contrast, visual RTL/LTR, DPI-scale review, and human observation remain pending until reviewed with evidence.',
        'Do not copy this session into docs/RELEASE_GATE_EVIDENCE.json as APPROVED without completing the manual review and verifying the evidence paths.'
    )
}

$manifestPath = Join-Path $sessionDirectory 'live-acceptance-session.json'
[IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $session -Depth 10), [Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host "Automated diagnostic result: $($session.AutomatedDiagnosticsStatus)"
Write-Host 'Manual Windows release acceptance: PENDING'
Write-Host 'Release decision: NO-GO until manual gates and all other release blockers are reviewed.'
Write-Host "Session manifest: $manifestPath"
Write-Host "Manual checklist: $checklistPath"

if ($failedRuns.Count -gt 0) {
    Write-Error "One or more live diagnostics failed. Inspect the session manifest and per-test console logs in $sessionDirectory."
    exit 1
}
exit 0
