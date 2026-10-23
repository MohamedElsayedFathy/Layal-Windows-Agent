using System;
using System.Collections.Generic;
using System.Linq;

namespace Layal.Services;

/// <summary>Maps real agent task-engine events to a concise expressive-mascot state.</summary>
public static class TaskMoodResolver
{
    public static string? Resolve(string? text, IEnumerable<string>? recentEvents = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (text.Contains("Task accepted", StringComparison.OrdinalIgnoreCase))
            return "planning";

        if (text.Contains("Requesting model turn", StringComparison.OrdinalIgnoreCase))
            return "thinking";

        if (text.Contains("Model provided an intermediate note", StringComparison.OrdinalIgnoreCase))
            return "responding";

        if (text.Contains("Tool requested:", StringComparison.OrdinalIgnoreCase))
        {
            var marker = text.IndexOf("Tool requested:", StringComparison.OrdinalIgnoreCase);
            var nameStart = marker + "Tool requested:".Length;
            while (nameStart < text.Length && char.IsWhiteSpace(text[nameStart])) nameStart++;

            var nameEnd = nameStart;
            while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] != '(')
                nameEnd++;

            var toolName = text[nameStart..nameEnd].Trim();
            return toolName switch
            {
                "list_workspace_files" or "get_current_time" or "get_system_info" or
                    "get_workspace_info" or "get_open_window_titles" => "researching",
                "read_text_file" or "search_memory" => "reading",
                "create_text_file" or "replace_text_in_file" or "save_memory" => "writing",
                "run_dotnet_build" or "run_dotnet_test" => "coding",
                "activate_window" or "open_notepad" or "open_url" => "using_tool",
                _ => "using_tool"
            };
        }

        if (text.Contains("Tool result recorded:", StringComparison.OrdinalIgnoreCase))
        {
            var recent = (recentEvents ?? Array.Empty<string>()).TakeLast(4).ToArray();
            if (recent.Any(line => line.Contains("Tool failed", StringComparison.OrdinalIgnoreCase)))
                return "error";
            if (recent.Any(line => line.Contains("declined", StringComparison.OrdinalIgnoreCase)))
                return "warning";

            var nameMarker = text.IndexOf("Tool result recorded:", StringComparison.OrdinalIgnoreCase);
            var toolName = text[(nameMarker + "Tool result recorded:".Length)..].Trim().TrimEnd('.');
            return toolName is "run_dotnet_build" or "run_dotnet_test" ? "validating" : "responding";
        }

        if (text.Contains("Tool failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Task failed", StringComparison.OrdinalIgnoreCase))
            return "error";

        if (text.Contains("declined", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("approval", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("requires approval", StringComparison.OrdinalIgnoreCase))
            return "warning";

        return null;
    }
}
