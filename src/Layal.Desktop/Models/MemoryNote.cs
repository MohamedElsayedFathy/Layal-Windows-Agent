namespace Layal.Models;

public sealed class MemoryNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Category { get; set; } = "General";
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<MemoryNoteRelationship> Relationships { get; set; } = new();

    public override string ToString() => $"{Title} — {UpdatedAt.LocalDateTime:g}";
}
