[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$LayalExePath,
    [string]$LogDirectory = "artifacts/display-dpi",
    [ValidateRange(5, 120)]
    [int]$StartupTimeoutSeconds = 25,
    [ValidateRange(0, 3600)]
    [int]$ObserveSeconds = 0,
    [ValidateRange(100, 2000)]
    [int]$SampleIntervalMilliseconds = 250,
    [switch]$RequireMultipleMonitors,
    [switch]$RequireMonitorTransition,
    [switch]$RequireDpiTransition
)

$ErrorActionPreference = "Stop"
$resolvedExe = (Resolve-Path -LiteralPath $LayalExePath).Path
if ([IO.Path]::GetFileName($resolvedExe) -ine "Layal.exe") {
    throw "Expected the actual Layal.exe release executable."
}
if ($RequireDpiTransition) {
    $RequireMultipleMonitors = $true
    $RequireMonitorTransition = $true
}
if ($RequireMonitorTransition) { $RequireMultipleMonitors = $true }
if ((Get-Process -Name "Layal" -ErrorAction SilentlyContinue)) {
    throw "A Layal process is already running. Close it before running this isolated display/DPI diagnostic."
}

$resolvedLogDirectory = [IO.Path]::GetFullPath($LogDirectory)
New-Item -ItemType Directory -Path $resolvedLogDirectory -Force | Out-Null
$jsonPath = Join-Path $resolvedLogDirectory "display-dpi-summary.json"
$csvPath = Join-Path $resolvedLogDirectory "display-dpi-samples.csv"
Remove-Item -LiteralPath $jsonPath, $csvPath -Force -ErrorAction SilentlyContinue

