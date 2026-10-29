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

    public SavedRoutineRecord Create(string name, string goal)
    {
        var safeName = (name ?? string.Empty).Trim();
        var safeGoal = (goal ?? string.Empty).Trim();
        if (safeName.Length is < 1 or > MaximumNameCharacters)
            throw new ArgumentException($"Routine names must contain 1–{MaximumNameCharacters} characters.", nameof(name));
        if (safeGoal.Length is < 1 or > MaximumGoalCharacters)
            throw new ArgumentException($"Routine prompts must contain 1–{MaximumGoalCharacters} characters.", nameof(goal));

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
                CreatedAt = now,
                UpdatedAt = now
            };
            routines.Add(routine);
            _store.Save(routines);
            return routine.Clone();
        }
    }

    public SavedRoutineRecord? Update(string id, string name, string goal)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var safeName = (name ?? string.Empty).Trim();
        var safeGoal = (goal ?? string.Empty).Trim();
        if (safeName.Length is < 1 or > MaximumNameCharacters)
            throw new ArgumentException($"Routine names must contain 1–{MaximumNameCharacters} characters.", nameof(name));
        if (safeGoal.Length is < 1 or > MaximumGoalCharacters)
            throw new ArgumentException($"Routine prompts must contain 1–{MaximumGoalCharacters} characters.", nameof(goal));

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
            existing.UpdatedAt = DateTimeOffset.Now;
            _store.Save(routines);
            return existing.Clone();
        }
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
