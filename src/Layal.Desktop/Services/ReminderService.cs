using System;
using System.Collections.Generic;
using System.Linq;
using Layal.Models;

namespace Layal.Services;

/// <summary>
/// Persists scheduled reminders in a DPAPI-protected store. It has no network or AI-provider dependency.
/// </summary>
public interface IReminderTaskScheduler
{
    void Schedule(ReminderRecord reminder);
    void Remove(string reminderId);
    void Reconcile(IReadOnlyCollection<ReminderRecord> reminders);
}

/// <summary>
/// Persists scheduled reminders in a DPAPI-protected store. Optional platform scheduling
/// provides delivery while Layal's main window is closed; the in-app timer remains a fallback.
/// </summary>
public sealed class ReminderService
{
    public const int MaximumReminders = 200;
    public const int MaximumTitleCharacters = 200;
    private static readonly string[] AllowedRecurrences = { "None", "Daily", "Weekly" };
    private readonly ProtectedJsonStore<List<ReminderRecord>> _store;
    private readonly IReminderTaskScheduler? _taskScheduler;
    private readonly object _sync = new();

    public ReminderService(string path, IReminderTaskScheduler? taskScheduler = null)
    {
        _store = new ProtectedJsonStore<List<ReminderRecord>>(path);
        _taskScheduler = taskScheduler;
    }

    /// <summary>Recreates only Layal-owned OS tasks from the current encrypted reminder records.</summary>
    public void ReconcileScheduledTasks()
    {
        if (_taskScheduler is null)
            return;
        _taskScheduler.Reconcile(GetAll());
    }

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
            // Persist first so that a task launched immediately can always resolve its reminder.
            _taskScheduler?.Schedule(record.Clone());
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
            _taskScheduler?.Schedule(item.Clone());
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
            {
                _store.Save(items);
                try { _taskScheduler?.Remove(id); }
                catch (InvalidOperationException) { /* A stale OS task is harmless: its ID no longer exists in the store. */ }
                catch (System.ComponentModel.Win32Exception) { /* The next app launch reconciles owned tasks. */ }
            }
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

    private static DateTimeOffset AdvanceLocalTime(DateTimeOffset previousDueAt, DateTimeOffset now, bool daily) =>
        GetNextRecurringDueTime(previousDueAt, now, daily, TimeZoneInfo.Local);

    /// <summary>
    /// Advances a recurring reminder at its original wall-clock time in the selected time zone.
    /// Invalid spring-forward times move forward by the daylight-saving gap. Ambiguous fall-back
    /// times choose the first occurrence strictly after the delivery instant.
    /// </summary>
    public static DateTimeOffset GetNextRecurringDueTime(
        DateTimeOffset previousDueAt,
        DateTimeOffset deliveredAt,
        bool daily,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var intervalDays = daily ? 1 : 7;
        var previousLocal = TimeZoneInfo.ConvertTime(previousDueAt, timeZone).DateTime;
        var deliveredLocal = TimeZoneInfo.ConvertTime(deliveredAt, timeZone).DateTime;
        var candidate = previousLocal.AddDays(intervalDays);

        // A reminder may have been missed while the machine was asleep. Advance by whole
        // local-calendar intervals, not elapsed UTC hours, so its wall-clock schedule remains stable.
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var resolved = ResolveLocalDueTime(candidate, deliveredAt, timeZone);
            if (resolved > deliveredAt)
                return resolved;
            candidate = candidate.AddDays(intervalDays);
        }

        throw new InvalidOperationException("Could not calculate the next recurring reminder time.");
    }

    private static DateTimeOffset ResolveLocalDueTime(
        DateTime localTime,
        DateTimeOffset deliveredAt,
        TimeZoneInfo timeZone)
    {
        var candidate = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(candidate))
        {
            var rule = timeZone.GetAdjustmentRules()
                .FirstOrDefault(item => candidate.Date >= item.DateStart.Date && candidate.Date <= item.DateEnd.Date);
            if (rule is not null && rule.DaylightDelta > TimeSpan.Zero)
                candidate = candidate.Add(rule.DaylightDelta);

            // Some custom zones have non-hour transitions; fail forward to the first valid minute.
            for (var minute = 0; timeZone.IsInvalidTime(candidate) && minute < 240; minute++)
                candidate = candidate.AddMinutes(1);
            if (timeZone.IsInvalidTime(candidate))
                throw new InvalidOperationException("The reminder time falls inside an unresolved daylight-saving gap.");
        }

        if (timeZone.IsAmbiguousTime(candidate))
        {
            var nextOccurrence = timeZone.GetAmbiguousTimeOffsets(candidate)
                .Select(offset => new DateTimeOffset(candidate, offset))
                .Where(value => value > deliveredAt)
                .OrderBy(value => value.UtcDateTime)
                .FirstOrDefault();
            if (nextOccurrence != default)
                return nextOccurrence;

            // Both occurrences have passed; the caller will move to the following interval.
            return new DateTimeOffset(candidate, timeZone.GetUtcOffset(candidate));
        }

        return new DateTimeOffset(candidate, timeZone.GetUtcOffset(candidate));
    }
}
