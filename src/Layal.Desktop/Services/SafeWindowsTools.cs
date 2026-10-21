using System.Diagnostics;

namespace Layal.Services;

/// <summary>First allow-listed native desktop tool. Never accepts arbitrary command text.</summary>
public static class SafeWindowsTools
{
    public static string LaunchNotepad()
    {
        var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true };
        var process = Process.Start(start);
        if (process is null) throw new InvalidOperationException("Windows did not return a process for Notepad.");
        return $"Notepad launch requested; process id {process.Id}.";
    }
}
