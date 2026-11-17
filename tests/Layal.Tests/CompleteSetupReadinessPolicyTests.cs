using System;
using System.Linq;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class CompleteSetupReadinessPolicyTests
{
    [TestMethod]
    public void CompleteSetupRequiresEveryRequiredCatalogResourceToBeReady()
    {
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
                DateTimeOffset.UtcNow))
            .ToArray();

        Assert.IsTrue(CompleteSetupReadinessPolicy.IsComplete(states));
        Assert.AreEqual(0, CompleteSetupReadinessPolicy.GetMissingRequiredResources(states).Count);
    }

    [TestMethod]
    public void MissingOrFailedRequiredResourceKeepsFirstRunGateOpen()
    {
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
                DateTimeOffset.UtcNow))
            .ToList();

        states.RemoveAt(states.Count - 1);
        Assert.IsFalse(CompleteSetupReadinessPolicy.IsComplete(states));

        var failed = LocalResourceCatalog.All.First(resource => resource.Required);
        var failedIndex = states.FindIndex(item =>
            string.Equals(item.ResourceId, failed.Id, StringComparison.Ordinal));
        Assert.IsTrue(failedIndex >= 0);
        states[failedIndex] = new LocalResourceStateEntry(
            failed.Id,
            LocalResourceLifecycle.Failed,
            0,
            null,
            null,
            null,
            "verification failed",
            DateTimeOffset.UtcNow);

        Assert.IsFalse(CompleteSetupReadinessPolicy.IsComplete(states));
        Assert.IsTrue(CompleteSetupReadinessPolicy.GetMissingRequiredResources(states)
            .Any(resource => resource.Id == failed.Id));
    }

    [TestMethod]
    public void OptionalResourcesDoNotBlockFirstRunCompletion()
    {
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
                DateTimeOffset.UtcNow))
            .ToArray();

        Assert.IsTrue(CompleteSetupReadinessPolicy.IsComplete(states));
    }
}
