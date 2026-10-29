using System;
using System.Collections.Generic;
using System.Linq;
using Layal.Models;

namespace Layal.Services;

/// <summary>
/// Persists scheduled reminders in a DPAPI-protected store. It has no network or AI-provider dependency.
/// </summary>
public sealed class ReminderService
{
    public const int MaximumReminders = 200;
    public const int MaximumTitleCharacters = 200;
    private static readonly string[] AllowedRecurrences = { "None", "Daily", "Weekly" };
    private readonly ProtectedJsonStore<List<ReminderRecord>> _store;
    private readonly object _sync = new();

    public ReminderService(string path) =>
        _store = new ProtectedJsonStore<List<ReminderRecord>>(path);

    public IReadOnlyList<ReminderRecord> GetAll()
    {
        lock (_sync)
        {
            var reminders = LoadAndNormalize();
            return reminders
                .OrderBy(r => r.DueAt)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .Select(r => r.Clone())
                .ToList();
        }
    }

    public ReminderRecord Create(string title, DateTimeOffset dueAt, string recurrence = "None")
    {
        var normalizedTitle = (title ?? string.Empty).Trim();
        if (normalizedTitle.Length is < 1 or > MaximumTitleCharacters)
            throw new ArgumentException($"Reminder titles must contain 1–{MaximumTitleCharacters} characters.", nameof(title));
        if (dueAt <= DateTimeOffset.Now)
            throw new ArgumentOutOfRangeException(nameof(dueAt), "The reminder time must be in the future.");
        var normalizedRecurrence = NormalizeRecurrence(recurrence);

        lock (_sync)
        {
            var items = LoadAndNormalize();
            if (items.Count >= MaximumReminders)
                throw new InvalidOperationException($"The local reminder limit of {MaximumReminders} has been reached.");

            var record = new ReminderRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = normalizedTitle,
                DueAt = dueAt,
                Recurrence = normalizedRecurrence,
                IsActive = true
            };
            items.Add(record);
            _store.Save(items);
            return record.Clone();
        }
    }

    public ReminderRecord? Update(string id, string title, DateTimeOffset dueAt, string recurrence = "None")
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var normalizedTitle = (title ?? string.Empty).Trim();
        if (normalizedTitle.Length is < 1 or > MaximumTitleCharacters)
            throw new ArgumentException($"Reminder titles must contain 1–{MaximumTitleCharacters} characters.", nameof(title));
        if (dueAt <= DateTimeOffset.Now)
            throw new ArgumentOutOfRangeException(nameof(dueAt), "The reminder time must be in the future.");
        var normalizedRecurrence = NormalizeRecurrence(recurrence);

        lock (_sync)
        {
            var items = LoadAndNormalize();
            var item = items.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (item is null)
                return null;

            item.Title = normalizedTitle;
            item.DueAt = dueAt;
            item.Recurrence = normalizedRecurrence;
            item.IsActive = true;
            item.LastDeliveredAt = null;
            _store.Save(items);
            return item.Clone();
        }
    }

    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        lock (_sync)
        {
            var items = LoadAndNormalize();
            var removed = items.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
                _store.Save(items);
            return removed;
        }
    }

    public IReadOnlyList<ReminderRecord> GetDue(DateTimeOffset now)
    {
        lock (_sync)
        {
            return LoadAndNormalize()
                .Where(item => item.IsActive && item.DueAt <= now)
                .OrderBy(item => item.DueAt)
                .Select(item => item.Clone())
                .ToList();
        }
    }

    /// <summary>
    /// Marks a reminder as delivered only when its expected due time still matches, preventing duplicate
    /// notifications from overlapping timer ticks. Recurring items advance past missed intervals on resume.
    /// </summary>
    public ReminderRecord? MarkDelivered(string id, DateTimeOffset expectedDueAt, DateTimeOffset deliveredAt)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        lock (_sync)
        {
            var items = LoadAndNormalize();
            var item = items.FirstOrDefault(candidate =>
                candidate.IsActive &&
                string.Equals(candidate.Id, id, StringComparison.Ordinal) &&
                candidate.DueAt == expectedDueAt);

            if (item is null || item.DueAt > deliveredAt)
                return null;

            var delivered = item.Clone();
            item.LastDeliveredAt = deliveredAt;
            if (string.Equals(item.Recurrence, "Daily", StringComparison.Ordinal))
                item.DueAt = AdvanceLocalTime(item.DueAt, deliveredAt, daily: true);
            else if (string.Equals(item.Recurrence, "Weekly", StringComparison.Ordinal))
                item.DueAt = AdvanceLocalTime(item.DueAt, deliveredAt, daily: false);
            else
                item.IsActive = false;

            _store.Save(items);
            return delivered;
        }
    }

    private List<ReminderRecord> LoadAndNormalize()
    {
        var items = _store.Load(new List<ReminderRecord>());
        foreach (var item in items)
        {
            item.Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id;
            item.Title = (item.Title ?? string.Empty).Trim();
            item.Recurrence = NormalizeRecurrence(item.Recurrence);
            if (string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > MaximumTitleCharacters)
                item.IsActive = false;
        }
        return items;
    }

    private static string NormalizeRecurrence(string? value) =>
        AllowedRecurrences.FirstOrDefault(
            allowed => string.Equals(allowed, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "None";

    private static DateTimeOffset AdvanceLocalTime(DateTimeOffset previousDueAt, DateTimeOffset now, bool daily)
    {
        var intervalDays = daily ? 1 : 7;
        var nextLocal = previousDueAt.LocalDateTime;
        var nowLocal = now.LocalDateTime;
        var elapsedDays = Math.Max(0, (nowLocal - nextLocal).TotalDays);
        var intervals = Math.Max(1, (int)Math.Floor(elapsedDays / intervalDays) + 1);
        nextLocal = nextLocal.AddDays(intervals * intervalDays);
        // Reconstruct using DateTimeKind.Local so Windows applies the local UTC offset for that date
        // instead of retaining yesterday's offset across a daylight-saving boundary.
        return new DateTimeOffset(DateTime.SpecifyKind(nextLocal, DateTimeKind.Local));
    }
}
