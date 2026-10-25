[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$LayalExePath,

    [string]$LogPath = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)) ("Layal-click-through-{0}.csv" -f (Get-Date -Format "yyyyMMdd-HHmmss")))
)

$ErrorActionPreference = "Stop"

$resolvedExe = (Resolve-Path -LiteralPath $LayalExePath).Path
if ([IO.Path]::GetExtension($resolvedExe) -ine ".exe") {
    throw "LayalExePath must point to Layal.exe."
}

$resolvedLogPath = [IO.Path]::GetFullPath($LogPath)
$logDirectory = Split-Path -Parent $resolvedLogPath
if ([string]::IsNullOrWhiteSpace($logDirectory)) {
    $logDirectory = (Get-Location).Path
    $resolvedLogPath = Join-Path $logDirectory $resolvedLogPath
}
if (Test-Path -LiteralPath $resolvedLogPath) {
    throw "The log file already exists. Choose a new -LogPath to avoid overwriting evidence: $resolvedLogPath"
}
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$script:AcceptanceLogPath = $resolvedLogPath
$script:TotalRoutedClicks = 0
$script:AcceptanceStatusLabel = $null

function Write-ClickThroughLog {
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$EventName
    )

    $cursor = [System.Windows.Forms.Cursor]::Position
    $row = [pscustomobject]@{
        TimestampLocal = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.fff zzz")
        Event = $EventName
        Target = $Target
        TotalRoutedClicks = $script:TotalRoutedClicks
        CursorX = $cursor.X
        CursorY = $cursor.Y
    }

    if (Test-Path -LiteralPath $script:AcceptanceLogPath) {
        $row | Export-Csv -LiteralPath $script:AcceptanceLogPath -NoTypeInformation -Append
    }
    else {
        $row | Export-Csv -LiteralPath $script:AcceptanceLogPath -NoTypeInformation
    }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = "Layal - Click-through acceptance surface"
$form.WindowState = [System.Windows.Forms.FormWindowState]::Maximized
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
$form.TopMost = $false
$form.ShowInTaskbar = $true
$form.KeyPreview = $true
$form.BackColor = [System.Drawing.Color]::FromArgb(20, 27, 42)
$form.ForeColor = [System.Drawing.Color]::FromArgb(238, 243, 255)
$form.MinimumSize = New-Object System.Drawing.Size(800, 600)

$root = New-Object System.Windows.Forms.TableLayoutPanel
$root.Dock = [System.Windows.Forms.DockStyle]::Fill
$root.ColumnCount = 1
$root.RowCount = 2
$root.Margin = New-Object System.Windows.Forms.Padding(0)
$root.Padding = New-Object System.Windows.Forms.Padding(0)
$root.BackColor = $form.BackColor

$headerRow = New-Object System.Windows.Forms.RowStyle
$headerRow.SizeType = [System.Windows.Forms.SizeType]::Absolute
$headerRow.Height = 110
[void]$root.RowStyles.Add($headerRow)

$contentRow = New-Object System.Windows.Forms.RowStyle
$contentRow.SizeType = [System.Windows.Forms.SizeType]::Percent
$contentRow.Height = 100
[void]$root.RowStyles.Add($contentRow)

$header = New-Object System.Windows.Forms.Panel
$header.Dock = [System.Windows.Forms.DockStyle]::Fill
$header.BackColor = [System.Drawing.Color]::FromArgb(28, 37, 57)

$title = New-Object System.Windows.Forms.Label
$title.Text = "Layal click-through acceptance"
$title.Location = New-Object System.Drawing.Point(18, 10)
$title.Size = New-Object System.Drawing.Size(600, 32)
$title.Font = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
$title.ForeColor = [System.Drawing.Color]::FromArgb(130, 222, 232)

$instructions = New-Object System.Windows.Forms.Label
$instructions.Text = "Move Layal over this window. Click its transparent padding: the target underneath must count the click. Click the visible mascot: the count must stay unchanged. Close this window when finished."
$instructions.Location = New-Object System.Drawing.Point(20, 48)
$instructions.Size = New-Object System.Drawing.Size(1100, 48)
$instructions.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Left -bor [System.Windows.Forms.AnchorStyles]::Right
$instructions.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Regular)
$instructions.ForeColor = [System.Drawing.Color]::FromArgb(221, 229, 245)

