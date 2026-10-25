using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Layal.Models;

namespace Layal.Services;

/// <summary>
/// Executes a small allow-list of typed tools. It deliberately does not expose arbitrary shell,
/// arbitrary filesystem access, or unrestricted mouse/keyboard control to the model.
/// </summary>
public sealed class AgentToolExecutor
{
    private static readonly HashSet<string> ReadableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".cs", ".csproj", ".sln", ".json", ".xaml", ".xml", ".py", ".ps1",
        ".bat", ".yml", ".yaml", ".toml", ".log", ".csv", ".props", ".targets", ".config", ".ini"
    };
    private static readonly HashSet<string> WritableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".cs", ".json", ".xaml", ".xml", ".py", ".yml", ".yaml", ".toml", ".csv"
    };
    private static readonly System.Text.RegularExpressions.Regex ReservedDeviceName =
        new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private const int MaxReadBytes = 256 * 1024;
    private const int MaxWriteChars = 100 * 1024;
    private const int MaxProcessOutputChars = 120_000;
    private readonly string _workspaceRoot;
    private readonly MemoryService _memory;

    public AgentToolExecutor(string workspaceRoot, MemoryService memory)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _memory = memory;
        Directory.CreateDirectory(_workspaceRoot);
    }

    public string WorkspaceRoot => _workspaceRoot;

    public JsonArray GetToolDefinitions() => new()
    {
        Definition("get_current_time", "Get the current local time and date.", "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        Definition("get_system_info", "Get non-sensitive basic OS/runtime information.", "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        Definition("get_workspace_info", "Get the approved Layal workspace path and basic file count.", "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        Definition("list_workspace_files", "List files and directories directly within an approved workspace directory. Provide a relative directory path or an empty string for the workspace root.", """{"type":"object","properties":{"directory":{"type":"string","description":"Relative directory inside the workspace; empty means the workspace root"}},"required":["directory"],"additionalProperties":false}"""),
        Definition("read_text_file", "Read a small text/source file from the approved workspace. The user must approve because its contents may be sent to the configured AI provider.", """{"type":"object","properties":{"path":{"type":"string","description":"Relative file path inside the workspace"}},"required":["path"],"additionalProperties":false}"""),
        Definition("create_text_file", "Create a new text/source file inside the workspace. Requires user approval; existing files are never overwritten.", """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"],"additionalProperties":false}"""),
        Definition("replace_text_in_file", "Replace an exact unique string in a UTF-8 text file inside the workspace. Creates a backup and requires user approval.", """{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"}},"required":["path","old_text","new_text"],"additionalProperties":false}"""),
        Definition("open_notepad", "Open Windows Notepad. Requires user approval.", "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        Definition("open_url", "Open an HTTP or HTTPS URL in the user's default browser. Requires approval.", """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"],"additionalProperties":false}"""),
        Definition("get_open_window_titles", "List visible top-level window titles. Disabled unless the user opts in, and requires consent before titles are sent to the AI provider.", "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        Definition("activate_window", "Bring a visible window with an exact title to the foreground. Requires user approval.", """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"],"additionalProperties":false}"""),
        Definition("run_dotnet_build", "Run dotnet build for a .sln or .csproj inside the approved workspace. Build targets may execute project code, so explicit approval is required.", """{"type":"object","properties":{"project_file":{"type":"string","description":"Relative path to a .sln or .csproj inside the workspace"},"configuration":{"type":"string","enum":["Debug","Release"]}},"required":["project_file","configuration"],"additionalProperties":false}"""),
        Definition("run_dotnet_test", "Run dotnet test for a .sln or .csproj inside the approved workspace. Tests may execute project code, so explicit approval is required.", """{"type":"object","properties":{"project_file":{"type":"string","description":"Relative path to a .sln or .csproj inside the workspace"},"configuration":{"type":"string","enum":["Debug","Release"]}},"required":["project_file","configuration"],"additionalProperties":false}"""),
        Definition("search_memory", "Search user-managed Layal memory. Always ask permission before sharing matched notes with the configured AI provider.", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}"""),
        Definition("save_memory", "Save a user-approved non-sensitive note to encrypted local memory. Requires approval.", """{"type":"object","properties":{"title":{"type":"string"},"content":{"type":"string"}},"required":["title","content"],"additionalProperties":false}""")
    };

    public async Task<string> ExecuteAsync(
        string name,
        string argumentsJson,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonObject args;
        try { args = JsonNode.Parse(argumentsJson) as JsonObject ?? throw new JsonException("Arguments must be a JSON object."); }
        catch (JsonException ex) { throw new InvalidOperationException("Tool arguments were invalid JSON: " + ex.Message); }

        return name switch
        {
            "get_current_time" => DateTimeOffset.Now.ToString("F"),
            "get_system_info" => GetSystemInfo(),
            "get_workspace_info" => GetWorkspaceInfo(),
            "list_workspace_files" => ListWorkspaceFiles(GetString(args, "directory")),
            "read_text_file" => await ReadTextFileAsync(GetString(args, "path"), confirm, report, cancellationToken),
            "create_text_file" => await CreateTextFileAsync(GetString(args, "path"), GetString(args, "content"), confirm, report, cancellationToken),
            "replace_text_in_file" => await ReplaceTextAsync(GetString(args, "path"), GetString(args, "old_text"), GetString(args, "new_text"), confirm, report, cancellationToken),
            "open_notepad" => await OpenNotepadAsync(confirm, cancellationToken),
            "open_url" => await OpenUrlAsync(GetString(args, "url"), confirm, cancellationToken),
            "get_open_window_titles" => await GetWindowTitlesAsync(confirm, cancellationToken),
            "activate_window" => await ActivateWindowAsync(GetString(args, "title"), confirm, cancellationToken),
            "run_dotnet_build" => await RunDotnetAsync("build", GetString(args, "project_file"), GetString(args, "configuration"), confirm, report, cancellationToken),
            "run_dotnet_test" => await RunDotnetAsync("test", GetString(args, "project_file"), GetString(args, "configuration"), confirm, report, cancellationToken),
            "search_memory" => await SearchMemoryAsync(GetString(args, "query"), confirm, cancellationToken),
            "save_memory" => await SaveMemoryAsync(GetString(args, "title"), GetString(args, "content"), confirm, cancellationToken),
            _ => throw new InvalidOperationException($"Tool '{name}' is not registered.")
        };
    }

    private static JsonObject Definition(string name, string description, string parametersJson)
    {
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = JsonNode.Parse(parametersJson) ?? new JsonObject()
            }
        };
    }

    private static string GetString(JsonObject args, string name)
    {
        if (args[name] is not JsonValue value || !value.TryGetValue<string>(out var result))
            throw new InvalidOperationException($"Required string argument '{name}' is missing or invalid.");
        if (result.Length > 150_000) throw new InvalidOperationException($"Argument '{name}' is too large.");
        return result;
    }

    private static string GetSystemInfo() => string.Join(Environment.NewLine,
        $"OS: {Environment.OSVersion}",
        $"64-bit process: {Environment.Is64BitProcess}",
        $"Processor count: {Environment.ProcessorCount}",
        $"Runtime: {Environment.Version}",
        "Note: user name, credentials, and private paths are intentionally excluded.");

    private string GetWorkspaceInfo()
    {
        var count = Directory.EnumerateFiles(_workspaceRoot, "*", SearchOption.TopDirectoryOnly).Take(1001).Count();
        return $"An approved Layal workspace is available. Use relative paths only.{Environment.NewLine}Top-level file count: {(count > 1000 ? "1000+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
    }

    private string ListWorkspaceFiles(string relativeDirectory)
    {
        var directory = ResolvePath(relativeDirectory, allowEmpty: true);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The requested workspace directory does not exist.");
        EnsureNoReparsePoints(directory);
        var lines = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(100))
        {
            var info = Directory.Exists(entry) ? (FileSystemInfo)new DirectoryInfo(entry) : new FileInfo(entry);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var rel = Path.GetRelativePath(_workspaceRoot, entry);
            lines.Add((Directory.Exists(entry) ? "[DIR] " : "[FILE] ") + rel);
        }
        return lines.Count == 0 ? "The directory is empty or contains no eligible entries." : string.Join(Environment.NewLine, lines);
    }

    private async Task<string> ReadTextFileAsync(string relativePath, Func<string, string, CancellationToken, Task<bool>> confirm, Action<string> report, CancellationToken token)
    {
        var path = ResolvePath(relativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("The requested workspace file does not exist.", relativePath);
        EnsureNoReparsePoints(path);
        var info = new FileInfo(path);
        if (!ReadableExtensions.Contains(info.Extension)) throw new InvalidOperationException("This file extension is not on the text-reading allow-list.");
        if (info.Length > MaxReadBytes) throw new InvalidOperationException($"File exceeds the {MaxReadBytes / 1024} KB read limit.");
        if (!await confirm("Read and share file", $"Read '{Path.GetRelativePath(_workspaceRoot, path)}' and send its contents to the configured AI provider? Only do this if the file does not contain secrets or private data.", token))
            return "User declined to share this file with the configured AI provider.";
        var content = await File.ReadAllTextAsync(path, token);
        report($"Read approved text file: {Path.GetRelativePath(_workspaceRoot, path)} ({info.Length} bytes)");
        return content;
    }

    private async Task<string> CreateTextFileAsync(string relativePath, string content, Func<string, string, CancellationToken, Task<bool>> confirm, Action<string> report, CancellationToken token)
    {
        if (content.Length > MaxWriteChars) throw new InvalidOperationException("File content exceeds the 100 KB limit.");
        var path = ResolvePath(relativePath);
        if (!WritableExtensions.Contains(Path.GetExtension(path))) throw new InvalidOperationException("This file extension is not on the creation allow-list.");
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("The target already exists. Layal never overwrites an existing file with create_text_file.");
        var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Parent directory must already exist inside the workspace.");
        EnsureNoReparsePoints(parent);
        if (!await confirm("Create file", $"Create a new file at '{Path.GetRelativePath(_workspaceRoot, path)}' ({content.Length} characters)?", token))
            return "User declined file creation.";
        token.ThrowIfCancellationRequested();
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) await writer.WriteAsync(content.AsMemory(), token);
        report($"Created file: {Path.GetRelativePath(_workspaceRoot, path)}");
        return $"File created: {Path.GetRelativePath(_workspaceRoot, path)}";
    }

    private async Task<string> ReplaceTextAsync(string relativePath, string oldText, string newText, Func<string, string, CancellationToken, Task<bool>> confirm, Action<string> report, CancellationToken token)
    {
        if (string.IsNullOrEmpty(oldText)) throw new InvalidOperationException("old_text cannot be empty.");
        if (newText.Length > MaxWriteChars) throw new InvalidOperationException("Replacement text exceeds the 100 KB limit.");
        var path = ResolvePath(relativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("The target file does not exist.", relativePath);
        EnsureNoReparsePoints(path);
        if (!WritableExtensions.Contains(Path.GetExtension(path))) throw new InvalidOperationException("This file extension is not on the text-modification allow-list.");
        var info = new FileInfo(path);
        if (info.Length > 512 * 1024) throw new InvalidOperationException("The file exceeds the 512 KB edit limit.");
        var original = await File.ReadAllTextAsync(path, token);
        var first = original.IndexOf(oldText, StringComparison.Ordinal);
        if (first < 0) throw new InvalidOperationException("The exact old_text was not found. No changes were made.");
        if (original.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("old_text occurs more than once. Provide a more specific unique snippet.");
        var updated = original[..first] + newText + original[(first + oldText.Length)..];
        if (!await confirm("Modify file", $"Replace one exact text match in '{Path.GetRelativePath(_workspaceRoot, path)}'? A timestamped backup will be created first.", token))
            return "User declined the file modification.";
        token.ThrowIfCancellationRequested();
        var current = await File.ReadAllTextAsync(path, token);
        if (!string.Equals(current, original, StringComparison.Ordinal)) throw new IOException("The file changed after preview; no changes were written. Re-read it and try again.");
        var backupRoot = Path.Combine(_workspaceRoot, ".layal-backups");
        Directory.CreateDirectory(backupRoot);
        EnsureNoReparsePoints(backupRoot);
        var backupName = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path) + ".bak";
        var backupPath = Path.Combine(backupRoot, backupName);
        await using (var backupStream = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var backupWriter = new StreamWriter(backupStream, new UTF8Encoding(false)))
            await backupWriter.WriteAsync(original.AsMemory(), token);
        var temp = path + ".layal-tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var tempStream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var tempWriter = new StreamWriter(tempStream, new UTF8Encoding(false)))
                await tempWriter.WriteAsync(updated.AsMemory(), token);
            EnsureNoReparsePoints(path);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        report($"Modified file: {Path.GetRelativePath(_workspaceRoot, path)}; backup: {Path.GetRelativePath(_workspaceRoot, backupPath)}");
        return $"Updated file: {Path.GetRelativePath(_workspaceRoot, path)}{Environment.NewLine}Backup: {Path.GetRelativePath(_workspaceRoot, backupPath)}";
    }

    private async Task<string> OpenNotepadAsync(Func<string, string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        if (!await confirm("Launch Notepad", "Allow Layal to launch the Windows Notepad application?", token)) return "User declined to launch Notepad.";
        token.ThrowIfCancellationRequested();
        var process = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        if (process is null) throw new InvalidOperationException("Windows did not return a Notepad process.");
        return $"Notepad launch requested. Process ID: {process.Id}.";
    }

    private async Task<string> OpenUrlAsync(string urlText, Func<string, string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Only valid HTTP/HTTPS URLs without embedded credentials may be opened.");
        if (!await confirm("Open website", $"Open this URL in the default browser?\n{uri.AbsoluteUri}", token)) return "User declined to open the URL.";
        token.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return "URL was handed to the Windows default browser. This does not prove that the page loaded successfully.";
    }

    private async Task<string> GetWindowTitlesAsync(Func<string, string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        if (!AppRuntime.Settings.AllowWindowTitleInspection)
            return "Window-title inspection is disabled in Layal settings. Ask the user to enable it first.";
        var titles = WindowInventory.GetVisibleWindowTitles();
        if (titles.Count == 0) return "No titled visible windows were found.";
        var content = string.Join(Environment.NewLine, titles.Take(60).Select(x => $"{x.Title} (PID {x.ProcessId})"));
        if (!await confirm("Share open-window titles", "The following window titles and process IDs will be sent to the configured AI provider. They may reveal private information. Continue?\n\n" + content, token))
            return "User declined to share open-window titles.";
        return content;
    }

    private async Task<string> ActivateWindowAsync(string title, Func<string, string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        if (!AppRuntime.Settings.AllowWindowTitleInspection)
            return "Window interaction is disabled. Enable window-title inspection in Settings first.";
        if (title.Length > 200) throw new InvalidOperationException("Window title is too long.");
        if (!await confirm("Activate window", $"Try to bring a window with this exact title to the foreground?\n{title}", token)) return "User declined window activation.";
        token.ThrowIfCancellationRequested();
        return WindowInventory.ActivateWindowByExactTitle(title)
            ? "Windows accepted the request to activate the matching window. Verify the foreground state manually if important."
            : "No visible window with that exact title was found or Windows declined the activation request.";
    }

    private async Task<string> RunDotnetAsync(string verb, string relativeProjectFile, string configuration, Func<string, string, CancellationToken, Task<bool>> confirm, Action<string> report, CancellationToken token)
    {
        if (configuration is not ("Debug" or "Release")) throw new InvalidOperationException("Configuration must be Debug or Release.");
        var projectPath = ResolvePath(relativeProjectFile);
        var extension = Path.GetExtension(projectPath);
        if (extension is not (".sln" or ".csproj")) throw new InvalidOperationException("Only .sln or .csproj files can be passed to this tool.");
        if (!File.Exists(projectPath)) throw new FileNotFoundException("Project or solution file not found inside the workspace.", relativeProjectFile);
        EnsureNoReparsePoints(projectPath);
        if (!await confirm($"Run dotnet {verb}", $"Run `dotnet {verb}` on '{Path.GetRelativePath(_workspaceRoot, projectPath)}' using {configuration}? Build/test targets may execute code in this project and may restore packages from the internet. Only continue if you trust the project.", token))
            return "User declined the build/test operation.";

        var workingDirectory = Path.GetDirectoryName(projectPath)!;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(verb);
        start.ArgumentList.Add(projectPath);
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(configuration);
        start.ArgumentList.Add("--nologo");
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        report($"Starting dotnet {verb} for {Path.GetRelativePath(_workspaceRoot, projectPath)}.");
        if (!process.Start()) throw new InvalidOperationException("Failed to start the dotnet process.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxProcessOutputChars / 2, timeout.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, MaxProcessOutputChars / 2, timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            // The bounded readers were cancelled together with the wait; observe them so their
            // cancellation is not reported later as an unobserved task exception.
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
            token.ThrowIfCancellationRequested();
            throw new TimeoutException($"dotnet {verb} timed out after 120 seconds; process termination was requested.");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var output = $"Command: dotnet {verb} \"{relativeProjectFile}\" --configuration {configuration}{Environment.NewLine}Exit code: {process.ExitCode}{Environment.NewLine}--- STDOUT ---{Environment.NewLine}{stdout}{Environment.NewLine}--- STDERR ---{Environment.NewLine}{stderr}";
        report($"dotnet {verb} finished with exit code {process.ExitCode}.");
        return output;
    }

    private async Task<string> SearchMemoryAsync(string query, Func<string, string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        var found = _memory.Search(query, 8);
        if (found.Count == 0) return "No matching memory notes were found.";
        var result = string.Join("\n\n", found.Select(n => $"Title: {n.Title}\nUpdated: {n.UpdatedAt:O}\nContent: {n.Content}"));
        if (!await confirm("Share memory with AI", "Matching memory notes will be sent to the configured AI provider. Review and approve this disclosure?\n\n" + Truncate(result, 1400), token))
            return "User declined to share memory notes with the AI provider.";
        return result;
    }

    private async Task<string> SaveMemoryAsync(string title, string content, Func<string, string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        if (!await confirm("Save memory", $"Save this note encrypted on this Windows user profile?\n\nTitle: {Truncate(title, 120)}\nContent: {Truncate(content, 800)}", token))
            return "User declined to save this memory note.";
        token.ThrowIfCancellationRequested();
        var note = _memory.Save(title, content);
        return $"Saved memory note '{note.Title}' locally using Windows DPAPI for the current user.";
    }

    private string ResolvePath(string relativePath, bool allowEmpty = false)
    {
        relativePath = (relativePath ?? string.Empty).Trim();
        if (allowEmpty && relativePath.Length == 0) return _workspaceRoot;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException("Use a non-empty relative path inside LayalWorkspace.");
        var parts = relativePath.Replace('/', Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or "..")) throw new InvalidOperationException("Relative paths cannot contain '.' or '..' path segments.");
        // Alternate data streams ("file.txt:hidden") and Windows device names (NUL, CON, COM1...) are never valid workspace targets.
        if (parts.Any(p => p.Contains(':') || ReservedDeviceName.IsMatch(p)))
            throw new InvalidOperationException("Path segments cannot contain ':' or use reserved Windows device names.");
        var fullPath = Path.GetFullPath(Path.Combine(_workspaceRoot, relativePath));
        var relative = Path.GetRelativePath(_workspaceRoot, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidOperationException("The path escapes the approved workspace.");
        return fullPath;
    }

    private void EnsureNoReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_workspaceRoot, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("The path is outside the workspace.");
        var current = _workspaceRoot;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The workspace root cannot be a symbolic link/reparse point.");
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Symbolic links/reparse points are not allowed in this tool path.");
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxChars, CancellationToken token)
    {
        var builder = new StringBuilder(Math.Min(maxChars, 8192));
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) break;
            var remaining = maxChars - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining) truncated = true;
        }
        if (truncated) builder.Append("\n[output truncated by Layal]");
        return builder.ToString();
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}

public sealed record WindowTitleInfo(string Title, uint ProcessId);

public static class WindowInventory
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static IReadOnlyList<WindowTitleInfo> GetVisibleWindowTitles()
    {
        var results = new List<WindowTitleInfo>();
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var length = GetWindowTextLength(hWnd);
            if (length <= 0 || length > 500) return true;
            var builder = new StringBuilder(length + 1);
            _ = GetWindowText(hWnd, builder, builder.Capacity);
            var title = builder.ToString().Trim();
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(hWnd, out var processId);
            results.Add(new WindowTitleInfo(title, processId));
            return results.Count < 100;
        }, IntPtr.Zero);
        return results;
    }

    public static bool ActivateWindowByExactTitle(string title)
    {
        IntPtr match = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var length = GetWindowTextLength(hWnd);
            if (length <= 0 || length > 500) return true;
            var builder = new StringBuilder(length + 1);
            _ = GetWindowText(hWnd, builder, builder.Capacity);
            if (string.Equals(builder.ToString().Trim(), title, StringComparison.Ordinal))
            {
                match = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        if (match == IntPtr.Zero) return false;
        _ = ShowWindow(match, 9); // SW_RESTORE
        return SetForegroundWindow(match);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hWnd, int command);
}