$nativeCode = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class LayalDisplayDpiNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public sealed class MonitorSnapshot
    {
        public long Handle { get; set; }
        public string DeviceName { get; set; }
        public bool IsPrimary { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public int WorkLeft { get; set; }
        public int WorkTop { get; set; }
        public int WorkRight { get; set; }
        public int WorkBottom { get; set; }
        public uint DpiX { get; set; }
        public uint DpiY { get; set; }
        public int DpiHResult { get; set; }
    }

    public sealed class WindowSnapshot
    {
        public long MonitorHandle { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public uint Dpi { get; set; }
        public int DpiAwareness { get; set; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr clipRect, IntPtr data);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clipRect, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    private static IntPtr EnterPerMonitorV2Context()
    {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "Could not set a temporary Per-Monitor-V2 thread context for physical screen coordinates.");
        return previous;
    }

    public static MonitorSnapshot[] EnumerateMonitors()
    {
        var monitors = new List<MonitorSnapshot>();
        IntPtr previous = EnterPerMonitorV2Context();
        try
        {
            MonitorEnumProc callback = (hMonitor, hdc, clipRect, data) =>
            {
                var info = new MonitorInfoEx
                {
                    cbSize = Marshal.SizeOf(typeof(MonitorInfoEx)),
                    szDevice = new string('\0', 32)
                };
                if (!GetMonitorInfo(hMonitor, ref info))
                    return false;

                monitors.Add(new MonitorSnapshot
                {
                    Handle = hMonitor.ToInt64(),
                    DeviceName = (info.szDevice ?? string.Empty).TrimEnd('\0'),
                    IsPrimary = (info.dwFlags & 1) != 0,
                    Left = info.rcMonitor.Left,
                    Top = info.rcMonitor.Top,
                    Right = info.rcMonitor.Right,
                    Bottom = info.rcMonitor.Bottom,
                    WorkLeft = info.rcWork.Left,
                    WorkTop = info.rcWork.Top,
                    WorkRight = info.rcWork.Right,
                    WorkBottom = info.rcWork.Bottom
                });
                return true;
            };

            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "EnumDisplayMonitors failed.");
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }

        // GetDpiForMonitor should run in a non-per-monitor-aware thread context.
        IntPtr originalContext = SetThreadDpiAwarenessContext(new IntPtr(-2));
        if (originalContext == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                "Could not establish a system-aware context for monitor DPI queries.");
        try
        {
            foreach (var monitor in monitors)
            {
                uint dpiX, dpiY;
                monitor.DpiHResult = GetDpiForMonitor(new IntPtr(monitor.Handle), 0, out dpiX, out dpiY);
                monitor.DpiX = dpiX;
                monitor.DpiY = dpiY;
            }
        }
        finally
        {
            SetThreadDpiAwarenessContext(originalContext);
        }
        return monitors.ToArray();
    }

    public static IntPtr FindVisibleMascotWindow(int processId, out string diagnostic)
    {
        var found = IntPtr.Zero;
        var seen = new List<string>();
        IntPtr previous = EnterPerMonitorV2Context();
        try
        {
            EnumWindows((hwnd, lParam) =>
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != (uint)processId) return true;

                bool visible = IsWindowVisible(hwnd);
                var title = new StringBuilder(512);
                GetWindowText(hwnd, title, title.Capacity);
                if (GetWindowRect(hwnd, out NativeRect rect))
                {
                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;
                    seen.Add("hwnd=0x" + hwnd.ToInt64().ToString("X") + ", visible=" + visible +
                        ", title='" + title + "', bounds=(" + rect.Left + "," + rect.Top + "," +
                        rect.Right + "," + rect.Bottom + ")");
                    if (visible && title.ToString() == "Layal" && width > 100 && height > 80)
                    {
                        found = hwnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }

        diagnostic = string.Join(" | ", seen);
        return found;
    }

    public static WindowSnapshot ReadWindow(IntPtr hwnd)
    {
        IntPtr previous = EnterPerMonitorV2Context();
        try
        {
            if (!GetWindowRect(hwnd, out NativeRect rect))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");

            uint dpi = GetDpiForWindow(hwnd);
            IntPtr awarenessContext = GetWindowDpiAwarenessContext(hwnd);
            return new WindowSnapshot
            {
                MonitorHandle = MonitorFromWindow(hwnd, 2).ToInt64(),
                Left = rect.Left,
                Top = rect.Top,
                Right = rect.Right,
                Bottom = rect.Bottom,
                Dpi = dpi,
                DpiAwareness = GetAwarenessFromDpiAwarenessContext(awarenessContext)
            };
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }
}
'@

if (-not ('LayalDisplayDpiNative' -as [type])) {
    Add-Type -TypeDefinition $nativeCode -Language CSharp -ErrorAction Stop
}

$script:Checks = New-Object System.Collections.Generic.List[object]
$script:Samples = New-Object System.Collections.Generic.List[object]
$script:FailureCount = 0
$script:AppProcess = $null
$script:MonitorList = @()
$script:WindowHandle = [IntPtr]::Zero
$startedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff zzz")

function Add-Check {
    param([string]$Name,[ValidateSet("passed","failed","info","not-run")][string]$Result,[string]$Detail)
    $row = [pscustomobject]@{
        TimestampLocal=(Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff zzz")
        Check=$Name; Result=$Result; Detail=$Detail
    }
    $script:Checks.Add($row)
    Write-Host ("[{0}] {1}: {2}" -f $Result.ToUpperInvariant(),$Name,$Detail)
    if ($Result -eq "failed") { $script:FailureCount++ }
}

function Get-MonitorByHandle {
    param([long]$Handle)
    return $script:MonitorList | Where-Object { [long]$_.Handle -eq $Handle } | Select-Object -First 1
}

function Get-WindowSample {
    $window = [LayalDisplayDpiNative]::ReadWindow($script:WindowHandle)
    $monitor = Get-MonitorByHandle -Handle ([long]$window.MonitorHandle)
    $device = if ($monitor) { [string]$monitor.DeviceName } else { "UNKNOWN" }
    return [pscustomobject]@{
        TimestampUtc=[DateTime]::UtcNow.ToString("o")
        MonitorHandle=([long]$window.MonitorHandle)
        MonitorDevice=$device
        WindowDpi=[uint]$window.Dpi
        ScalePercent=if ($window.Dpi -gt 0) { [Math]::Round(([double]$window.Dpi / 96.0) * 100.0, 2) } else { $null }
        DpiAwareness=[int]$window.DpiAwareness
        Left=[int]$window.Left
        Top=[int]$window.Top
        Right=[int]$window.Right
        Bottom=[int]$window.Bottom
        Width=([int]$window.Right - [int]$window.Left)
        Height=([int]$window.Bottom - [int]$window.Top)
    }
}

try {
    $script:MonitorList = @([LayalDisplayDpiNative]::EnumerateMonitors())
    if ($script:MonitorList.Count -ge 1) {
        Add-Check "Active display enumeration" "passed" ("Detected {0} monitor(s)." -f $script:MonitorList.Count)
    } else {
        Add-Check "Active display enumeration" "failed" "EnumDisplayMonitors returned no active monitor."
    }

    $invalidGeometry = @($script:MonitorList | Where-Object {
        $_.Right -le $_.Left -or $_.Bottom -le $_.Top -or
        $_.WorkRight -lt $_.WorkLeft -or $_.WorkBottom -lt $_.WorkTop
    })
    if ($invalidGeometry.Count -eq 0 -and $script:MonitorList.Count -gt 0) {
        Add-Check "Monitor geometry" "passed" "Every enumerated monitor has a non-empty physical bounds rectangle."
    } else {
        Add-Check "Monitor geometry" "failed" ("Invalid monitor bounds count: {0}." -f $invalidGeometry.Count)
    }

    $badDpi = @($script:MonitorList | Where-Object {
        $_.DpiHResult -ne 0 -or $_.DpiX -lt 48 -or $_.DpiX -gt 768 -or $_.DpiY -lt 48 -or $_.DpiY -gt 768
    })
    if ($badDpi.Count -eq 0 -and $script:MonitorList.Count -gt 0) {
        $dpiSummary = @($script:MonitorList | ForEach-Object {
            "{0}={1}x{2} DPI ({3}% x {4}%)" -f $_.DeviceName,$_.DpiX,$_.DpiY,
                [Math]::Round(([double]$_.DpiX/96.0)*100.0,1),[Math]::Round(([double]$_.DpiY/96.0)*100.0,1)
        }) -join "; "
        Add-Check "Per-monitor DPI query" "passed" $dpiSummary
    } else {
        Add-Check "Per-monitor DPI query" "failed" ("Monitor(s) with an invalid DPI result: {0}." -f $badDpi.Count)
    }

    if ($script:MonitorList.Count -ge 2) {
        Add-Check "Multi-monitor topology" "passed" ("Detected {0} active monitors; coordinates include negative origins when present." -f $script:MonitorList.Count)
    } elseif ($RequireMultipleMonitors) {
        Add-Check "Multi-monitor topology" "failed" "This run requires at least two active monitors, but Windows exposes fewer than two."
    } else {
        Add-Check "Multi-monitor topology" "info" "Only one active monitor is exposed. Multi-monitor crossing is not certified by this run."
    }

    $script:AppProcess = Start-Process -FilePath $resolvedExe -WorkingDirectory (Split-Path -Parent $resolvedExe) -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $diagnostic = ""
    while ([DateTime]::UtcNow -lt $deadline -and $script:WindowHandle -eq [IntPtr]::Zero) {
        if ($script:AppProcess.HasExited) {
            throw "Layal.exe exited during display-DPI startup with exit code $($script:AppProcess.ExitCode)."
        }
        $script:WindowHandle = [LayalDisplayDpiNative]::FindVisibleMascotWindow($script:AppProcess.Id, [ref]$diagnostic)
        if ($script:WindowHandle -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 150 }
    }
    if ($script:WindowHandle -eq [IntPtr]::Zero) {
        throw "Could not find the visible floating mascot window for PID $($script:AppProcess.Id). Windows: $diagnostic"
    }

    Add-Check "Visible Layal mascot window" "passed" ("Found HWND=0x{0:X} in process {1}." -f $script:WindowHandle.ToInt64(),$script:AppProcess.Id)
    $firstSample = Get-WindowSample
    $script:Samples.Add($firstSample)
    $knownMonitor = Get-MonitorByHandle -Handle $firstSample.MonitorHandle
    if ($null -ne $knownMonitor) {
        Add-Check "Window mapped to active display" "passed" ("Layal currently occupies {0} at {1} DPI ({2}% scale)." -f $firstSample.MonitorDevice,$firstSample.WindowDpi,$firstSample.ScalePercent)
    } else {
        Add-Check "Window mapped to active display" "failed" ("Window monitor handle 0x{0:X} was not in EnumDisplayMonitors results." -f $firstSample.MonitorHandle)
    }

    if ($firstSample.DpiAwareness -eq 2) {
        Add-Check "Layal process window DPI awareness" "passed" "The mascot HWND reports per-monitor DPI awareness. Static validation separately verifies the manifest's PerMonitorV2 declaration."
    } else {
        Add-Check "Layal process window DPI awareness" "failed" ("Expected per-monitor DPI awareness value 2; actual value was {0}." -f $firstSample.DpiAwareness)
    }
    if ($firstSample.WindowDpi -ge 48 -and $firstSample.WindowDpi -le 768 -and $firstSample.Width -gt 0 -and $firstSample.Height -gt 0) {
        Add-Check "Window pixel bounds and DPI" "passed" ("Pixel bounds=({0},{1},{2},{3}), size={4}x{5}, GetDpiForWindow={6} ({7}% scale)." -f
            $firstSample.Left,$firstSample.Top,$firstSample.Right,$firstSample.Bottom,$firstSample.Width,$firstSample.Height,$firstSample.WindowDpi,$firstSample.ScalePercent)
    } else {
        Add-Check "Window pixel bounds and DPI" "failed" "The live window returned an empty rectangle or invalid DPI."
    }

    if ($ObserveSeconds -gt 0) {
        Write-Host ("Observing the Layal mascot window for {0} second(s). Move it between displays now when performing a manual multi-monitor run." -f $ObserveSeconds)
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($timer.Elapsed.TotalSeconds -lt $ObserveSeconds) {
            Start-Sleep -Milliseconds $SampleIntervalMilliseconds
            $sample = Get-WindowSample
            $script:Samples.Add($sample)
        }
        $timer.Stop()
    }

    $uniqueMonitors = @($script:Samples | Select-Object -ExpandProperty MonitorHandle -Unique)
    $uniqueDpis = @($script:Samples | Select-Object -ExpandProperty WindowDpi -Unique)
    $monitorTransitions = 0
    $dpiTransitions = 0
    for ($index = 1; $index -lt $script:Samples.Count; $index++) {
        if ($script:Samples[$index].MonitorHandle -ne $script:Samples[$index - 1].MonitorHandle) { $monitorTransitions++ }
        if ($script:Samples[$index].WindowDpi -ne $script:Samples[$index - 1].WindowDpi) { $dpiTransitions++ }
    }

    if ($RequireMonitorTransition) {
        if ($monitorTransitions -gt 0) {
            Add-Check "Observed cross-monitor movement" "passed" ("Observed {0} monitor transition(s) across {1} display(s)." -f $monitorTransitions,$uniqueMonitors.Count)
        } else {
            Add-Check "Observed cross-monitor movement" "failed" ("No monitor transition was observed. Keep the script running and drag Layal fully onto the second display during the {0}-second observation." -f $ObserveSeconds)
        }
    } elseif ($uniqueMonitors.Count -gt 1) {
        Add-Check "Observed cross-monitor movement" "passed" ("Observed {0} distinct monitor handles." -f $uniqueMonitors.Count)
    } else {
        Add-Check "Observed cross-monitor movement" "info" "No monitor crossing was observed during this run."
    }

    if ($RequireDpiTransition) {
        if ($uniqueDpis.Count -gt 1) {
            Add-Check "Observed DPI transition" "passed" ("Window DPI changed across samples: {0}." -f (@($uniqueDpis | Sort-Object) -join ", "))
        } else {
            Add-Check "Observed DPI transition" "failed" ("The Layal HWND DPI did not change during observation. Test with two displays configured at different Windows scaling values; samples={0}." -f (@($uniqueDpis) -join ", "))
        }
    } elseif ($uniqueDpis.Count -gt 1) {
        Add-Check "Observed DPI transition" "passed" ("Window DPI changed across samples: {0}." -f (@($uniqueDpis | Sort-Object) -join ", "))
    } else {
        Add-Check "Observed DPI transition" "info" "No DPI transition was observed. The CI runner is generally a single-display smoke environment."
    }

    Add-Check "Display topology evidence captured" "passed" ("Writing {0} window sample(s), {1} monitor record(s), and coordinate/DPI diagnostics." -f $script:Samples.Count,$script:MonitorList.Count)
}
catch {
    Add-Check "Display/DPI diagnostic runtime" "failed" $_.Exception.ToString()
}
finally {
    if ($script:AppProcess -and -not $script:AppProcess.HasExited) {
        try { Stop-Process -Id $script:AppProcess.Id -Force -ErrorAction SilentlyContinue } catch {}
        try { [void]$script:AppProcess.WaitForExit(5000) } catch {}
    }

    $monitorOutput = @($script:MonitorList | ForEach-Object {
        [pscustomobject]@{
            Handle=("0x{0:X}" -f [long]$_.Handle)
            DeviceName=$_.DeviceName
            IsPrimary=$_.IsPrimary
            MonitorBoundsPixels=[pscustomobject]@{Left=$_.Left;Top=$_.Top;Right=$_.Right;Bottom=$_.Bottom;Width=($_.Right-$_.Left);Height=($_.Bottom-$_.Top)}
            WorkAreaPixels=[pscustomobject]@{Left=$_.WorkLeft;Top=$_.WorkTop;Right=$_.WorkRight;Bottom=$_.WorkBottom;Width=($_.WorkRight-$_.WorkLeft);Height=($_.WorkBottom-$_.WorkTop)}
            EffectiveDpi=[pscustomobject]@{X=$_.DpiX;Y=$_.DpiY;ScalePercentX=if ($_.DpiX -gt 0) {[Math]::Round(([double]$_.DpiX/96.0)*100.0,2)} else {$null};ScalePercentY=if ($_.DpiY -gt 0) {[Math]::Round(([double]$_.DpiY/96.0)*100.0,2)} else {$null};HResult=$_.DpiHResult}
        }
    })

    $capturedSamples = $script:Samples.ToArray()
    if ($capturedSamples.Count -gt 0) {
        $capturedSamples | Export-Csv -LiteralPath $csvPath -NoTypeInformation
    }
    $uniqueMonitorHandles = @($capturedSamples | Select-Object -ExpandProperty MonitorHandle -Unique)
    $uniqueWindowDpis = @($capturedSamples | Select-Object -ExpandProperty WindowDpi -Unique)
    $capturedChecks = $script:Checks.ToArray()
    $failRows = @($capturedChecks | Where-Object { $_.Result -eq "failed" })
    $resultLabel = if ($failRows.Count -eq 0 -and $script:Checks.Count -gt 0) { "passed" } else { "failed" }
    $monitorTransitionCount = 0
    $dpiTransitionCount = 0
    for ($index = 1; $index -lt $capturedSamples.Count; $index++) {
        if ($capturedSamples[$index].MonitorHandle -ne $capturedSamples[$index - 1].MonitorHandle) { $monitorTransitionCount++ }
        if ($capturedSamples[$index].WindowDpi -ne $capturedSamples[$index - 1].WindowDpi) { $dpiTransitionCount++ }
    }
    # "Result" reports whether checks required by this invocation failed.
    # The CI smoke run does not require two physical displays and cannot certify
    # cross-monitor behavior. This gate only certifies that monitor/DPI transitions
    # were observed; visual clipping and interactive acceptance remain a separate manual gate.
    $topologyDpiGateSatisfied = (
        $script:MonitorList.Count -ge 2 -and
        $uniqueMonitorHandles.Count -ge 2 -and
        $uniqueWindowDpis.Count -ge 2 -and
        $monitorTransitionCount -gt 0 -and
        $dpiTransitionCount -gt 0 -and
        $failRows.Count -eq 0
    )
    $topologyDpiGateStatus = if ($failRows.Count -gt 0) {
        "failed"
    } elseif ($topologyDpiGateSatisfied) {
        "passed"
    } else {
        "not-certified"
    }
    $topologyDpiGateDetail = if ($failRows.Count -gt 0) {
        "One or more checks required by this run failed; inspect Checks for details."
    } elseif ($topologyDpiGateSatisfied) {
        "Two or more monitors and actual monitor and window-DPI transitions were observed. Visual and interactive acceptance remains separate."
    } elseif ($script:MonitorList.Count -lt 2) {
        "Only one active monitor was detected; this run cannot certify multi-monitor behavior."
    } elseif ($uniqueMonitorHandles.Count -lt 2 -or $monitorTransitionCount -eq 0) {
        "Multiple monitors may be configured, but no actual cross-monitor transition was recorded."
    } elseif ($uniqueWindowDpis.Count -lt 2 -or $dpiTransitionCount -eq 0) {
        "No actual window-DPI transition was recorded; use displays with different Windows scale factors."
    } else {
        "The monitor and DPI transition conditions were not fully met."
    }

    $summary = [pscustomobject]@{
        TestSuite="Layal Windows display/DPI topology diagnostic"
        CreatedAtLocal=(Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff zzz")
        StartedAtLocal=$startedAt
        Executable=$resolvedExe
        ProcessId=if ($script:AppProcess) { $script:AppProcess.Id } else { $null }
        Result=$resultLabel
        TopologyDpiGateStatus=$topologyDpiGateStatus
        TopologyDpiGateDetail=$topologyDpiGateDetail
        TopologyDpiTransitionsObserved=[bool]$topologyDpiGateSatisfied
        ManualVisualAcceptanceStatus="pending"
        TopologyDpiEvidenceCriteria=[pscustomobject]@{
            AtLeastTwoMonitorsDetected=($script:MonitorList.Count -ge 2)
            AtLeastTwoMonitorsObservedInSamples=($uniqueMonitorHandles.Count -ge 2)
            MonitorTransitionObserved=($monitorTransitionCount -gt 0)
            AtLeastTwoWindowDpisObserved=($uniqueWindowDpis.Count -ge 2)
            WindowDpiTransitionObserved=($dpiTransitionCount -gt 0)
        }
        ObserveSeconds=$ObserveSeconds
        RequireMultipleMonitors=[bool]$RequireMultipleMonitors
        RequireMonitorTransition=[bool]$RequireMonitorTransition
        RequireDpiTransition=[bool]$RequireDpiTransition
        MonitorCount=$script:MonitorList.Count
        MonitorRecords=$monitorOutput
        WindowSampleCount=$capturedSamples.Count
        ObservedMonitorHandleCount=$uniqueMonitorHandles.Count
        ObservedWindowDpis=@($uniqueWindowDpis | Sort-Object)
        MonitorTransitionCount=$monitorTransitionCount
        DpiTransitionCount=$dpiTransitionCount
        Samples=$capturedSamples
        Checks=$capturedChecks
        Scope="This script records monitor topology and observes actual monitor/DPI transitions. A passing topology/DPI gate does not certify clipping, appearance, mouse behavior, or all manual acceptance criteria. Physical testing requires running this script on the target PC with RequireMonitorTransition and RequireDpiTransition."
    }
    [IO.File]::WriteAllText($jsonPath,(ConvertTo-Json -InputObject $summary -Depth 8),(New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("Display/DPI smoke result: {0}; monitor/DPI transition gate: {1} ({2}); manual visual acceptance: pending. JSON: {3}; CSV: {4}" -f $resultLabel,$topologyDpiGateStatus,$topologyDpiGateDetail,$jsonPath,$csvPath)
}

$finalFailures = @($script:Checks.ToArray() | Where-Object { $_.Result -eq "failed" })
if ($finalFailures.Count -gt 0) {
    exit 1
}
Write-Host ("Layal display/DPI topology diagnostic passed with {0} check(s)." -f $script:Checks.Count)
