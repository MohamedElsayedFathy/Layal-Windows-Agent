using System;

namespace Layal.Models;

/// <summary>A saved, user-authored task template. It is a prompt, never an executable script.</summary>
public sealed class SavedRoutineRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public SavedRoutineRecord Clone() => new()
    {
        Id = Id,
        Name = Name,
        Goal = Goal,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };
}
