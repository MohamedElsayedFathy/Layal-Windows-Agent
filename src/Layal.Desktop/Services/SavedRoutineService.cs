using System;
using System.Collections.Generic;
using System.Linq;
using Layal.Models;

namespace Layal.Services;

/// <summary>Stores reviewed task templates locally with Windows DPAPI protection.</summary>
public sealed class SavedRoutineService
{
    public const int MaximumRoutines = 50;
    public const int MaximumNameCharacters = 80;
    public const int MaximumGoalCharacters = 2_000;
    public const int MaximumSteps = 20;
    public const int MaximumStepCharacters = 250;
    public const int MaximumTotalStepCharacters = 4_000;
    private readonly ProtectedJsonStore<List<SavedRoutineRecord>> _store;
    private readonly object _sync = new();

    public SavedRoutineService(string path) => _store = new ProtectedJsonStore<List<SavedRoutineRecord>>(path);

    public IReadOnlyList<SavedRoutineRecord> GetAll()
    {
        lock (_sync)
        {
            return LoadAndNormalize()
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Clone())
                .ToList();
        }
    }

    public SavedRoutineRecord Create(string name, string goal, IEnumerable<string>? steps = null)
    {
        var safeName = (name ?? string.Empty).Trim();
        var safeGoal = (goal ?? string.Empty).Trim();
        if (safeName.Length is < 1 or > MaximumNameCharacters)
            throw new ArgumentException($"Routine names must contain 1–{MaximumNameCharacters} characters.", nameof(name));
        if (safeGoal.Length is < 1 or > MaximumGoalCharacters)
            throw new ArgumentException($"Routine prompts must contain 1–{MaximumGoalCharacters} characters.", nameof(goal));
        var safeSteps = NormalizeSteps(steps);

        lock (_sync)
        {
            var routines = LoadAndNormalize();
            if (routines.Count >= MaximumRoutines)
                throw new InvalidOperationException($"The local routine limit of {MaximumRoutines} has been reached.");
            if (routines.Any(item => string.Equals(item.Name, safeName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A routine with this name already exists.");

            var now = DateTimeOffset.Now;
            var routine = new SavedRoutineRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = safeName,
                Goal = safeGoal,
                Steps = safeSteps,
                CreatedAt = now,
                UpdatedAt = now
            };
            routines.Add(routine);
            _store.Save(routines);
            return routine.Clone();
        }
    }

    public SavedRoutineRecord? Update(string id, string name, string goal, IEnumerable<string>? steps = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var safeName = (name ?? string.Empty).Trim();
        var safeGoal = (goal ?? string.Empty).Trim();
        if (safeName.Length is < 1 or > MaximumNameCharacters)
            throw new ArgumentException($"Routine names must contain 1–{MaximumNameCharacters} characters.", nameof(name));
        if (safeGoal.Length is < 1 or > MaximumGoalCharacters)
            throw new ArgumentException($"Routine prompts must contain 1–{MaximumGoalCharacters} characters.", nameof(goal));
        var safeSteps = steps is null ? null : NormalizeSteps(steps);

        lock (_sync)
        {
            var routines = LoadAndNormalize();
            var existing = routines.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (existing is null)
                return null;

            if (routines.Any(item =>
                !string.Equals(item.Id, id, StringComparison.Ordinal) &&
                string.Equals(item.Name, safeName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("A routine with this name already exists.");
            }

            existing.Name = safeName;
            existing.Goal = safeGoal;
            if (safeSteps is not null)
                existing.Steps = safeSteps;
            existing.UpdatedAt = DateTimeOffset.Now;
            _store.Save(routines);
            return existing.Clone();
        }
    }

    public static List<string> NormalizeSteps(IEnumerable<string>? steps)
    {
        var normalized = (steps ?? Array.Empty<string>())
            .Select(step => (step ?? string.Empty).Trim())
            .Where(step => step.Length > 0)
            .ToList();
        if (normalized.Count > MaximumSteps)
            throw new ArgumentException($"A routine can contain at most {MaximumSteps} steps.", nameof(steps));
        if (normalized.Any(step => step.Length > MaximumStepCharacters || step.Any(char.IsControl)))
            throw new ArgumentException($"Each routine step must contain 1–{MaximumStepCharacters} printable characters.", nameof(steps));
        if (normalized.Sum(step => step.Length) > MaximumTotalStepCharacters)
            throw new ArgumentException($"Routine steps cannot exceed {MaximumTotalStepCharacters} characters in total.", nameof(steps));
        return normalized;
    }

    /// <summary>Formats a routine as a user-reviewed task draft; it never starts execution.</summary>
    public static string BuildReviewPrompt(SavedRoutineRecord routine, bool isArabic = false)
    {
        ArgumentNullException.ThrowIfNull(routine);
        var steps = NormalizeSteps(routine.Steps);
        if (steps.Count == 0)
            return routine.Goal;

        var heading = isArabic
            ? "خطوات مقترحة للمراجعة (لم يبدأ التنفيذ):"
            : "Proposed steps to review (no execution has started):";
        return routine.Goal + Environment.NewLine + Environment.NewLine +
            heading + Environment.NewLine +
            string.Join(Environment.NewLine, steps.Select((step, index) => $"{index + 1}. {step}"));
    }

    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;
        lock (_sync)
        {
            var routines = LoadAndNormalize();
            var removed = routines.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
                _store.Save(routines);
            return removed;
        }
    }

    private List<SavedRoutineRecord> LoadAndNormalize()
    {
        var routines = _store.Load(new List<SavedRoutineRecord>());
        foreach (var routine in routines)
        {
            routine.Id = string.IsNullOrWhiteSpace(routine.Id) ? Guid.NewGuid().ToString("N") : routine.Id;
            routine.Name = (routine.Name ?? string.Empty).Trim();
            routine.Goal = (routine.Goal ?? string.Empty).Trim();
            try { routine.Steps = NormalizeSteps(routine.Steps); }
            catch (ArgumentException) { routine.Steps = new List<string>(); }
            if (routine.Name.Length is < 1 or > MaximumNameCharacters ||
                routine.Goal.Length is < 1 or > MaximumGoalCharacters)
            {
                routine.Name = string.IsNullOrWhiteSpace(routine.Name) ? "(invalid saved routine)" : routine.Name;
                routine.Goal = string.Empty;
            }
        }
        return routines.Where(item => !string.IsNullOrWhiteSpace(item.Goal)).ToList();
    }
}
