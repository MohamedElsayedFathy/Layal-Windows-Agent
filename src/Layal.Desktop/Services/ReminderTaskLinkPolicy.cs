using System;
using System.Collections.Generic;
using System.Linq;
using Layal.Models;

namespace Layal.Services;

/// <summary>
/// Resolves an actionable task target for a digest of simultaneously delivered reminders.
/// A digest may deep-link only when every linked reminder refers to the same exact task.
/// </summary>
public static class ReminderTaskLinkPolicy
{
    public static string? GetUniqueLinkedTaskId(IEnumerable<ReminderRecord> reminders)
    {
        ArgumentNullException.ThrowIfNull(reminders);

        var linkedIds = reminders
            .Where(reminder => reminder is not null)
            .Select(reminder => reminder.RelatedTaskId?.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();

        return linkedIds.Length == 1 ? linkedIds[0] : null;
    }
}
