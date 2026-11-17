using System;
using System.Collections.Generic;
using System.Linq;

namespace Layal.Services;

public sealed record CompleteSetupResourcePlan(
    long KnownReferenceBytes,
    int PendingRequiredResources,
    IReadOnlyList<string> UnknownSizeResources,
    IReadOnlyList<LocalResourceDefinition> ConditionalResources,
    bool HasUnknownSizes);

/// <summary>
/// Computes a lower-bound sum of known catalog reference sizes for required resources
/// not yet Ready. It does not assert that every such component will be downloaded:
/// reusable runtimes may already exist. Optional fallbacks are reported separately.
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

        var knownReferences = pending
            .Where(resource => resource.Kind != LocalResourceKind.EmbeddedData)
            .Where(resource => resource.ExpectedSizeBytes is > 0)
            .ToArray();

        var knownBytes = knownReferences.Sum(resource => resource.ExpectedSizeBytes!.Value);

        var unknown = pending
            .Where(resource => resource.Kind != LocalResourceKind.EmbeddedData)
            .Where(resource => resource.ExpectedSizeBytes is null or <= 0)
            .Select(resource => resource.DisplayName)
            .ToArray();

        var ollamaRuntimeIsReady =
            stateById.TryGetValue(LocalResourceIds.OllamaRuntime, out var ollamaState) &&
            ollamaState == LocalResourceLifecycle.Ready;
        var conditional = LocalResourceCatalog.All
            .Where(resource => !resource.Required &&
                (resource.Id != LocalResourceIds.OllamaRuntimeInstaller || !ollamaRuntimeIsReady) &&
                resource.Kind != LocalResourceKind.EmbeddedData &&
                resource.ExpectedSizeBytes is > 0 &&
                (!stateById.TryGetValue(resource.Id, out var state) ||
                 state != LocalResourceLifecycle.Ready))
            .ToArray();

        return new CompleteSetupResourcePlan(
            knownBytes,
            pending.Length,
            unknown,
            conditional,
            unknown.Length > 0);
    }
}
