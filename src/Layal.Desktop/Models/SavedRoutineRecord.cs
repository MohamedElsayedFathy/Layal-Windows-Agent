using System;
using System.Collections.Generic;
using System.Linq;

namespace Layal.Models;

/// <summary>A saved, user-authored task template. It is a prompt, never an executable script.</summary>
public sealed class SavedRoutineRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public List<string> Steps { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public SavedRoutineRecord Clone() => new()
    {
        Id = Id,
        Name = Name,
        Goal = Goal,
        Steps = Steps?.ToList() ?? new List<string>(),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };
}
