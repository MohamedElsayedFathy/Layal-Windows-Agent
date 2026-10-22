[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$LayalExePath,
    [string]$LogDirectory = "artifacts/interactive-acceptance",
    [int]$StartupTimeoutSeconds = 25,
    [switch]$CalibrationOnly
)

$ErrorActionPreference = "Stop"
$resolvedExe = (Resolve-Path -LiteralPath $LayalExePath).Path
if ([IO.Path]::GetFileName($resolvedExe) -ine "Layal.exe") { throw "Expected the actual Layal.exe release executable." }
if (Get-Process -Name "Layal" -ErrorAction SilentlyContinue) { throw "A Layal process is already running. Close it before running this isolated integration test." }

$resolvedLogDirectory = [IO.Path]::GetFullPath($LogDirectory)
New-Item -ItemType Directory -Path $resolvedLogDirectory -Force | Out-Null
$csvPath = Join-Path $resolvedLogDirectory "native-click-through.csv"
$jsonPath = Join-Path $resolvedLogDirectory "native-click-through-summary.json"
Remove-Item -LiteralPath $csvPath, $jsonPath -Force -ErrorAction SilentlyContinue

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$nativeCode = @'
using System;
using System.Runtime.InteropServices;
public static class LayalNativeInput
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint type; public MouseInput mi; }

    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll", SetLastError=true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll", SetLastError=true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError=true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError=true)]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW", SetLastError=true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint="GetWindowLongW", SetLastError=true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError=true)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError=true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private const uint SwpNoActivate=0x0010, InputMouse=0, MouseMove=0x0001, LeftDown=0x0002, LeftUp=0x0004;
    public static uint ProcessIdForWindow(IntPtr hwnd) { GetWindowThreadProcessId(hwnd, out uint pid); return pid; }
    public static bool Visible(IntPtr hwnd) => IsWindowVisible(hwnd);
    public static long ExtendedStyle(IntPtr hwnd) => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, -20).ToInt64() : GetWindowLong32(hwnd, -20);
    public static string DescribeWindowAt(int x, int y) {
        IntPtr hwnd = WindowFromPoint(new NativePoint { X=x, Y=y });
        if (hwnd == IntPtr.Zero) return "HWND=0 (desktop/no window)";
        var title = new System.Text.StringBuilder(512);
        GetWindowText(hwnd, title, title.Capacity);
        GetWindowThreadProcessId(hwnd, out uint pid);
        long style = ExtendedStyle(hwnd);
        return "HWND=0x" + hwnd.ToInt64().ToString("X") + ", PID=" + pid + ", title='" + title + "', exStyle=0x" + style.ToString("X");
    }
    public static IntPtr FindVisibleLayalWindowForProcess(int processId, out string diagnostic) {
        IntPtr found = IntPtr.Zero;
        var seen = new System.Collections.Generic.List<string>();
        EnumWindows((hwnd, lParam) => {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)processId) {
                bool isVisible = IsWindowVisible(hwnd);
                var title = new System.Text.StringBuilder(512);
                GetWindowText(hwnd, title, title.Capacity);
                int[] bounds = null;
                if (GetWindowRect(hwnd, out Rect rect))
                    bounds = new int[] { rect.Left, rect.Top, rect.Right, rect.Bottom };
                seen.Add("hwnd=0x" + hwnd.ToInt64().ToString("X") + ", visible=" + isVisible +
                    ", title='" + title + "', bounds=" + (bounds == null ? "unknown" : string.Join(",", bounds)));
                if (isVisible && title.ToString() == "Layal" && bounds != null &&
                    bounds[2]-bounds[0] > 100 && bounds[3]-bounds[1] > 80) {
                    found = hwnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        diagnostic = string.Join(" | ", seen);
        return found;
    }
    public static int[] GetRect(IntPtr hwnd) {
        if (!GetWindowRect(hwnd, out Rect r)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
        return new int[] { r.Left, r.Top, r.Right, r.Bottom };
    }
    public static void MoveTopMost(IntPtr hwnd, int x, int y) {
        if (!GetWindowRect(hwnd, out Rect r)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed before move.");
        if (!SetWindowPos(hwnd, new IntPtr(-1), x, y, r.Right-r.Left, r.Bottom-r.Top, SwpNoActivate))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos failed.");
    }
    public static void SetCursor(int x, int y) {
        if (!SetCursorPos(x,y)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetCursorPos failed.");
    }
    private static uint SendButton(uint flag) {
        Input input = new Input { type=InputMouse, mi=new MouseInput { dwFlags=flag } };
        return SendInput(1, new Input[] { input }, Marshal.SizeOf(typeof(Input)));
    }
    public static uint MoveRelative(int dx, int dy) {
        Input input = new Input { type=InputMouse, mi=new MouseInput { dx=dx, dy=dy, dwFlags=MouseMove } };
        return SendInput(1, new Input[] { input }, Marshal.SizeOf(typeof(Input)));
    }
    public static uint ClickLeft() {
        Input[] inputs = new Input[2];
        inputs[0] = new Input { type=InputMouse, mi=new MouseInput { dwFlags=LeftDown } };
        inputs[1] = new Input { type=InputMouse, mi=new MouseInput { dwFlags=LeftUp } };
        return SendInput(2, inputs, Marshal.SizeOf(typeof(Input)));
    }
    public static uint LeftButtonDown() => SendButton(LeftDown);
    public static uint LeftButtonUp() => SendButton(LeftUp);
}
'@
Add-Type -TypeDefinition $nativeCode -Language CSharp -ErrorAction Stop

$script:Rows = New-Object System.Collections.Generic.List[object]
$script:UnderlyingClicks = 0
$script:TargetButton = $null
$script:AppProcess = $null
$script:MascotHwnd = [IntPtr]::Zero
$script:MouseButtonHeld = $false
$script:FailureCount = 0

function Write-Result {
    param([string]$Name,[string]$Status,[string]$Detail)
    $row = [pscustomobject]@{
        TimestampLocal=(Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff zzz")
        Test=$Name; Status=$Status; Detail=$Detail; UnderlyingClicks=$script:UnderlyingClicks
    }
    $script:Rows.Add($row)
    $append = [IO.File]::Exists($csvPath)
    $row | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Append:$append
    Write-Host ("[{0}] {1}: {2}" -f $Status.ToUpperInvariant(),$Name,$Detail)
    if ($Status -eq "failed") { $script:FailureCount++ }
}
function Pump-Messages {
    param([int]$Milliseconds)
    $until=[DateTime]::UtcNow.AddMilliseconds($Milliseconds)
    while ([DateTime]::UtcNow -lt $until) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 20
    }
    [System.Windows.Forms.Application]::DoEvents()
}
function Get-MascotRect {
    $rect=[LayalNativeInput]::GetRect($script:MascotHwnd)
    return @{Left=$rect[0];Top=$rect[1];Width=($rect[2]-$rect[0]);Height=($rect[3]-$rect[1]);Right=$rect[2];Bottom=$rect[3]}
}
function Get-ScaledPoint {
    param([double]$DesignX,[double]$DesignY)
    $r=Get-MascotRect
    return @{X=[int][Math]::Round($r.Left+($DesignX/248.0)*$r.Width);Y=[int][Math]::Round($r.Top+($DesignY/175.0)*$r.Height)}
}
function Move-CursorAndPump {
    param([hashtable]$Point,[int]$WaitMilliseconds=400)
    [LayalNativeInput]::SetCursor($Point.X,$Point.Y)
    # SetCursorPos sets the location, but does not provide an explicit raw mouse-move
    # input event. Nudge with SendInput so WPF and Win32 both process real move messages.
    if ([LayalNativeInput]::MoveRelative(1,0) -ne 1) { throw "Could not inject native mouse-move event." }
    Pump-Messages 35
    if ([LayalNativeInput]::MoveRelative(-1,0) -ne 1) { throw "Could not inject native return mouse-move event." }
    Pump-Messages $WaitMilliseconds
}
function Send-LeftClick {
    $sent=[LayalNativeInput]::ClickLeft()
    if ($sent -ne 2) { throw "SendInput inserted $sent of 2 mouse events (Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))." }
    Pump-Messages 500
}
function Invoke-NativeAcceptanceCase {
    param([string]$Name,[scriptblock]$Action)
    try { & $Action } catch { Write-Result -Name $Name -Status "failed" -Detail $_.Exception.Message }
}

$form=New-Object System.Windows.Forms.Form
$form.Text="Layal native click-through integration surface"
$form.WindowState=[System.Windows.Forms.FormWindowState]::Maximized
$form.StartPosition=[System.Windows.Forms.FormStartPosition]::CenterScreen
$form.TopMost=$true
$form.ShowInTaskbar=$true
$form.KeyPreview=$true
$form.BackColor=[System.Drawing.Color]::FromArgb(20,27,42)
$form.ForeColor=[System.Drawing.Color]::FromArgb(238,243,255)

$root=New-Object System.Windows.Forms.TableLayoutPanel
$root.Dock=[System.Windows.Forms.DockStyle]::Fill
$root.ColumnCount=1
$root.RowCount=2
$root.BackColor=$form.BackColor
$headerRow=New-Object System.Windows.Forms.RowStyle
$headerRow.SizeType=[System.Windows.Forms.SizeType]::Absolute
$headerRow.Height=100
$contentRow=New-Object System.Windows.Forms.RowStyle
$contentRow.SizeType=[System.Windows.Forms.SizeType]::Percent
$contentRow.Height=100
[void]$root.RowStyles.Add($headerRow)
[void]$root.RowStyles.Add($contentRow)

$header=New-Object System.Windows.Forms.Label
$header.Dock=[System.Windows.Forms.DockStyle]::Fill
$header.TextAlign=[System.Drawing.ContentAlignment]::MiddleCenter
$header.Font=New-Object System.Drawing.Font("Segoe UI",16,[System.Drawing.FontStyle]::Bold)
$header.ForeColor=[System.Drawing.Color]::FromArgb(130,222,232)
$header.BackColor=[System.Drawing.Color]::FromArgb(28,37,57)
$header.Text=("Native Windows mouse acceptance surface"+[Environment]::NewLine+"Click targets below must receive clicks through Layal's transparent padding.")

$grid=New-Object System.Windows.Forms.TableLayoutPanel
$grid.Dock=[System.Windows.Forms.DockStyle]::Fill
$grid.RowCount=3
$grid.ColumnCount=3
$grid.Padding=New-Object System.Windows.Forms.Padding(20)
$grid.CellBorderStyle=[System.Windows.Forms.TableLayoutPanelCellBorderStyle]::Single
for ($i=0;$i -lt 3;$i++) {
    $column=New-Object System.Windows.Forms.ColumnStyle
    $column.SizeType=[System.Windows.Forms.SizeType]::Percent
    $column.Width=33.333
    [void]$grid.ColumnStyles.Add($column)
    $row=New-Object System.Windows.Forms.RowStyle
    $row.SizeType=[System.Windows.Forms.SizeType]::Percent
    $row.Height=33.333
    [void]$grid.RowStyles.Add($row)
}
for ($r=0;$r -lt 3;$r++) {
    for ($c=0;$c -lt 3;$c++) {
        $id="R{0}C{1}" -f ($r+1),($c+1)
        $button=New-Object System.Windows.Forms.Button
        $button.Dock=[System.Windows.Forms.DockStyle]::Fill
        $button.Margin=New-Object System.Windows.Forms.Padding(7)
        $button.FlatStyle=[System.Windows.Forms.FlatStyle]::Flat
        $button.BackColor=[System.Drawing.Color]::FromArgb(38,49,74)
        $button.ForeColor=[System.Drawing.Color]::FromArgb(238,243,255)
        $button.Font=New-Object System.Drawing.Font("Segoe UI",18,[System.Drawing.FontStyle]::Bold)
        $button.UseVisualStyleBackColor=$false
        $button.Tag=@{Name=$id;Hits=0}
        $button.Text=$id+[Environment]::NewLine+"Hits: 0"
        $button.Add_Click({
            param($sender,$eventArgs)
            $sender.Tag.Hits=[int]$sender.Tag.Hits+1
            $script:UnderlyingClicks++
            $sender.Text=$sender.Tag.Name+[Environment]::NewLine+"Hits: "+$sender.Tag.Hits
        })
        [void]$grid.Controls.Add($button,$c,$r)
        if ($r -eq 1 -and $c -eq 1) { $script:TargetButton=$button }
    }
}
[void]$root.Controls.Add($header,0,0)
[void]$root.Controls.Add($grid,0,1)
[void]$form.Controls.Add($root)

try {
    $form.Show()
    [System.Windows.Forms.Application]::DoEvents()
    $script:AppProcess=Start-Process -FilePath $resolvedExe -WorkingDirectory (Split-Path -Parent $resolvedExe) -PassThru
    $deadline=[DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and $script:MascotHwnd -eq [IntPtr]::Zero) {
        Pump-Messages 100
        if ($script:AppProcess.HasExited) { throw "Layal.exe exited during startup with exit code $($script:AppProcess.ExitCode)." }
        $windowDiagnostic=$null
        $hwnd=[LayalNativeInput]::FindVisibleLayalWindowForProcess($script:AppProcess.Id,[ref]$windowDiagnostic)
        if ($hwnd -ne [IntPtr]::Zero) { $script:MascotHwnd=$hwnd }
    }
    if ($script:MascotHwnd -eq [IntPtr]::Zero) {
        throw "Could not find a visible floating Layal window for launched PID $($script:AppProcess.Id). Process windows: $windowDiagnostic"
    }
    if (-not [LayalNativeInput]::Visible($script:MascotHwnd)) { throw "Selected Layal mascot window became hidden after enumeration." }

    $buttonCenter=$script:TargetButton.PointToScreen([System.Drawing.Point]::new([int]($script:TargetButton.Width/2),[int]($script:TargetButton.Height/2)))
    $initialRect=Get-MascotRect

    # Validate the WinForms target and SendInput path before testing pass-through. Move
    # Layal away from the target first so a failure here cannot be mistaken for app failure.
    [LayalNativeInput]::MoveTopMost($script:MascotHwnd,10,10)
    Pump-Messages 400
    $baselineBefore=$script:UnderlyingClicks
    $baselinePoint=@{X=$buttonCenter.X;Y=$buttonCenter.Y}
    Move-CursorAndPump $baselinePoint 300
    $baselineTarget=[LayalNativeInput]::DescribeWindowAt($baselinePoint.X,$baselinePoint.Y)
    Send-LeftClick
    if ($script:UnderlyingClicks -ne ($baselineBefore+1)) {
        throw "Native input calibration failed before overlay tests. Expected underlying count $($baselineBefore+1), got $script:UnderlyingClicks. Target=$baselineTarget"
    }
    Write-Result "Underlying target receives native click with mascot moved away" "passed" "Calibration click incremented the WinForms target. WindowFromPoint=$baselineTarget."

    if (-not $CalibrationOnly) {
        [LayalNativeInput]::MoveTopMost($script:MascotHwnd,$buttonCenter.X-[int]($initialRect.Width/2),$buttonCenter.Y-[int]($initialRect.Height/2))
        Pump-Messages 500

        Invoke-NativeAcceptanceCase "Transparent upper-left padding reaches underlying button" {
        $before=$script:UnderlyingClicks
        $point=Get-ScaledPoint -DesignX 8 -DesignY 8
        Move-CursorAndPump $point 450
        $nativeTarget=[LayalNativeInput]::DescribeWindowAt($point.X,$point.Y)
        $exStyle=[LayalNativeInput]::ExtendedStyle($script:MascotHwnd)
        Send-LeftClick
        if ($script:UnderlyingClicks -ne ($before+1)) {
            throw "Expected underlying click count $($before+1), got $script:UnderlyingClicks. WindowFromPoint diagnostic=$nativeTarget; LayalExStyle=0x$($exStyle.ToString('X')). Note: WindowFromPoint is not authoritative for HTTRANSPARENT layered windows."
        }
        Write-Result "Transparent upper-left padding reaches underlying button" "passed" "Native click incremented the underlying button. WindowFromPoint diagnostic=$nativeTarget."
    }

    Invoke-NativeAcceptanceCase "Visible mascot receives click without underlying activation" {
        $before=$script:UnderlyingClicks
        $point=Get-ScaledPoint -DesignX 124 -DesignY 80
        Move-CursorAndPump $point 650
        $nativeTarget=[LayalNativeInput]::DescribeWindowAt($point.X,$point.Y)
        if ($nativeTarget -notmatch ("PID=" + $script:AppProcess.Id + ", title='Layal'")) {
            throw "The visible mascot is not the native hit-test target at its core. Cursor=($($point.X),$($point.Y)); $nativeTarget"
        }
        Send-LeftClick
        if ($script:UnderlyingClicks -ne $before) { throw "Mascot click leaked through: expected $before underlying clicks, got $script:UnderlyingClicks. Native target before click: $nativeTarget" }
        Write-Result "Visible mascot receives click without underlying activation" "passed" "WindowFromPoint targeted the actual Layal HWND and the underlying button count stayed unchanged."
    }

    Invoke-NativeAcceptanceCase "Transparent bottom padding reaches underlying button" {
        $before=$script:UnderlyingClicks
        $point=Get-ScaledPoint -DesignX 124 -DesignY 160
        Move-CursorAndPump $point 450
        $nativeTarget=[LayalNativeInput]::DescribeWindowAt($point.X,$point.Y)
        $exStyle=[LayalNativeInput]::ExtendedStyle($script:MascotHwnd)
        Send-LeftClick
        if ($script:UnderlyingClicks -ne ($before+1)) {
            throw "Expected underlying click count $($before+1), got $script:UnderlyingClicks. WindowFromPoint diagnostic=$nativeTarget; LayalExStyle=0x$($exStyle.ToString('X')). Note: WindowFromPoint is not authoritative for HTTRANSPARENT layered windows."
        }
        Write-Result "Transparent bottom padding reaches underlying button" "passed" "Native click incremented the underlying button. WindowFromPoint diagnostic=$nativeTarget."
    }

    Invoke-NativeAcceptanceCase "Drag moves mascot without clicking through" {
        $beforeClicks=$script:UnderlyingClicks
        $beforeRect=Get-MascotRect
        $anchor=Get-ScaledPoint -DesignX 124 -DesignY 80
        Move-CursorAndPump $anchor 450
        $downSent=[LayalNativeInput]::LeftButtonDown()
        if ($downSent -ne 1) { throw "Could not inject native left-button down event." }
        $script:MouseButtonHeld=$true
        Pump-Messages 120
        $moveSent=[LayalNativeInput]::MoveRelative(42,28)
        if ($moveSent -ne 1) { throw "Could not inject native mouse-move while dragging." }
        Pump-Messages 300
        $upSent=[LayalNativeInput]::LeftButtonUp()
        if ($upSent -ne 1) { throw "Could not inject native left-button up event." }
        $script:MouseButtonHeld=$false
        Pump-Messages 450
        $afterRect=Get-MascotRect
        $movedX=[Math]::Abs($afterRect.Left-$beforeRect.Left)
        $movedY=[Math]::Abs($afterRect.Top-$beforeRect.Top)
        if ($movedX -lt 15 -and $movedY -lt 15) { throw "Expected mascot movement after native drag; delta was ($movedX,$movedY) pixels." }
        if ($script:UnderlyingClicks -ne $beforeClicks) { throw "Drag leaked input to underlying target; count changed from $beforeClicks to $script:UnderlyingClicks." }
        Write-Result "Drag moves mascot without clicking through" "passed" "Native drag moved actual mascot window by ($movedX,$movedY) px."
        }
    }
}
catch {
    Write-Result -Name "Integration test setup/runtime" -Status "failed" -Detail $_.Exception.ToString()
}
finally {
    if ($script:MouseButtonHeld) { try { [void][LayalNativeInput]::LeftButtonUp() } catch {} }
    if ($script:AppProcess -and -not $script:AppProcess.HasExited) {
        try { Stop-Process -Id $script:AppProcess.Id -Force -ErrorAction SilentlyContinue } catch {}
        try { $script:AppProcess.WaitForExit(5000) } catch {}
    }
    if ($form -and -not $form.IsDisposed) { try { [void]$form.Close(); $form.Dispose() } catch {} }

    $checks = $script:Rows.ToArray()
    $passedRows = @($checks | Where-Object { $_.Status -eq "passed" })
    $failedRows = @($checks | Where-Object { $_.Status -eq "failed" })
    $minimumChecks = if ($CalibrationOnly) { 1 } else { 4 }
    $resultLabel = "failed"
    if ($script:FailureCount -eq 0 -and $checks.Length -ge $minimumChecks) { $resultLabel = "passed" }
    $summary=[pscustomobject]@{
        TestSuite="Layal native Windows desktop click-through integration"
        CreatedAtLocal=(Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff zzz")
        Executable=$resolvedExe
        Mode=if ($CalibrationOnly) { "calibration-only" } else { "full-interactive-acceptance" }
        Result=$resultLabel
        Passed=$passedRows.Count
        Failed=$failedRows.Count
        UnderlyingClicksObserved=$script:UnderlyingClicks
        Checks=$checks
        Scope="Actual Layal WPF window and native Windows mouse input in the CI desktop session; not a physical multi-monitor DPI certification."
    }
    $jsonText = ConvertTo-Json -InputObject $summary -Depth 6
    [IO.File]::WriteAllText($jsonPath, $jsonText, (New-Object System.Text.UTF8Encoding($false)))
}
$minimumChecks = if ($CalibrationOnly) { 1 } else { 4 }
if ($script:FailureCount -gt 0 -or $script:Rows.Count -lt $minimumChecks) {
    Write-Error "Native desktop click-through integration failed. See $jsonPath and $csvPath"
    exit 1
}
Write-Host "Native desktop click-through integration passed ($($script:Rows.Count) check(s)). Summary: $jsonPath"
