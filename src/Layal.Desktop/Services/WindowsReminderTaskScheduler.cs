using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using Layal.Models;

namespace Layal.Services;

/// <summary>
/// Schedules local reminder launches in Windows Task Scheduler for the currently logged-on user.
/// Tasks run only with an interactive, least-privilege token; they never run when that user is logged off.
/// </summary>
public sealed class WindowsReminderTaskScheduler : IReminderTaskScheduler
{
    private const string TaskFolder = @"\Layal\";
    private const string TaskPrefix = "Reminder-";
    private const int CommandTimeoutMilliseconds = 15_000;

    public void Schedule(ReminderRecord reminder)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Task Scheduler is available only on Windows.");
        if (!Guid.TryParseExact(reminder.Id, "N", out var id))
            throw new InvalidOperationException("The reminder ID is invalid.");
        if (!reminder.IsActive)
        {
            Remove(reminder.Id);
            return;
        }
        if (reminder.DueAt <= DateTimeOffset.Now)
            return;

        var executable = GetCurrentExecutablePath();
        var script = BuildRegisterScript(executable, reminder);
        RunPowerShell(script);
    }

    public void Remove(string reminderId)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!Guid.TryParseExact(reminderId, "N", out _))
            return;

        var taskName = TaskPrefix + reminderId;
        var script = $@"
$ErrorActionPreference = 'Stop'
Import-Module ScheduledTasks -ErrorAction Stop
$taskPath = '{TaskFolder}'
$taskName = '{taskName}'
$task = Get-ScheduledTask -TaskPath $taskPath -TaskName $taskName -ErrorAction SilentlyContinue
if ($null -ne $task) {{
    Unregister-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Confirm:$false -ErrorAction Stop
}}
";
        RunPowerShell(script);
    }

    /// <summary>Rebuilds only Layal-owned reminder tasks from the encrypted local reminder store.</summary>
    public void Reconcile(IReadOnlyCollection<ReminderRecord> reminders)
    {
        ArgumentNullException.ThrowIfNull(reminders);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Task Scheduler is available only on Windows.");

        var executable = GetCurrentExecutablePath();
        var active = reminders
            .Where(item => item.IsActive && item.DueAt > DateTimeOffset.Now)
            .OrderBy(item => item.DueAt)
            .ToArray();
        if (active.Any(item => !Guid.TryParseExact(item.Id, "N", out _)))
            throw new InvalidOperationException("The reminder store contains an invalid reminder ID.");

        var builder = new StringBuilder();
        builder.AppendLine("$ErrorActionPreference = 'Stop'");
        builder.AppendLine("Import-Module ScheduledTasks -ErrorAction Stop");
        builder.AppendLine("$taskPath = '" + TaskFolder + "'");
        builder.AppendLine("$service = New-Object -ComObject Schedule.Service");
        builder.AppendLine("$service.Connect()");
        builder.AppendLine("$rootFolder = $service.GetFolder('\\')");
        builder.AppendLine("try { $null = $service.GetFolder('\\Layal') } catch { $null = $rootFolder.CreateFolder('Layal', $null) }");
        builder.AppendLine("Get-ScheduledTask -TaskPath $taskPath -ErrorAction SilentlyContinue |");
        builder.AppendLine("    Where-Object { $_.TaskName -like '" + TaskPrefix + "*' } |");
        builder.AppendLine("    Unregister-ScheduledTask -Confirm:$false -ErrorAction Stop");
        foreach (var reminder in active)
            builder.Append(BuildRegisterCommands(executable, reminder));
        RunPowerShell(builder.ToString());
    }

    public static string BuildRegisterScript(string executablePath, ReminderRecord reminder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(reminder);
        if (!Guid.TryParseExact(reminder.Id, "N", out _))
            throw new ArgumentException("The reminder ID must be a canonical GUID.", nameof(reminder));
        if (reminder.Recurrence is not "None" and not "Daily" and not "Weekly")
            throw new ArgumentException("The reminder recurrence is not supported.", nameof(reminder));

        var builder = new StringBuilder();
        builder.AppendLine("$ErrorActionPreference = 'Stop'");
        builder.AppendLine("Import-Module ScheduledTasks -ErrorAction Stop");
        builder.AppendLine("$taskPath = '" + TaskFolder + "'");
        builder.AppendLine("$service = New-Object -ComObject Schedule.Service");
        builder.AppendLine("$service.Connect()");
        builder.AppendLine("$rootFolder = $service.GetFolder('\\')");
        builder.AppendLine("try { $null = $service.GetFolder('\\Layal') } catch { $null = $rootFolder.CreateFolder('Layal', $null) }");
        builder.Append(BuildRegisterCommands(executablePath, reminder));
        return builder.ToString();
    }

    private static string BuildRegisterCommands(string executablePath, ReminderRecord reminder)
    {
        var id = Guid.ParseExact(reminder.Id, "N").ToString("N");
        var taskName = TaskPrefix + id;
        var exe = EscapePowerShellLiteral(Path.GetFullPath(executablePath));
        var scheduledAt = reminder.DueAt.ToLocalTime().DateTime;
        var at = scheduledAt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var argument = "--deliver-reminder " + id;
        var builder = new StringBuilder();
        builder.AppendLine("$taskName = '" + taskName + "'");
        builder.AppendLine("$exe = '" + exe + "'");
        builder.AppendLine("$at = [datetime]::ParseExact('" + at + "', 'yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)");
        builder.AppendLine("$action = New-ScheduledTaskAction -Execute $exe -Argument '" + argument + "' -WorkingDirectory (Split-Path -Parent $exe)");
        builder.AppendLine(reminder.Recurrence switch
        {
            "Daily" => "$trigger = New-ScheduledTaskTrigger -Daily -At $at",
            "Weekly" => "$trigger = New-ScheduledTaskTrigger -Weekly -WeeksInterval 1 -DaysOfWeek " +
                        scheduledAt.DayOfWeek + " -At $at",
            _ => "$trigger = New-ScheduledTaskTrigger -Once -At $at"
        });
        builder.AppendLine("$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -WakeToRun -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2)");
        builder.AppendLine("$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited");
        builder.AppendLine("Register-ScheduledTask -TaskPath $taskPath -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null");
        return builder.ToString();
    }

    private static string GetCurrentExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Layal could not determine its installed executable path for Windows Task Scheduler.");
        return Path.GetFullPath(path);
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static void RunPowerShell(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(encoded);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Windows PowerShell could not be started to update reminder tasks.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CommandTimeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("Updating Windows reminder tasks exceeded 15 seconds.");
        }

        Task.WaitAll(standardOutput, standardError);
        if (process.ExitCode != 0)
        {
            var detail = standardError.Result.Trim();
            if (detail.Length > 500) detail = detail[..500];
            throw new InvalidOperationException(
                "Windows could not update the scheduled reminder. Check that Task Scheduler is available and the current Windows account can create user tasks." +
                (detail.Length == 0 ? string.Empty : " " + detail));
        }
    }
}
