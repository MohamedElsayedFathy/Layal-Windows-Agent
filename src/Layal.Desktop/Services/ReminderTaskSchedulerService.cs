using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Xml.Linq;
using Layal.Models;

namespace Layal.Services;

/// <summary>
/// Registers one user-scoped Windows Task Scheduler trigger for the next active reminder.
/// A short-lived PowerShell wrapper launches Layal in the interactive user's session at
/// least-privilege and exits immediately; it never runs as SYSTEM/elevated or wakes the
/// computer. StartWhenAvailable catches a missed trigger after sign-in. The encrypted
/// reminder store remains the source of truth.
/// </summary>
public sealed class ReminderTaskSchedulerService
{
    public const string TaskName = @"\LayalReminderDispatcher";
    private const int ProcessTimeoutMilliseconds = 15_000;

    private readonly string _executablePath;
    private readonly string _userSid;

    public ReminderTaskSchedulerService(string? executablePath = null, string? userSid = null)
    {
        _executablePath = Path.GetFullPath(executablePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Layal's executable path is unavailable."));
        _userSid = userSid ?? (WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable."));
        if (string.IsNullOrWhiteSpace(_userSid) || _userSid.Length > 184)
            throw new ArgumentException("The current Windows user SID is invalid.", nameof(userSid));
    }

    /// <summary>
    /// Creates or replaces the single dispatch task for the earliest active reminder.
    /// With no active reminders, the old Layal dispatch task is removed.
    /// </summary>
    public void Synchronize(
        System.Collections.Generic.IEnumerable<ReminderRecord> reminders,
        DateTimeOffset? proactiveCheckInDueAt = null)
    {
        ArgumentNullException.ThrowIfNull(reminders);
        var nextReminderDueAt = reminders
            .Where(item => item is not null && item.IsActive && item.DueAt != default)
            .Select(item => (DateTimeOffset?)item.DueAt)
            .OrderBy(value => value)
            .FirstOrDefault();

        var dueAtValue = nextReminderDueAt;
        if (proactiveCheckInDueAt is not null &&
            (dueAtValue is null || proactiveCheckInDueAt.Value < dueAtValue.Value))
            dueAtValue = proactiveCheckInDueAt;

        if (dueAtValue is null)
        {
            DeleteTaskIfPresent();
            return;
        }

        var dueAt = dueAtValue.Value.ToLocalTime().DateTime;
        var now = DateTime.Now;
        if (dueAt <= now)
            dueAt = now.AddMinutes(1);
        else if (dueAt.Second != 0 || dueAt.Millisecond != 0)
            dueAt = new DateTime(dueAt.Year, dueAt.Month, dueAt.Day, dueAt.Hour, dueAt.Minute, 0, DateTimeKind.Local).AddMinutes(1);

        var xml = BuildTaskXml(new DateTimeOffset(dueAt), _executablePath, _userSid);
        var temporaryPath = Path.Combine(Path.GetTempPath(), "layal-reminder-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(temporaryPath, xml, new System.Text.UTF8Encoding(false));
            RunSchtasks("/Create", "/TN", TaskName, "/XML", temporaryPath, "/F");
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes only Layal's fixed dispatch task; a missing task is already the desired state.</summary>
    public void DeleteTaskIfPresent()
    {
        var result = RunSchtasksAllowMissing("/Delete", "/TN", TaskName, "/F");
        if (result.ExitCode != 0 &&
            !result.Output.Contains("cannot find", StringComparison.OrdinalIgnoreCase) &&
            !result.Output.Contains("could not be found", StringComparison.OrdinalIgnoreCase) &&
            !result.Output.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Windows Task Scheduler could not remove Layal's reminder task: " + Truncate(result.Output, 500));
        }
    }

    /// <summary>Builds a Task Scheduler 1.4 XML document using an interactive, least-privilege principal.</summary>
    public static string BuildTaskXml(DateTimeOffset dueAt, string executablePath, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        var fullExecutablePath = Path.GetFullPath(executablePath);
        if (fullExecutablePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new ArgumentException("The executable path is invalid.", nameof(executablePath));

        var ns = XNamespace.Get("http://schemas.microsoft.com/windows/2004/02/mit/task");
        var root = new XElement(ns + "Task",
            new XAttribute("version", "1.4"),
            new XElement(ns + "RegistrationInfo",
                new XElement(ns + "Author", "Layal"),
                new XElement(ns + "Description", "Dispatches the next encrypted Layal local reminder. Runs only in the current interactive user session.")),
            new XElement(ns + "Triggers",
                new XElement(ns + "TimeTrigger",
                    new XElement(ns + "StartBoundary", dueAt.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                    new XElement(ns + "Enabled", "true"))),
            new XElement(ns + "Principals",
                new XElement(ns + "Principal",
                    new XAttribute("id", "LayalUser"),
                    new XElement(ns + "UserId", userSid),
                    new XElement(ns + "LogonType", "InteractiveToken"),
                    new XElement(ns + "RunLevel", "LeastPrivilege"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(ns + "StopIfGoingOnBatteries", "false"),
                new XElement(ns + "StartWhenAvailable", "true"),
                new XElement(ns + "Enabled", "true"),
                new XElement(ns + "Hidden", "true"),
                new XElement(ns + "ExecutionTimeLimit", "PT2M"),
                new XElement(ns + "Priority", "7")),
            new XElement(ns + "Actions",
                new XAttribute("Context", "LayalUser"),
                new XElement(ns + "Exec",
                    new XElement(ns + "Command", Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "WindowsPowerShell", "v1.0", "powershell.exe")),
                    new XElement(ns + "Arguments", BuildDetachedLaunchArguments(fullExecutablePath)),
                    new XElement(ns + "WorkingDirectory", Environment.GetFolderPath(Environment.SpecialFolder.System))));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root)
            .ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildDetachedLaunchArguments(string executablePath)
    {
        // The scheduled action must finish immediately. Starting Layal directly would leave the
        // Task Scheduler instance running for the entire tray lifetime and block future updates
        // under MultipleInstancesPolicy=IgnoreNew.
        var workingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        var script = "$ErrorActionPreference = 'Stop'; " +
            "$exe = '" + EscapePowerShellLiteral(executablePath) + "'; " +
            "$working = '" + EscapePowerShellLiteral(workingDirectory) + "'; " +
            "Start-Process -FilePath $exe -ArgumentList '--scheduled-reminder' -WorkingDirectory $working -WindowStyle Hidden";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        return "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand " + encoded;
    }

    private static string EscapePowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static (int ExitCode, string Output) RunSchtasksAllowMissing(params string[] arguments) =>
        RunSchtasksCore(arguments);

    private static void RunSchtasks(params string[] arguments)
    {
        var result = RunSchtasksCore(arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                "Windows Task Scheduler could not update Layal's reminder task: " + Truncate(result.Output, 500));
    }

    private static (int ExitCode, string Output) RunSchtasksCore(string[] arguments)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Task Scheduler is available only on Windows.");

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var executable = Path.Combine(systemDirectory, "schtasks.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Windows Task Scheduler command-line utility was not found.", executable);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = systemDirectory
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows Task Scheduler process could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeoutMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("Windows Task Scheduler did not respond within 15 seconds.");
        }

        Task.WaitAll(standardOutput, standardError);
        return (process.ExitCode, standardOutput.Result + Environment.NewLine + standardError.Result);
    }

    private static string Truncate(string value, int maximum) =>
        string.IsNullOrEmpty(value) || value.Length <= maximum ? value : value[..maximum];
}