$status = New-Object System.Windows.Forms.Label
$status.Text = "Routed clicks received: 0"
$status.Location = New-Object System.Drawing.Point(700, 15)
$status.Size = New-Object System.Drawing.Size(520, 30)
$status.Anchor = [System.Windows.Forms.AnchorStyles]::Top -bor [System.Windows.Forms.AnchorStyles]::Right
$status.TextAlign = [System.Drawing.ContentAlignment]::MiddleRight
$status.Font = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
$status.ForeColor = [System.Drawing.Color]::FromArgb(255, 209, 125)
$script:AcceptanceStatusLabel = $status

[void]$header.Controls.Add($title)
[void]$header.Controls.Add($instructions)
[void]$header.Controls.Add($status)

$targets = New-Object System.Windows.Forms.TableLayoutPanel
$targets.Dock = [System.Windows.Forms.DockStyle]::Fill
$targets.ColumnCount = 3
$targets.RowCount = 3
$targets.Padding = New-Object System.Windows.Forms.Padding(12)
$targets.Margin = New-Object System.Windows.Forms.Padding(0)
$targets.BackColor = [System.Drawing.Color]::FromArgb(20, 27, 42)
$targets.CellBorderStyle = [System.Windows.Forms.TableLayoutPanelCellBorderStyle]::Single

for ($i = 0; $i -lt 3; $i++) {
    $columnStyle = New-Object System.Windows.Forms.ColumnStyle
    $columnStyle.SizeType = [System.Windows.Forms.SizeType]::Percent
    $columnStyle.Width = 33.333
    [void]$targets.ColumnStyles.Add($columnStyle)

    $rowStyle = New-Object System.Windows.Forms.RowStyle
    $rowStyle.SizeType = [System.Windows.Forms.SizeType]::Percent
    $rowStyle.Height = 33.333
    [void]$targets.RowStyles.Add($rowStyle)
}

for ($row = 0; $row -lt 3; $row++) {
    for ($column = 0; $column -lt 3; $column++) {
        $targetId = "R{0}C{1}" -f ($row + 1), ($column + 1)
        $button = New-Object System.Windows.Forms.Button
        $button.Dock = [System.Windows.Forms.DockStyle]::Fill
        $button.Margin = New-Object System.Windows.Forms.Padding(6)
        $button.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
        $button.FlatAppearance.BorderSize = 1
        $button.BackColor = [System.Drawing.Color]::FromArgb(38, 49, 74)
        $button.ForeColor = [System.Drawing.Color]::FromArgb(238, 243, 255)
        $button.Font = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
        $button.UseVisualStyleBackColor = $false
        $button.Tag = @{ Target = $targetId; Hits = 0 }
        $button.Text = "$targetId$([Environment]::NewLine)Hits: 0"

        $button.Add_Click({
            param($sender, $eventArgs)
            $sender.Tag["Hits"] = [int]$sender.Tag["Hits"] + 1
            $script:TotalRoutedClicks++
            $sender.Text = "{0}{1}Hits: {2}" -f $sender.Tag["Target"], [Environment]::NewLine, $sender.Tag["Hits"]
            $script:AcceptanceStatusLabel.Text = "Routed clicks received: $script:TotalRoutedClicks"
            Write-ClickThroughLog -Target $sender.Tag["Target"] -EventName "UnderlyingButtonClick"
        })

        [void]$targets.Controls.Add($button, $column, $row)
    }
}

[void]$root.Controls.Add($header, 0, 0)
[void]$root.Controls.Add($targets, 0, 1)
[void]$form.Controls.Add($root)

$form.Add_Shown({
    try {
        Start-Process -FilePath $resolvedExe -WorkingDirectory (Split-Path -Parent $resolvedExe) | Out-Null
        $script:AcceptanceStatusLabel.Text = "Layal launch requested. Routed clicks: 0"
        Write-ClickThroughLog -Target "Harness" -EventName "LayalLaunchRequested"
    }
    catch {
        $script:AcceptanceStatusLabel.Text = "Failed to start Layal.exe: $($_.Exception.Message)"
        Write-ClickThroughLog -Target "Harness" -EventName "LayalLaunchFailed"
        $message = "Could not start Layal.exe. Check the path and .NET Windows Desktop Runtime." + [Environment]::NewLine + $_.Exception.Message
        [System.Windows.Forms.MessageBox]::Show(
            $message,
            "Layal click-through test",
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
    }
})

$form.Add_FormClosed({
    Write-ClickThroughLog -Target "Harness" -EventName "HarnessClosed"
})

Write-ClickThroughLog -Target "Harness" -EventName "HarnessStarted"
Write-Host "Click-through log: $resolvedLogPath"
Write-Host "Close the test surface when done. Close Layal separately from its tray/menu."
[void]$form.ShowDialog()
