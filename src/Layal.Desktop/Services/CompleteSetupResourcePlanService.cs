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
/// not yet Ready. Per-file pins contribute known bytes even when the complete resource
/// also contains small files whose sizes are intentionally unknown.
/// It does not assert that every component will be downloaded: reusable runtimes may
/// already exist. Optional fallbacks are reported separately.
/// </summary>
public static class CompleteSetupResourcePlanService
{
    public static CompleteSetupResourcePlan Calculate(
        IEnumerable<LocalResourceStateEntry> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var stateById = states
            .GroupBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        bool IsCurrentReady(LocalResourceDefinition resource) =>
            stateById.TryGetValue(resource.Id, out var state) &&
            state.State == LocalResourceLifecycle.Ready &&
            string.Equals(state.DefinitionVersion, LocalResourceCatalog.GetCurrentDefinitionVersion(resource), StringComparison.Ordinal);

        var pending = LocalResourceCatalog.All
            .Where(resource => resource.Required && !IsCurrentReady(resource))
            .ToArray();

        var knownReferences = pending
            .Where(resource => resource.Kind != LocalResourceKind.EmbeddedData)
            .Where(resource => resource.ExpectedSizeBytes is > 0)
            .ToArray();

        var knownBytes = knownReferences.Sum(resource => resource.ExpectedSizeBytes!.Value) +
            pending.SelectMany(resource => resource.ExpectedFiles ?? Array.Empty<LocalResourceFilePin>())
                .Sum(file => file.ExpectedSizeBytes) +
            (pending.Any(resource => resource.Id == LocalResourceIds.ChatModel)
                ? QwenModelVariantCatalog.GetRequired(LocalResourceCatalog.CurrentChatModelId).MaximumDownloadBytes
                : 0L);

        var unknown = pending
            .Where(resource => resource.Kind != LocalResourceKind.EmbeddedData && resource.Kind != LocalResourceKind.ChatModel)
            .Where(resource => resource.ExpectedSizeBytes is null or <= 0)
            .Select(resource => resource.DisplayName)
            .ToArray();

        var ollamaRuntimeIsReady = IsCurrentReady(
            LocalResourceCatalog.GetRequired(LocalResourceIds.OllamaRuntime));
        var conditional = LocalResourceCatalog.All
            .Where(resource => !resource.Required &&
                (resource.Id != LocalResourceIds.OllamaRuntimeInstaller || !ollamaRuntimeIsReady) &&
                resource.Kind != LocalResourceKind.EmbeddedData &&
                resource.ExpectedSizeBytes is > 0 &&
                !IsCurrentReady(resource))
            .ToArray();

        return new CompleteSetupResourcePlan(
            knownBytes,
            pending.Length,
            unknown,
            conditional,
            unknown.Length > 0);
    }
}
