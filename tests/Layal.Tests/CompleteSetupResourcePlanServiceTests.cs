using System;
using System.Linq;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class CompleteSetupResourcePlanServiceTests
{
    [TestMethod]
    public void PlanSumsKnownCatalogReferencesAndListsUnknownRequiredResources()
    {
        var states = LocalResourceCatalog.All
            .Where(resource => resource.Required && resource.Kind == LocalResourceKind.EmbeddedData)
            .Select(resource => new LocalResourceStateEntry(
                resource.Id, LocalResourceLifecycle.Ready, 0, null, null, null, null, DateTimeOffset.UtcNow,
                LocalResourceCatalog.GetCurrentDefinitionVersion(resource)))
            .ToArray();

        var plan = CompleteSetupResourcePlanService.Calculate(states);

        Assert.IsTrue(plan.KnownReferenceBytes > 0);
        Assert.IsTrue(plan.PendingRequiredResources > 0);
        Assert.IsTrue(plan.HasUnknownSizes);
        Assert.IsTrue(plan.KnownReferenceBytes >=
            QwenModelVariantCatalog.GetRequired(LocalResourceCatalog.CurrentChatModelId).MaximumDownloadBytes,
            "The selected Qwen tag must contribute its published size ceiling to the capacity plan.");
        CollectionAssert.Contains(plan.UnknownSizeResources.ToArray(), "PyTorch and TorchAudio");
        CollectionAssert.Contains(plan.UnknownSizeResources.ToArray(), "VoiceTut Egyptian Arabic and English model");
        CollectionAssert.Contains(
            plan.ConditionalResources.Select(resource => resource.Id).ToArray(),
            LocalResourceIds.OllamaRuntimeInstaller);
    }

    [TestMethod]
    public void KnownVoiceTutBinaryFilesContributeToTheLowerBoundWithoutClaimingTheFullSnapshotSize()
    {
        var states = LocalResourceCatalog.All
            .Where(resource => resource.Required && resource.Id != LocalResourceIds.VoiceTutModel)
            .Select(resource => new LocalResourceStateEntry(
                resource.Id, LocalResourceLifecycle.Ready, 0, null, null, null, null, DateTimeOffset.UtcNow,
                LocalResourceCatalog.GetCurrentDefinitionVersion(resource)))
            .ToArray();

        var plan = CompleteSetupResourcePlanService.Calculate(states);

        Assert.AreEqual(
            2_461_768_130L +
            QwenModelVariantCatalog.GetRequired(LocalResourceCatalog.CurrentChatModelId).MaximumDownloadBytes,
            plan.KnownReferenceBytes);
        Assert.IsTrue(plan.HasUnknownSizes);
        CollectionAssert.Contains(plan.UnknownSizeResources.ToArray(), "VoiceTut Egyptian Arabic and English model");
    }

    [TestMethod]
    public void StaleReadyResourceRemainsPendingInStoragePlan()
    {
        var staleId = LocalResourceIds.WhisperModel;
        var states = LocalResourceCatalog.All
            .Where(resource => resource.Required)
            .Select(resource => new LocalResourceStateEntry(
                resource.Id,
                LocalResourceLifecycle.Ready,
                0,
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow,
                resource.Id == staleId ? "old-definition" : LocalResourceCatalog.GetCurrentDefinitionVersion(resource)))
            .ToArray();

        var plan = CompleteSetupResourcePlanService.Calculate(states);

        Assert.AreEqual(1, plan.PendingRequiredResources);
        Assert.AreEqual(
            LocalResourceCatalog.GetRequired(staleId).ExpectedSizeBytes!.Value,
            plan.KnownReferenceBytes);
        Assert.IsFalse(plan.HasUnknownSizes,
            "All currently unknown-size resources in this test are marked ready with the current definition.");
    }

    [TestMethod]
    public void ReadyResourcesAreExcludedFromPendingSizePlan()
    {
        var states = LocalResourceCatalog.All
            .Where(resource => resource.Required)
            .Select(resource => new LocalResourceStateEntry(
                resource.Id, LocalResourceLifecycle.Ready, 0, null, null, null, null, DateTimeOffset.UtcNow,
                LocalResourceCatalog.GetCurrentDefinitionVersion(resource)))
            .ToArray();

        var plan = CompleteSetupResourcePlanService.Calculate(states);

        Assert.AreEqual(0L, plan.KnownReferenceBytes);
        Assert.AreEqual(0, plan.PendingRequiredResources);
        Assert.IsFalse(plan.HasUnknownSizes);
        Assert.AreEqual(0, plan.UnknownSizeResources.Count);
        Assert.AreEqual(0, plan.ConditionalResources.Count);
    }
}
