using System;

namespace Layal.Models;

/// <summary>A local reminder. Titles and schedules are stored encrypted for the current Windows user.</summary>
public sealed class ReminderRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset DueAt { get; set; }
    public string Recurrence { get; set; } = "None";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastDeliveredAt { get; set; }

    public ReminderRecord Clone() => new()
    {
        Id = Id,
        Title = Title,
        DueAt = DueAt,
        Recurrence = Recurrence,
        IsActive = IsActive,
        LastDeliveredAt = LastDeliveredAt
    };
}
