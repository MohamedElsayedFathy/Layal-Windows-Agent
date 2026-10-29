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
    private const int MaxNewFilePreviewChars = 4_000;
    private const int MaxReplacementPreviewSideChars = 1_800;
    private const string ApprovalPreviewOmissionText = "…[preview truncated; remaining content omitted]";
    private const int MaxProcessOutputChars = 120_000;
    private readonly string _workspaceRoot;
    private readonly MemoryService _memory;
    private readonly ApiConnectionService? _apiConnections;
    private readonly BrowserAutomationService? _browser;

    public AgentToolExecutor(string workspaceRoot, MemoryService memory, ApiConnectionService? apiConnections = null, BrowserAutomationService? browser = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _memory = memory;
        _apiConnections = apiConnections;
        _browser = browser;
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
        Definition("browser_navigate", "Navigate the dedicated Layal-controlled Microsoft Edge profile to an HTTP/HTTPS URL. This browser uses a separate profile and does not inherit the user's normal browser cookies or sign-in session. Ask for local approval before every navigation.", """{"type":"object","properties":{"url":{"type":"string","description":"Absolute HTTP/HTTPS URL without embedded credentials"}},"required":["url"],"additionalProperties":false}"""),
        Definition("browser_read_page", "Read the current page from Layal's dedicated Edge session: title, URL, visible text up to 20,000 characters and bounded lists of links/buttons/fields. The page contents are sent to the AI provider as tool output, so request local approval before reading/sharing them. Navigate first.", """{"type":"object","properties":{},"additionalProperties":false}"""),
        Definition("browser_click", "Click one visible standalone link/button outside HTML forms in Layal's dedicated browser using a CSS selector. Submit/reset controls and form-associated elements are blocked. Inspect the target locally, show its details, require fresh approval, and revalidate the exact page and element before dispatching a DOM click. Approved page handlers can still navigate or modify remote data. Navigate and inspect the page first.", """{"type":"object","properties":{"selector":{"type":"string","description":"CSS selector matching exactly one intended visible link or button"}},"required":["selector"],"additionalProperties":false}"""),
        Definition("browser_fill", "Fill a visible editable text/email/search/telephone/URL/number input, textarea, or contenteditable element in Layal's dedicated browser using a CSS selector. Password, file, hidden, and button fields are deliberately unsupported. Show the text preview, require fresh approval, and revalidate the page and element. Navigate and inspect the page first.", """{"type":"object","properties":{"selector":{"type":"string","description":"CSS selector for a single visible supported text field"},"text":{"type":"string","description":"Text to enter; max 10,000 characters"}},"required":["selector","text"],"additionalProperties":false}"""),
        Definition("get_open_window_titles", "Call this tool when the user asks to list visible windows. Disabled unless the user opts in; Layal itself will display a local consent prompt before titles are shared with the AI provider. Do not ask for separate permission in chat instead of calling this tool.", "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        Definition("activate_window", "Bring a visible window with an exact title to the foreground. Requires user approval.", """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"],"additionalProperties":false}"""),
        Definition("inspect_window_controls", "Call this tool directly when the user asks to inspect UI elements in a known visible external window. Supply its exact title. Read-only; Layal itself displays a local consent prompt before sharing labels and identifiers with the AI provider, so do not ask a separate natural-language permission question first. It never reads editable field values or clicks/types.", """{"type":"object","properties":{"window_title":{"type":"string","description":"Exact title of a visible external window; first use get_open_window_titles if needed"}},"required":["window_title"],"additionalProperties":false}"""),
        Definition("click_ui_control", "Invoke exactly one uniquely identified supported control in one visible external window after explicit per-action approval. Requires the exact AutomationId and supported control type; no free-form mouse/keyboard input.", """{"type":"object","properties":{"window_title":{"type":"string","description":"Exact title of the visible external window"},"control_type":{"type":"string","enum":["Button","Hyperlink","MenuItem","SplitButton"]},"automation_id":{"type":"string","description":"Exact non-empty AutomationId reported by inspect_window_controls"}},"required":["window_title","control_type","automation_id"],"additionalProperties":false}"""),
        Definition("fill_ui_text", "Set the value of one exact visible Windows UI Automation Edit field in an external application after explicit local approval. Requires the exact window title and AutomationId from inspect_window_controls. Never use for password, protected, read-only, disabled, offscreen, or ambiguous controls; it does not press Enter or submit forms.", """{"type":"object","properties":{"window_title":{"type":"string","description":"Exact unique visible title of the external application window"},"automation_id":{"type":"string","description":"Exact non-empty AutomationId reported by inspect_window_controls"},"text":{"type":"string","description":"Value to enter; maximum 10,000 characters"}},"required":["window_title","automation_id","text"],"additionalProperties":false}"""),
        Definition("set_ui_checkbox", "Set one uniquely identified visible Windows CheckBox to checked or unchecked after fresh local approval. Requires the exact AutomationId from inspect_window_controls. Indeterminate/three-state checkboxes, ambiguous controls, and unsupported controls are rejected. Does not use coordinates or keyboard input.", """{"type":"object","properties":{"window_title":{"type":"string","description":"Exact unique visible title of the external application window"},"automation_id":{"type":"string","description":"Exact non-empty AutomationId of one CheckBox reported by inspect_window_controls"},"checked":{"type":"boolean","description":"true to check the box, false to uncheck it"}},"required":["window_title","automation_id","checked"],"additionalProperties":false}"""),
        Definition("list_app_api_connections", "Search all saved application API profiles by name, base URL, model ID, or non-secret details. This returns no credentials and requires approval before catalog metadata is shared with the AI provider.", """{"type":"object","properties":{"query":{"type":"string","description":"Search term; an empty string lists the first matching page of names. Search narrower terms to find connections among a large catalog."}},"required":["query"],"additionalProperties":false}"""),
        Definition("call_app_api", _apiConnections?.BuildToolDescription() ?? "No external app API connection service is available. Do not invent a connection.", """{"type":"object","properties":{"connection_name":{"type":"string","description":"Exact configured connection name from the available connections"},"method":{"type":"string","enum":["GET","POST","PUT","PATCH","DELETE"]},"path":{"type":"string","description":"Relative path beneath the configured base URL; do not include scheme, query, or fragment"},"query_json":{"type":"string","description":"Optional JSON object of query parameters; use empty string for none"},"body_json":{"type":"string","description":"Optional valid JSON request body; use empty string when not needed, and never send a body with GET"}},"required":["connection_name","method","path","query_json","body_json"],"additionalProperties":false}"""),
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
            "browser_navigate" => await BrowserNavigateAsync(GetString(args, "url"), confirm, report, cancellationToken),
            "browser_read_page" => await BrowserReadPageAsync(confirm, report, cancellationToken),
            "browser_click" => await BrowserClickAsync(GetString(args, "selector"), confirm, report, cancellationToken),
            "browser_fill" => await BrowserFillAsync(GetString(args, "selector"), GetString(args, "text"), confirm, report, cancellationToken),
            "get_open_window_titles" => await GetWindowTitlesAsync(confirm, cancellationToken),
            "activate_window" => await ActivateWindowAsync(GetString(args, "title"), confirm, cancellationToken),
            "inspect_window_controls" => await InspectWindowControlsAsync(GetString(args, "window_title"), confirm, report, cancellationToken),
            "click_ui_control" => await InvokeWindowControlAsync(GetString(args, "window_title"), GetString(args, "control_type"), GetString(args, "automation_id"), confirm, report, cancellationToken),
            "fill_ui_text" => await FillUiTextAsync(GetString(args, "window_title"), GetString(args, "automation_id"), GetString(args, "text"), confirm, report, cancellationToken),
            "set_ui_checkbox" => await SetUiCheckboxAsync(GetString(args, "window_title"), GetString(args, "automation_id"), GetBoolean(args, "checked"), confirm, report, cancellationToken),
            "list_app_api_connections" => await ListAppApiConnectionsAsync(GetString(args, "query"), confirm, cancellationToken),
            "call_app_api" => await CallAppApiAsync(GetString(args, "connection_name"), GetString(args, "method"), GetString(args, "path"), GetString(args, "query_json"), GetString(args, "body_json"), confirm, report, cancellationToken),
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

    private static bool GetBoolean(JsonObject args, string name)
    {
        if (args[name] is not JsonValue value || !value.TryGetValue<bool>(out var result))
            throw new InvalidOperationException($"Required boolean argument '{name}' is missing or invalid.");
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

        // Revalidate after the approval dialog: while the user is reviewing it, the file
        // or one of its parent directories may have been replaced by a reparse point.
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            throw new FileNotFoundException("The approved workspace file no longer exists. No data was read.", relativePath);
        EnsureNoReparsePoints(path);
        var approvedInfo = new FileInfo(path);
        if (!ReadableExtensions.Contains(approvedInfo.Extension))
            throw new InvalidOperationException("The approved file no longer has an allowed text-reading extension.");
        if (approvedInfo.Length > MaxReadBytes)
            throw new InvalidOperationException($"File exceeds the {MaxReadBytes / 1024} KB read limit.");
        if (approvedInfo.Length != info.Length || approvedInfo.LastWriteTimeUtc != info.LastWriteTimeUtc)
            throw new IOException("The file changed while awaiting approval. No data was read; review the file and approve again.");

        var content = await File.ReadAllTextAsync(path, token);
        report($"Read approved text file: {Path.GetRelativePath(_workspaceRoot, path)} ({approvedInfo.Length} bytes)");
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
        var approvalDetails =
            $"Create a new file at '{Path.GetRelativePath(_workspaceRoot, path)}' ({content.Length} characters)?"
            + Environment.NewLine + Environment.NewLine
            + $"Proposed file content preview (max {MaxNewFilePreviewChars} characters):"
            + Environment.NewLine
            + BuildBoundedApprovalPreview(content, MaxNewFilePreviewChars);
        if (!await confirm("Create file", approvalDetails, token))
            return "User declined file creation.";
        token.ThrowIfCancellationRequested();

        // The approved target and its parent must still be the same safe workspace paths.
        // Never rely only on the checks performed before showing the approval dialog.
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("The target appeared while awaiting approval. No file was created.");
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The approved parent directory no longer exists. No file was created.");
        EnsureNoReparsePoints(parent);

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
        var approvalDetails =
            $"Replace one exact text match in '{Path.GetRelativePath(_workspaceRoot, path)}'? A timestamped backup will be created first."
            + Environment.NewLine + Environment.NewLine
            + "Proposed replacement preview:"
            + Environment.NewLine + $"--- Removed (max {MaxReplacementPreviewSideChars} characters) ---"
            + Environment.NewLine + BuildBoundedApprovalPreview(oldText, MaxReplacementPreviewSideChars)
            + Environment.NewLine + $"--- Added (max {MaxReplacementPreviewSideChars} characters) ---"
            + Environment.NewLine + BuildBoundedApprovalPreview(newText, MaxReplacementPreviewSideChars);
        if (!await confirm("Modify file", approvalDetails, token))
            return "User declined the file modification.";
        token.ThrowIfCancellationRequested();

        // Revalidate the approved file before reading it again. This prevents a file or
        // parent directory changed during the consent prompt from redirecting the operation.
        if (!File.Exists(path))
            throw new FileNotFoundException("The approved target no longer exists. No changes were made.", relativePath);
        EnsureNoReparsePoints(path);
        var approvedInfo = new FileInfo(path);
        if (approvedInfo.Length > 512 * 1024)
            throw new InvalidOperationException("The approved file exceeds the 512 KB edit limit.");
        if (approvedInfo.Length != info.Length || approvedInfo.LastWriteTimeUtc != info.LastWriteTimeUtc)
            throw new IOException("The file changed while awaiting approval. No changes were made; re-read it and approve again.");

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
            EnsureNoReparsePoints(Path.GetDirectoryName(path)!);
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
            return "Window and UI inspection is disabled in Layal settings. Ask the user to enable it first.";
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
            return "Window interaction is disabled. Enable window and UI inspection in Settings first.";
        if (title.Length > 200) throw new InvalidOperationException("Window title is too long.");
        if (!await confirm("Activate window", $"Try to bring a window with this exact title to the foreground?\n{title}", token)) return "User declined window activation.";
        token.ThrowIfCancellationRequested();
        return WindowInventory.ActivateWindowByExactTitle(title)
            ? "Windows accepted the request to activate the matching window. Verify the foreground state manually if important."
            : "No visible window with that exact title was found or Windows declined the activation request.";
    }

    private async Task<string> InspectWindowControlsAsync(
        string title,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (!AppRuntime.Settings.AllowWindowTitleInspection)
            return "Window and UI inspection is disabled in Layal settings. Ask the user to enable it first.";
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new InvalidOperationException("Provide an exact visible window title up to 200 characters.");

        var target = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (target is null)
            throw new InvalidOperationException("No unique visible external window with that exact title was found. Use get_open_window_titles to review available windows.");
        var approvalText = $"Read the accessible control labels and automation identifiers in '{target.Title}' (PID {target.ProcessId})? This is read-only. Editable field values and screenshots are not read, and no click or keystroke will be sent. The selected metadata will be shared with the configured AI provider and may still reveal private information.";
        if (!await confirm("Inspect application controls", approvalText, token))
            return "User declined UI inspection. No controls were read or shared.";

        token.ThrowIfCancellationRequested();
        var controls = await WindowsUiAutomationService.ReadControlsAsync(target, maxControls: 60, cancellationToken: token);
        report($"Read-only UI inspection completed for an external window (PID {target.ProcessId}); {controls.Count} eligible controls returned.");
        return WindowsUiAutomationService.FormatReport(target.Title, target.ProcessId, controls);
    }

    private async Task<string> InvokeWindowControlAsync(
        string title,
        string controlType,
        string automationId,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (!AppRuntime.Settings.AllowWindowTitleInspection)
            return "Window and UI inspection is disabled in Layal settings. Ask the user to enable it first.";
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new InvalidOperationException("Provide an exact visible window title up to 200 characters.");

        _ = WindowsUiAutomationService.NormalizeInvokeControlType(controlType);
        automationId = WindowsUiAutomationService.ValidateAutomationId(automationId);

        var target = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (target is null)
            throw new InvalidOperationException("No unique visible external window with that exact title was found. Use get_open_window_titles first.");

        var preview = await WindowsUiAutomationService.PreviewInvokeControlAsync(target, controlType, automationId, token);
        var accessibleName = string.IsNullOrWhiteSpace(preview.Name) ? "(no accessible label)" : $"'{preview.Name}'";
        var approvalText =
            $"Perform exactly one UI Automation Invoke action in external window '{target.Title}' (PID {target.ProcessId})?\n" +
            $"Control: {preview.ControlType}; accessible label: {accessibleName}; AutomationId: '{preview.AutomationId}'.\n\n" +
            "This may activate a button, link, menu item, submit a form, navigate away, or change data in the target app. Confirm that the window and control are correct. " +
            "Layal will send no coordinates, typed text, or additional UI actions. A separate action requires another approval.";
        if (!await confirm("Invoke one application control", approvalText, token))
            return "User declined the UI action. No control was invoked.";

        token.ThrowIfCancellationRequested();

        // The user may have changed focus or closed a window while reviewing the approval.
        // Re-resolve the exact title and require the same native handle and process ID.
        var confirmedTarget = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (confirmedTarget is null ||
            confirmedTarget.Handle != target.Handle ||
            confirmedTarget.ProcessId != target.ProcessId)
            throw new InvalidOperationException("The selected window changed while awaiting approval. No UI action was performed; inspect the target again.");

        token.ThrowIfCancellationRequested();
        var outcome = await WindowsUiAutomationService.InvokeControlAsync(confirmedTarget, controlType, automationId, token);
        report($"One approved UI Automation action was issued to external PID {confirmedTarget.ProcessId}; application-level success still requires observing the app.");
        return WindowsUiAutomationService.FormatInvokeReport(outcome);
    }

    private async Task<string> FillUiTextAsync(
        string title,
        string automationId,
        string text,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (!AppRuntime.Settings.AllowWindowTitleInspection)
            return "Window and UI inspection is disabled in Layal settings. Enable it in Settings before interacting with external applications.";
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new InvalidOperationException("Provide an exact visible external window title up to 200 characters.");

        automationId = WindowsUiAutomationService.ValidateAutomationId(automationId);
        _ = WindowsUiAutomationService.ValidateUiText(text);

        var target = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (target is null)
            throw new InvalidOperationException("No unique visible external window with that exact title was found. Use get_open_window_titles and inspect_window_controls first.");

        var preview = await WindowsUiAutomationService.PreviewSetTextAsync(target, automationId, token);
        var shownText = text.Length <= 700 ? text : text[..700] + "… (truncated in preview)";
        var nl = Environment.NewLine;
        var approvalText =
            $"Set the value of exactly one editable field in external window '{target.Title}' (PID {target.ProcessId})?{nl}" +
            $"Control type: Edit; AutomationId: '{preview.AutomationId}'; characters: {text.Length}.{nl}{nl}" +
            $"Text to enter (preview; may be sensitive):{nl}{shownText}{nl}{nl}" +
            "Layal will not read back the existing field value, will not use password fields, and will not press Enter or submit the form. The field must remain the same unique, enabled, visible, writable control while you review this approval.";

        if (!await confirm("Enter text in application field", approvalText, token))
            return "User declined the Windows text-entry action. No field value was changed.";

        token.ThrowIfCancellationRequested();

        // The user may have closed the app or changed the selected window while reading the prompt.
        var confirmedTarget = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (confirmedTarget is null ||
            confirmedTarget.Handle != target.Handle ||
            confirmedTarget.ProcessId != target.ProcessId)
            throw new InvalidOperationException("The selected window changed while awaiting approval. No text was entered; inspect the target again.");

        token.ThrowIfCancellationRequested();
        var outcome = await WindowsUiAutomationService.SetTextAsync(confirmedTarget, preview, text, token);
        report($"One approved UI Automation ValuePattern.SetValue call was issued to external PID {outcome.ProcessId}; the application-level result is not confirmed.");
        return WindowsUiAutomationService.FormatTextEntryReport(outcome);
    }

    private async Task<string> SetUiCheckboxAsync(
        string title,
        string automationId,
        bool shouldBeChecked,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (!AppRuntime.Settings.AllowWindowTitleInspection)
            return "Window and UI inspection is disabled in Layal settings. Enable it in Settings before interacting with external applications.";
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw new InvalidOperationException("Provide an exact visible external window title up to 200 characters.");

        automationId = WindowsUiAutomationService.ValidateAutomationId(automationId);
        var target = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (target is null)
            throw new InvalidOperationException("No unique visible external window with that exact title was found. Use get_open_window_titles and inspect_window_controls first.");

        var preview = await WindowsUiAutomationService.PreviewSetCheckBoxAsync(target, automationId, token);
        var requested = shouldBeChecked ? "checked (On)" : "unchecked (Off)";
        var current = preview.IsChecked ? "checked (On)" : "unchecked (Off)";
        var approvalText =
            $"Set one checkbox in external window '{target.Title}' (PID {target.ProcessId}) to {requested}?\n" +
            $"AutomationId: '{preview.AutomationId}'. Current observed state: {current}.\n\n" +
            "Layal will target only this exact CheckBox. It will reject ambiguous, hidden, disabled, changed, or indeterminate controls. It will not send coordinates or keyboard input. If the checkbox already has the requested state, no toggle is needed.";

        if (!await confirm("Set application checkbox", approvalText, token))
            return "User declined the Windows checkbox action. No state change was requested.";

        token.ThrowIfCancellationRequested();
        var confirmedTarget = WindowsUiAutomationService.FindVisibleWindowByExactTitle(title);
        if (confirmedTarget is null || confirmedTarget.Handle != target.Handle || confirmedTarget.ProcessId != target.ProcessId)
            throw new InvalidOperationException("The selected window changed while awaiting approval. No checkbox action was performed; inspect the target again.");

        token.ThrowIfCancellationRequested();
        var outcome = await WindowsUiAutomationService.SetCheckBoxAsync(confirmedTarget, preview, shouldBeChecked, token);
        report($"One approved TogglePattern state-setting action was issued to external PID {outcome.ProcessId}; verify the application's resulting state.");
        return WindowsUiAutomationService.FormatCheckBoxReport(outcome);
    }

    private async Task<string> BrowserNavigateAsync(
        string url,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (_browser is null)
            throw new InvalidOperationException("The dedicated browser service is not initialized.");
        var target = BrowserAutomationService.ValidateHttpUrl(url);
        var warning = "Open this URL in a separate Layal-controlled Microsoft Edge profile? The website will receive the request and its network-visible metadata. This profile does not reuse your normal browser sign-in session.\n\nURL: " + target.AbsoluteUri;
        if (!await confirm("Navigate Layal browser", warning, token))
            return "User declined browser navigation. No navigation was performed.";
        token.ThrowIfCancellationRequested();
        var result = await _browser.NavigateAsync(target.AbsoluteUri, token);
        report("Layal browser navigation was requested for " + target.AbsoluteUri + "; the site's intended outcome has not been verified.");
        return result;
    }

    private async Task<string> BrowserReadPageAsync(
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (_browser is null)
            throw new InvalidOperationException("The dedicated browser service is not initialized.");
        if (!await confirm("Read browser page", "Read the current page from Layal's dedicated Edge session and share its title, URL, visible text (up to 20,000 characters), and bounded link/button/field metadata with the configured AI provider? Page content may contain personal or confidential information. Navigate in Layal's browser first.", token))
            return "User declined to share the browser page. No page content was read for the AI provider.";
        token.ThrowIfCancellationRequested();
        var result = await _browser.ReadCurrentPageAsync(token);
        report("The current Layal browser page metadata/text was read and returned to the AI provider.");
        return result;
    }

    private async Task<string> BrowserClickAsync(
        string selector,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (_browser is null)
            throw new InvalidOperationException("The dedicated browser service is not initialized.");
        var preview = await _browser.PreviewElementAsync(selector, "click", token);
        var details = $"Page: {preview.PageUrl}\nSelector: {preview.Selector}\nElement: {preview.Tag}\nLabel/name: {Truncate(preview.Label, 160)}\nText: {Truncate(preview.Text, 160)}\nLink URL: {Truncate(preview.Href, 500)}";
        if (!await confirm("Click browser element", "Dispatch one click to this visible standalone link/button? Submit/reset controls and all elements inside or associated with an HTML form are blocked. An allowed page click handler may still navigate or modify remote data. Verify the target and destination before approving.\n\n" + details, token))
            return "User declined browser click. No click was dispatched.";
        token.ThrowIfCancellationRequested();
        var result = await _browser.ClickElementAsync(preview, token);
        report("One browser DOM click event was dispatched after explicit approval. The site-level outcome must be verified.");
        return result;
    }

    private async Task<string> BrowserFillAsync(
        string selector,
        string text,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (_browser is null)
            throw new InvalidOperationException("The dedicated browser service is not initialized.");
        if (text.Length > 10_000) throw new InvalidOperationException("Text entry is limited to 10,000 characters.");
        var preview = await _browser.PreviewElementAsync(selector, "fill", token);
        var details = $"Page: {preview.PageUrl}\nSelector: {preview.Selector}\nField: {preview.Tag} {preview.Type}\nLabel/name: {Truncate(preview.Label, 160)} / {Truncate(preview.Name, 160)}\nText to enter ({text.Length} characters): {Truncate(text, 700)}";
        if (!await confirm("Fill browser field", "Enter this text into one visible field on the current page? The text will be sent to that website. Password and file fields are blocked. Review the text and target before approving.\n\n" + details, token))
            return "User declined browser text entry. No text was entered.";
        token.ThrowIfCancellationRequested();
        var result = await _browser.FillElementAsync(preview, text, token);
        report($"Text-entry events were dispatched to one approved browser field ({text.Length} characters). The site's form state and submission outcome are not confirmed.");
        return result;
    }

    private async Task<string> ListAppApiConnectionsAsync(
        string query,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        CancellationToken token)
    {
        if (_apiConnections is null)
            throw new InvalidOperationException("External app API connections are not initialized. Configure API integrations in Layal first.");
        var result = _apiConnections.SearchDescriptions(query, maxResults: 50);
        if (!await confirm("Share API catalog matches",
                "Layal will share the matching connection names, base URLs, and optional non-secret notes with the configured AI provider. API keys and authentication secrets are not included. Continue?\n\n" + Truncate(result, 2400),
                token))
            return "User declined to share API catalog metadata.";
        token.ThrowIfCancellationRequested();
        return result;
    }

    private async Task<string> CallAppApiAsync(
        string connectionName,
        string method,
        string path,
        string queryJson,
        string bodyJson,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken token)
    {
        if (_apiConnections is null)
            throw new InvalidOperationException("External app API connections are not initialized. Configure API integrations in Layal first.");
        var preview = _apiConnections.PreviewRequest(connectionName, method, path, queryJson, bodyJson);
        var warning =
            "Send this request to the configured external application API? This can read data or create, update, or delete records depending on the method and endpoint. " +
            "The API response will be sent back to the configured AI provider as tool output. Review the method, endpoint, and body carefully. No API credential will be shown to the model.\n\n" +
            preview;
        if (!await confirm("Call external application API", warning, token))
            return "User declined the external API request. No request was sent.";

        token.ThrowIfCancellationRequested();
        var result = await _apiConnections.SendAsync(connectionName, method, path, queryJson, bodyJson, token);
        report($"External API request to '{connectionName}' returned HTTP {result.StatusCode}; response body may have been forwarded to the configured AI provider.");
        return result.ToDisplayText();
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

    private static string BuildBoundedApprovalPreview(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters) return text;
        var suffix = Environment.NewLine + ApprovalPreviewOmissionText;
        if (maxCharacters <= suffix.Length) return text[..maxCharacters];

        var contentLength = maxCharacters - suffix.Length;
        // Do not split a UTF-16 surrogate pair at the preview boundary.
        if (contentLength > 0 && contentLength < text.Length &&
            char.IsHighSurrogate(text[contentLength - 1]) && char.IsLowSurrogate(text[contentLength]))
            contentLength--;

        return text[..contentLength] + suffix;
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
