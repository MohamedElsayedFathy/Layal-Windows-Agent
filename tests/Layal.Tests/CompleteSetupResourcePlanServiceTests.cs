using System;
using System.Linq;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class CompleteSetupResourcePlanServiceTests
{
    [TestMethod]
    public void PlanSumsOnlyKnownSizesAndListsUnknownRequiredDownloads()
    {
        var states = LocalResourceCatalog.All
            .Where(resource => resource.Required && resource.Kind == LocalResourceKind.EmbeddedData)
            .Select(resource => new LocalResourceStateEntry(
                resource.Id, LocalResourceLifecycle.Ready, 0, null, null, null, null, DateTimeOffset.UtcNow))
            .ToArray();

        var plan = CompleteSetupResourcePlanService.Calculate(states);

        Assert.IsTrue(plan.KnownReferenceBytes > 0);
        Assert.IsTrue(plan.PendingRequiredResources > 0);
        Assert.IsTrue(plan.HasUnknownSizes);
        CollectionAssert.Contains(plan.UnknownSizeResources.ToArray(), "Qwen3.5 local chat model");
        CollectionAssert.Contains(plan.UnknownSizeResources.ToArray(), "PyTorch and TorchAudio");
        CollectionAssert.Contains(
            plan.ConditionalResources.Select(resource => resource.Id).ToArray(),
            LocalResourceIds.OllamaRuntimeInstaller);
    }

    [TestMethod]
    public void ReadyResourcesAreExcludedFromPendingSizePlan()
    {
        var states = LocalResourceCatalog.All
            .Where(resource => resource.Required)
            .Select(resource => new LocalResourceStateEntry(
                resource.Id, LocalResourceLifecycle.Ready, 0, null, null, null, null, DateTimeOffset.UtcNow))
            .ToArray();

        var plan = CompleteSetupResourcePlanService.Calculate(states);

        Assert.AreEqual(0L, plan.KnownReferenceBytes);
        Assert.AreEqual(0, plan.PendingRequiredResources);
        Assert.IsFalse(plan.HasUnknownSizes);
        Assert.AreEqual(0, plan.UnknownSizeResources.Count);
        Assert.AreEqual(0, plan.ConditionalResources.Count);
    }
}
