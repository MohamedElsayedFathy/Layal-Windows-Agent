using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

[Flags]
public enum PermissionActionCategory
{
    None = 0,
    Read = 1,
    LocalProcessing = 2,
    Modify = 4,
    Execute = 8,
    ExternalShare = 16,
    PublishOrUpload = 32,
    Delete = 64,
    SystemChange = 128,
    Unknown = 256
}

/// <summary>
/// Describes one action that is about to be shown to the local user for approval.
/// Details are passed to the dialog but are deliberately excluded from audit events.
/// </summary>
public sealed record PermissionRequest(
    string ToolName,
    PermissionActionCategory Category,
    string Target,
    string Reason,
    string Details);

/// <summary>
/// Routes every approval prompt for agent tools through one typed request and records
/// the local user's decision without copying the potentially sensitive dialog details.
/// </summary>
public sealed class PermissionBroker
{
    public async Task<bool> RequestAsync(
        PermissionRequest request,
        Func<string, string, CancellationToken, Task<bool>> confirm,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();

        report(FormatAuditEvent("requested", request));
        bool approved;
        try
        {
            approved = await confirm(request.Reason, request.Details, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            report(FormatAuditEvent("cancelled", request));
            throw;
        }

        report(FormatAuditEvent(approved ? "approved" : "denied", request));
        return approved;
    }

    private static string FormatAuditEvent(string decision, PermissionRequest request) =>
        $"Permission {decision}: actor=local user; tool={Sanitize(request.ToolName, 80)}; " +
        $"category={request.Category}; target={Sanitize(request.Target, 200)}; " +
        $"reason={Sanitize(request.Reason, 120)}.";

    private static string Sanitize(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unspecified";
        var safe = new string(value.Where(character => !char.IsControl(character)).ToArray())
            .Replace(';', ',')
            .Trim();
        return safe.Length <= maximumLength ? safe : safe[..maximumLength] + "…";
    }
}

public static class PermissionRequestClassifier
{
    public static PermissionRequest Create(
        string toolName,
        JsonObject arguments,
        bool keepReadDataLocal,
        string reason,
        string details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(arguments);

        var category = Classify(toolName, arguments, keepReadDataLocal);
        return new PermissionRequest(
            toolName,
            category,
            GetTarget(toolName, arguments),
            reason,
            details);
    }

    private static PermissionActionCategory Classify(string toolName, JsonObject arguments, bool keepReadDataLocal) => toolName switch
    {
        "read_text_file" => keepReadDataLocal
            ? PermissionActionCategory.Read | PermissionActionCategory.LocalProcessing
            : PermissionActionCategory.Read | PermissionActionCategory.ExternalShare,
        "list_workspace_files" or "get_current_time" or "get_system_info" or "get_workspace_info" => PermissionActionCategory.Read,
        "search_memory" or "list_app_api_connections" => PermissionActionCategory.Read | PermissionActionCategory.ExternalShare,
        "get_open_window_titles" or "inspect_window_controls" or "browser_read_page" => PermissionActionCategory.Read | PermissionActionCategory.ExternalShare,
        "create_text_file" or "replace_text_in_file" or "save_memory" => PermissionActionCategory.Modify,
        "launch_allowed_program" or "open_notepad" or "activate_window" or "run_dotnet_build" or "run_dotnet_test" => PermissionActionCategory.Execute,
        "open_url" => PermissionActionCategory.Execute | PermissionActionCategory.ExternalShare,
        "browser_navigate" or "web_search" or "fact_check_claim" => PermissionActionCategory.ExternalShare,
        "browser_click" => PermissionActionCategory.Execute | PermissionActionCategory.ExternalShare,
        "browser_fill" => PermissionActionCategory.Modify | PermissionActionCategory.ExternalShare,
        "browser_download" => PermissionActionCategory.Read | PermissionActionCategory.Modify | PermissionActionCategory.ExternalShare,
        "browser_upload_file" => PermissionActionCategory.ExternalShare | PermissionActionCategory.PublishOrUpload,
        "click_ui_control" => PermissionActionCategory.Execute,
        "fill_ui_text" or "set_ui_checkbox" => PermissionActionCategory.Modify | PermissionActionCategory.Execute,
        "call_app_api" => ClassifyApiCall(arguments),
        _ => PermissionActionCategory.Unknown
    };

    private static PermissionActionCategory ClassifyApiCall(JsonObject arguments)
    {
        var method = ReadString(arguments, "method").ToUpperInvariant();
        return method switch
        {
            "GET" or "HEAD" => PermissionActionCategory.Read | PermissionActionCategory.ExternalShare,
            "DELETE" => PermissionActionCategory.Delete | PermissionActionCategory.ExternalShare,
            "POST" or "PUT" or "PATCH" => PermissionActionCategory.Modify | PermissionActionCategory.ExternalShare,
            _ => PermissionActionCategory.Unknown | PermissionActionCategory.ExternalShare
        };
    }

    private static string GetTarget(string toolName, JsonObject arguments)
    {
        if (toolName == "call_app_api")
        {
            var connection = ReadString(arguments, "connection_name");
            var method = ReadString(arguments, "method");
            var path = ReadString(arguments, "path");
            return string.Join(" ", new[] { method, connection, path }.Where(value => value.Length > 0));
        }

        if (arguments["url"] is JsonValue urlNode && urlNode.TryGetValue<string>(out var url) && url is not null &&
            Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Scheme + "://" + uri.Authority + uri.AbsolutePath;

        if (toolName == "web_search") return "Bing search";
        if (toolName == "fact_check_claim") return "Bing and selected source pages";
        if (toolName == "browser_read_page") return "active Layal Edge page";
        if (toolName == "get_open_window_titles") return "open Windows window titles";
        if (toolName == "list_app_api_connections") return "saved API connection catalog";
        if (toolName == "search_memory") return "local memory notes";

        var fields = toolName switch
        {
            "read_text_file" or "create_text_file" or "replace_text_in_file" or "run_dotnet_build" or "run_dotnet_test" => new[] { "path", "project_file" },
            "browser_upload_file" => new[] { "path", "selector" },
            "browser_download" => new[] { "selector" },
            "launch_allowed_program" => new[] { "program" },
            "activate_window" or "inspect_window_controls" => new[] { "title", "window_title" },
            "click_ui_control" or "fill_ui_text" or "set_ui_checkbox" => new[] { "window_title", "automation_id" },
            "browser_click" or "browser_fill" => new[] { "selector" },
            "web_search" or "fact_check_claim" => Array.Empty<string>(),
            "save_memory" => new[] { "title" },
            _ => Array.Empty<string>()
        };

        return string.Join(" ", fields.Select(field => ReadString(arguments, field)).Where(value => value.Length > 0));
    }

    private static string ReadString(JsonObject arguments, string propertyName) =>
        arguments[propertyName] is JsonValue value && value.TryGetValue<string>(out var text) && text is not null
            ? new string(text.Where(character => !char.IsControl(character)).ToArray()).Trim()
            : string.Empty;
}

