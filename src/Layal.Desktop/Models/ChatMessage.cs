using System.Windows;

namespace Layal.Models;

public sealed class ChatMessage
{
    public string Role { get; init; } = "assistant";
    public string Content { get; init; } = string.Empty;
    public string DisplayRole { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public FlowDirection Direction { get; set; } = FlowDirection.LeftToRight;
}
