[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$LayalExePath,
    [string]$LogDirectory = "artifacts/glb-preview",
    [ValidateRange(10, 60)]
    [int]$StartupTimeoutSeconds = 25
)

$ErrorActionPreference = "Stop"
$resolvedExe = (Resolve-Path -LiteralPath $LayalExePath).Path
if ([IO.Path]::GetFileName($resolvedExe) -ine "Layal.exe") {
    throw "Expected the actual Layal.exe release executable."
}
if (Get-Process -Name "Layal" -ErrorAction SilentlyContinue) {
    throw "A Layal process is already running. Close it before this isolated GLB preview test."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$resolvedLogDirectory = [IO.Path]::GetFullPath($LogDirectory)
New-Item -ItemType Directory -Path $resolvedLogDirectory -Force | Out-Null
$summaryPath = Join-Path $resolvedLogDirectory "glb-preview-summary.json"
Remove-Item -LiteralPath $summaryPath -Force -ErrorAction SilentlyContinue

$previousTestFlag = $env:LAYAL_GLB_PREVIEW_TEST
$process = $null
$tests = [System.Collections.Generic.List[object]]::new()

function Find-GlbPreviewElement {
    param([int]$ProcessId)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $elements = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($element in $elements) {
        try {
            if ($element.Current.Name -like "GLB 3D preview*") {
                return $element
            }
        } catch {
            # UIA elements may become stale while windows initialize; retry during the polling loop.
        }
    }
    return $null
}

function Read-PreviewHelpText {
    param($Element)
    try {
        $value = $Element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::HelpTextProperty)
        if ($null -eq $value -or $value -eq [System.Windows.Automation.AutomationElement]::NotSupported)
            { return "" }
        return [string]$value
    } catch {
        return ""
    }
}

try {
    $env:LAYAL_GLB_PREVIEW_TEST = "1"
    $process = Start-Process -FilePath $resolvedExe -PassThru
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    $preview = $null
    while ((Get-Date) -lt $deadline -and $null -eq $preview) {
        Start-Sleep -Milliseconds 300
        $process.Refresh()
        if ($process.HasExited) { throw "Layal exited before the GLB preview became accessible." }
        $preview = Find-GlbPreviewElement -ProcessId $process.Id
    }
    if ($null -eq $preview) {
        throw "The active GLB viewport was not exposed through Windows UI Automation."
    }

    $initial = Read-PreviewHelpText -Element $preview
    foreach ($required in @("vertices=370", "triangles=256", "joints=19", "animation=")) {
        $ok = $initial.Contains($required)
        $tests.Add([pscustomobject]@{
            Test = "GLB diagnostics include $required"
            Result = if ($ok) { "PASS" } else { "FAIL" }
            Details = $initial
        })
        if (-not $ok) { throw "GLB preview diagnostics did not contain '$required'. Actual: $initial" }
    }

    $beforeMatch = [regex]::Match($initial, "frames=(\d+)")
    if (-not $beforeMatch.Success) { throw "The GLB preview did not report its render-frame count." }
    $beforeFrames = [int]$beforeMatch.Groups[1].Value

    Start-Sleep -Milliseconds 1200
    $preview = Find-GlbPreviewElement -ProcessId $process.Id
    if ($null -eq $preview) { throw "The GLB preview disappeared while the animation was running." }
    $after = Read-PreviewHelpText -Element $preview
    $afterMatch = [regex]::Match($after, "frames=(\d+)")
    if (-not $afterMatch.Success) { throw "The GLB preview stopped reporting render frames." }
    $afterFrames = [int]$afterMatch.Groups[1].Value
    $advancing = $afterFrames -gt $beforeFrames
    $tests.Add([pscustomobject]@{
        Test = "GLB animation render loop advances"
        Result = if ($advancing) { "PASS" } else { "FAIL" }
        Details = "frames before=$beforeFrames; frames after=$afterFrames; $after"
    })
    if (-not $advancing) { throw "The model loaded but its render loop did not advance." }

    $summary = [pscustomobject]@{
        Result = "passed"
        ProcessId = $process.Id
        Executable = $resolvedExe
        PreviewName = $preview.Current.Name
        FramesBefore = $beforeFrames
        FramesAfter = $afterFrames
        Tests = @($tests)
        ManualScreenReaderAcceptance = "pending"
        ManualHighContrastAcceptance = "pending"
        ManualVisualAcceptance = "pending"
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
    Write-Host "GLB preview: PASS; skinned vertices are loaded and the animation render loop is advancing."
}
catch {
    [pscustomobject]@{
        Result = "failed"
        Error = $_.Exception.Message
        Tests = @($tests)
        ManualScreenReaderAcceptance = "pending"
        ManualHighContrastAcceptance = "pending"
        ManualVisualAcceptance = "pending"
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
    throw
}
finally {
    if ($null -ne $process) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if ($null -eq $previousTestFlag) {
        Remove-Item Env:LAYAL_GLB_PREVIEW_TEST -ErrorAction SilentlyContinue
    } else {
        $env:LAYAL_GLB_PREVIEW_TEST = $previousTestFlag
    }
}
