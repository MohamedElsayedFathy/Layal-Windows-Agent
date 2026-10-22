#requires -Version 5.1
# A local, disposable WPF target for manually testing Layal's guarded Windows UI Automation tools.
# This app does not submit data, persist the entered text, or read back password content.
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$window = New-Object System.Windows.Window
$window.Title = "Layal UI Automation Sample"
$window.Width = 520
$window.Height = 390
$window.MinWidth = 460
$window.MinHeight = 340
$window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::CenterScreen
$window.ResizeMode = [System.Windows.ResizeMode]::CanResize
[System.Windows.Automation.AutomationProperties]::SetAutomationId($window, "layalSampleWindow")

$panel = New-Object System.Windows.Controls.StackPanel
$panel.Margin = New-Object System.Windows.Thickness(22)
$window.Content = $panel

$heading = New-Object System.Windows.Controls.TextBlock
$heading.Text = "Layal — UI Automation safety test"
$heading.FontSize = 19
$heading.FontWeight = [System.Windows.FontWeights]::SemiBold
$heading.Margin = New-Object System.Windows.Thickness(0, 0, 0, 8)
$panel.Children.Add($heading) | Out-Null

$instructions = New-Object System.Windows.Controls.TextBlock
$instructions.Text = "Disposable sample only. Text is displayed locally and never sent anywhere by this window."
$instructions.TextWrapping = [System.Windows.TextWrapping]::Wrap
$instructions.Margin = New-Object System.Windows.Thickness(0, 0, 0, 16)
$panel.Children.Add($instructions) | Out-Null

$textLabel = New-Object System.Windows.Controls.TextBlock
$textLabel.Text = "Ordinary text field (AutomationId: layalSampleTextInput)"
$textLabel.Margin = New-Object System.Windows.Thickness(0, 0, 0, 4)
$panel.Children.Add($textLabel) | Out-Null

$textBox = New-Object System.Windows.Controls.TextBox
$textBox.Height = 32
$textBox.Padding = New-Object System.Windows.Thickness(6, 4, 6, 4)
$textBox.Margin = New-Object System.Windows.Thickness(0, 0, 0, 12)
[System.Windows.Automation.AutomationProperties]::SetAutomationId($textBox, "layalSampleTextInput")
$panel.Children.Add($textBox) | Out-Null

$passwordLabel = New-Object System.Windows.Controls.TextBlock
$passwordLabel.Text = "Protected password field (AutomationId: layalSamplePasswordInput)"
$passwordLabel.Margin = New-Object System.Windows.Thickness(0, 0, 0, 4)
$panel.Children.Add($passwordLabel) | Out-Null

$passwordBox = New-Object System.Windows.Controls.PasswordBox
$passwordBox.Height = 32
$passwordBox.Padding = New-Object System.Windows.Thickness(6, 4, 6, 4)
$passwordBox.Margin = New-Object System.Windows.Thickness(0, 0, 0, 16)
[System.Windows.Automation.AutomationProperties]::SetAutomationId($passwordBox, "layalSamplePasswordInput")
$panel.Children.Add($passwordBox) | Out-Null

$actionButton = New-Object System.Windows.Controls.Button
$actionButton.Content = "Test approved button action"
$actionButton.Height = 34
$actionButton.HorizontalAlignment = [System.Windows.HorizontalAlignment]::Left
$actionButton.Padding = New-Object System.Windows.Thickness(12, 3, 12, 3)
$actionButton.Margin = New-Object System.Windows.Thickness(0, 0, 0, 12)
[System.Windows.Automation.AutomationProperties]::SetAutomationId($actionButton, "layalSampleActionButton")
$panel.Children.Add($actionButton) | Out-Null

$status = New-Object System.Windows.Controls.TextBlock
$status.Text = "Ready. Nothing has been submitted."
$status.TextWrapping = [System.Windows.TextWrapping]::Wrap
$status.Margin = New-Object System.Windows.Thickness(0, 4, 0, 0)
[System.Windows.Automation.AutomationProperties]::SetAutomationId($status, "layalSampleStatus")
$panel.Children.Add($status) | Out-Null

$textBox.Add_TextChanged({
    # Only a character count is shown. The field content is not logged or sent to any service.
    $status.Text = "TextChanged observed locally. Character count: $($textBox.Text.Length)."
})

$actionButton.Add_Click({
    $status.Text = "Button action observed locally at $(Get-Date -Format 'HH:mm:ss'). No data was submitted."
})

[void]$window.ShowDialog()
