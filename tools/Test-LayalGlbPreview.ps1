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
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$resolvedLogDirectory = [IO.Path]::GetFullPath($LogDirectory)
New-Item -ItemType Directory -Path $resolvedLogDirectory -Force | Out-Null
$summaryPath = Join-Path $resolvedLogDirectory "glb-preview-summary.json"
Remove-Item -LiteralPath $summaryPath -Force -ErrorAction SilentlyContinue

$previousTestFlag = $env:LAYAL_GLB_PREVIEW_TEST
$process = $null
$tests = [System.Collections.Generic.List[object]]::new()
$uiaWindowSnapshot = @()
$processSnapshot = @()
$screenshotPath = Join-Path $resolvedLogDirectory "glb-preview-window.png"
$screenshotEvidence = $null

function Save-PreviewWindowScreenshot {
    param(
        [Parameter(Mandatory = $true)]$WindowElement,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $rectangle = $WindowElement.Current.BoundingRectangle
    if ($rectangle.Width -lt 2 -or $rectangle.Height -lt 2) {
        throw "The GLB preview window reports an empty UI Automation rectangle: $rectangle"
    }

    # UIA bounding rectangles use physical screen pixels. Clip to the Windows virtual desktop
    # to avoid invalid Bitmap sizes on hosted agents with unusual display topology.
    $virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $left = [Math]::Max([int][Math]::Floor($rectangle.Left), $virtual.Left)
    $top = [Math]::Max([int][Math]::Floor($rectangle.Top), $virtual.Top)
    $right = [Math]::Min([int][Math]::Ceiling($rectangle.Right), $virtual.Right)
    $bottom = [Math]::Min([int][Math]::Ceiling($rectangle.Bottom), $virtual.Bottom)
    $width = $right - $left
    $height = $bottom - $top
    if ($width -lt 2 -or $height -lt 2) {
        throw "The GLB preview window is outside the virtual desktop bounds."
    }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = $null
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($left, $top, 0, 0, $bitmap.Size, [System.Drawing.CopyPixelOperation]::SourceCopy)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        if ($null -ne $graphics) { $graphics.Dispose() }
        $bitmap.Dispose()
    }
    return $Path
}

function Find-GlbPreviewElement {
    param([int]$ProcessId)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $namedCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, "GLB 3D preview — Rigged Figure")
    $elements = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $processCondition)
    foreach ($element in $elements) {
        try {
            if ($element.Current.Name -like "GLB 3D preview*") { return $element }
            $named = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $namedCondition)
            if ($null -ne $named -and $named.Current.ProcessId -eq $ProcessId) { return $named }
        } catch {
            # UIA elements may become stale while WPF initializes; retry during the polling loop.
        }
    }
    # Fallback search by stable peer name, but still verify the owning process to avoid matching
    # an unrelated Layal instance or another application's look-alike element.
    $allNamed = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $namedCondition)
    foreach ($element in $allNamed) {
        try {
            if ($element.Current.ProcessId -eq $ProcessId) { return $element }
        } catch { }
    }
    return $null
}

function Get-UiAutomationWindowSnapshot {
    param([int]$ProcessId)
    $snapshot = [System.Collections.Generic.List[object]]::new()
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Window)
    $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $windowCondition)
    foreach ($window in $windows) {
        try {
            $current = $window.Current
            $snapshot.Add([pscustomobject]@{
                Name = [string]$current.Name
                ProcessId = [int]$current.ProcessId
                IsTargetProcess = ($current.ProcessId -eq $ProcessId)
                NativeWindowHandle = [int]$current.NativeWindowHandle
                IsOffscreen = [bool]$current.IsOffscreen
                Bounds = [string]$current.BoundingRectangle
                HelpText = [string](Read-PreviewHelpText -Element $window)
            })
        } catch {
            $snapshot.Add([pscustomobject]@{ Name = "<stale UIA window>"; ProcessId = -1; Error = $_.Exception.Message })
        }
    }
    return @($snapshot)
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
    # Use an explicit command-line flag as well as the inherited environment variable so test mode
    # cannot be lost if the process is started under a differently-scoped environment.
    $process = Start-Process -FilePath $resolvedExe -ArgumentList "--glb-preview-test" -PassThru
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    $preview = $null
    while ((Get-Date) -lt $deadline -and $null -eq $preview) {
        Start-Sleep -Milliseconds 300
        $process.Refresh()
        if ($process.HasExited) { throw "Layal exited before the GLB preview became accessible." }
        $preview = Find-GlbPreviewElement -ProcessId $process.Id
    }
    if ($null -eq $preview) {
        $uiaWindowSnapshot = @(Get-UiAutomationWindowSnapshot -ProcessId $process.Id)
        $processSnapshot = @(Get-Process -Name "Layal" -ErrorAction SilentlyContinue |
            Select-Object Id, ProcessName, MainWindowTitle, MainWindowHandle, HasExited)
        $windowJson = ConvertTo-Json -InputObject $uiaWindowSnapshot -Depth 5 -Compress
        $processJson = ConvertTo-Json -InputObject $processSnapshot -Depth 5 -Compress
        throw "The GLB preview was not exposed through UI Automation within $StartupTimeoutSeconds seconds. UIA windows: $windowJson; processes: $processJson"
    }

    $initial = Read-PreviewHelpText -Element $preview
    if ($initial -match "GLB preview failed:") {
        $tests.Add([pscustomobject]@{
            Test = "GLB preview loads successfully"
            Result = "FAIL"
            Details = $initial
        })
        throw "The GLB renderer reported a load failure: $initial"
    }
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
    $screenshotEvidence = Save-PreviewWindowScreenshot -WindowElement $preview -Path $screenshotPath
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
        Screenshot = $screenshotEvidence
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
        Screenshot = $screenshotEvidence
        UiAutomationWindows = @($uiaWindowSnapshot)
        ProcessSnapshot = @($processSnapshot)
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
