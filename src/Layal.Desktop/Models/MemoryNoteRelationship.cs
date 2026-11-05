namespace Layal.Models;

/// <summary>A user-authored, local-only relationship between two memory notes.</summary>
public sealed class MemoryNoteRelationship
{
    public string TargetNoteId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
