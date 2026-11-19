using System;
using System.Collections.Generic;
using System.Linq;

namespace Layal.Services;

/// <summary>
/// The first-run gate is complete only when every required catalog entry has a
/// persisted Ready state. A missing or failed resource always keeps the gate open.
/// </summary>
public static class CompleteSetupReadinessPolicy
{
    public static bool IsComplete(IEnumerable<LocalResourceStateEntry> states) =>
        GetMissingRequiredResources(states).Count == 0;

    public static IReadOnlyList<LocalResourceDefinition> GetMissingRequiredResources(
        IEnumerable<LocalResourceStateEntry> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var definitionsById = LocalResourceCatalog.All.ToDictionary(
            resource => resource.Id, StringComparer.Ordinal);
        var readyIds = new HashSet<string>(
            states
                .Where(item => item.State == LocalResourceLifecycle.Ready &&
                    definitionsById.TryGetValue(item.ResourceId, out var definition) &&
                    string.Equals(item.DefinitionVersion, definition.Version, StringComparison.Ordinal))
                .Select(item => item.ResourceId),
            StringComparer.Ordinal);

        return LocalResourceCatalog.All
            .Where(resource => resource.Required && !readyIds.Contains(resource.Id))
            .ToArray();
    }
}
