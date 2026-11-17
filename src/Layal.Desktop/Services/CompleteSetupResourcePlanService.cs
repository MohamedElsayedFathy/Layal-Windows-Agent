using System;
using System.Collections.Generic;
using System.Linq;

namespace Layal.Services;

public sealed record CompleteSetupResourcePlan(
    long KnownDownloadBytes,
    int PendingRequiredResources,
    IReadOnlyList<string> UnknownSizeResources,
    bool HasUnknownSizes);

/// <summary>
/// Computes a lower-bound download subtotal from resource-catalog sizes.
/// It deliberately keeps dynamic package/model downloads visible as unknown rather than
/// manufacturing a complete aggregate or equating download bytes with final disk usage.
/// </summary>
public static class CompleteSetupResourcePlanService
{
    public static CompleteSetupResourcePlan Calculate(
        IEnumerable<LocalResourceStateEntry> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var stateById = states
            .GroupBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().State, StringComparer.Ordinal);

        var pending = LocalResourceCatalog.All
            .Where(resource => resource.Required &&
                (!stateById.TryGetValue(resource.Id, out var state) ||
                 state != LocalResourceLifecycle.Ready))
            .ToArray();

        var downloadable = pending
            .Where(resource => resource.Kind != LocalResourceKind.EmbeddedData)
            .ToArray();

        var knownBytes = downloadable
            .Where(resource => resource.ExpectedSizeBytes is > 0)
            .Sum(resource => resource.ExpectedSizeBytes!.Value);

        var unknown = downloadable
            .Where(resource => resource.ExpectedSizeBytes is null or <= 0)
            .Select(resource => resource.DisplayName)
            .ToArray();

        return new CompleteSetupResourcePlan(
            knownBytes,
            pending.Length,
            unknown,
            unknown.Length > 0);
    }
}
