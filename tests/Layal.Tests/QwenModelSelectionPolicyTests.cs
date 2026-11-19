using System.Linq;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class QwenModelSelectionPolicyTests
{
    [TestMethod]
    public void CatalogContainsOnlySupportedOfficialQwen35TagsAndKnownSizeCeilings()
    {
        CollectionAssert.AreEqual(
            new[] { "qwen3.5:0.8b", "qwen3.5:2b", "qwen3.5:4b", "qwen3.5:9b" },
            QwenModelVariantCatalog.All.Select(item => item.ModelId).ToArray());

        Assert.AreEqual(1_300_000_000L,
            QwenModelVariantCatalog.GetRequired("qwen3.5:0.8b").MaximumDownloadBytes);
        Assert.AreEqual(3_100_000_000L,
            QwenModelVariantCatalog.GetRequired("qwen3.5:2b").MaximumDownloadBytes);
        Assert.AreEqual(4_000_000_000L,
            QwenModelVariantCatalog.GetRequired("qwen3.5:4b").MaximumDownloadBytes);
        Assert.AreEqual(7_600_000_000L,
            QwenModelVariantCatalog.GetRequired("qwen3.5:9b").MaximumDownloadBytes);
    }

    [TestMethod]
    public void SelectsTwoBWhenSixGbVramCoversItsBudgetButNotFourB()
    {
        var selection = QwenModelSelectionPolicy.Select(new QwenHardwareProfile(
            TotalRamBytes: 16L * 1024 * 1024 * 1024,
            AvailableRamBytes: 9L * 1024 * 1024 * 1024,
            LogicalProcessorCount: 8,
            TotalDedicatedGpuMemoryBytes: 8L * 1024 * 1024 * 1024,
            AvailableDedicatedGpuMemoryBytes: 6_000_000_000,
            GpuName: "NVIDIA test adapter"));

        Assert.AreEqual("qwen3.5:2b", selection.Variant.ModelId);
        Assert.IsTrue(selection.MemoryBudgetSatisfied);
        Assert.IsTrue(selection.UsesDedicatedGpuBudget);
    }

    [TestMethod]
    public void SelectsFourBWhenVramCoversItsBudgetButNotNineB()
    {
        var selection = QwenModelSelectionPolicy.Select(new QwenHardwareProfile(
            TotalRamBytes: 32L * 1024 * 1024 * 1024,
            AvailableRamBytes: 12L * 1024 * 1024 * 1024,
            LogicalProcessorCount: 8,
            TotalDedicatedGpuMemoryBytes: 12L * 1024 * 1024 * 1024,
            AvailableDedicatedGpuMemoryBytes: 8_000_000_000,
            GpuName: "NVIDIA test adapter"));

        Assert.AreEqual("qwen3.5:4b", selection.Variant.ModelId);
        Assert.IsTrue(selection.MemoryBudgetSatisfied);
        Assert.IsTrue(selection.UsesDedicatedGpuBudget);
    }

    [TestMethod]
    public void SelectsNineBOnlyWhenFreeVramAndSystemRamMeetConservativeBudgets()
    {
        var selection = QwenModelSelectionPolicy.Select(new QwenHardwareProfile(
            TotalRamBytes: 32L * 1024 * 1024 * 1024,
            AvailableRamBytes: 6L * 1024 * 1024 * 1024,
            LogicalProcessorCount: 8,
            TotalDedicatedGpuMemoryBytes: 16L * 1024 * 1024 * 1024,
            AvailableDedicatedGpuMemoryBytes: 13L * 1024 * 1024 * 1024,
            GpuName: "NVIDIA test adapter"));

        Assert.AreEqual("qwen3.5:9b", selection.Variant.ModelId);
        Assert.IsTrue(selection.MemoryBudgetSatisfied);
        Assert.IsTrue(selection.UsesDedicatedGpuBudget);
    }

    [TestMethod]
    public void CpuOnlySelectionUsesAvailableSystemRamAndCanChooseNineBWithEnoughHeadroom()
    {
        var selection = QwenModelSelectionPolicy.Select(new QwenHardwareProfile(
            TotalRamBytes: 32L * 1024 * 1024 * 1024,
            AvailableRamBytes: 20L * 1024 * 1024 * 1024,
            LogicalProcessorCount: 12,
            TotalDedicatedGpuMemoryBytes: null,
            AvailableDedicatedGpuMemoryBytes: null,
            GpuName: "Integrated graphics"));

        Assert.AreEqual("qwen3.5:9b", selection.Variant.ModelId);
        Assert.IsTrue(selection.MemoryBudgetSatisfied);
        Assert.IsFalse(selection.UsesDedicatedGpuBudget);
        StringAssert.Contains(selection.Rationale, "available system RAM");
    }

    [TestMethod]
    public void SelectsTwoBWhenCpuOnlyMemoryBudgetIsModerate()
    {
        var selection = QwenModelSelectionPolicy.Select(new QwenHardwareProfile(
            TotalRamBytes: 16L * 1024 * 1024 * 1024,
            AvailableRamBytes: 9L * 1024 * 1024 * 1024,
            LogicalProcessorCount: 8,
            TotalDedicatedGpuMemoryBytes: null,
            AvailableDedicatedGpuMemoryBytes: null,
            GpuName: "Integrated graphics"));

        Assert.AreEqual("qwen3.5:2b", selection.Variant.ModelId);
        Assert.IsTrue(selection.MemoryBudgetSatisfied);
        Assert.IsFalse(selection.UsesDedicatedGpuBudget);
    }

    [TestMethod]
    public void BlocksFullSetupWhenEvenSmallestVariantExceedsAvailableMemoryBudget()
    {
        var selection = QwenModelSelectionPolicy.Select(new QwenHardwareProfile(
            TotalRamBytes: 8L * 1024 * 1024 * 1024,
            AvailableRamBytes: 3L * 1024 * 1024 * 1024,
            LogicalProcessorCount: 4,
            TotalDedicatedGpuMemoryBytes: null,
            AvailableDedicatedGpuMemoryBytes: null,
            GpuName: "Integrated graphics"));

        Assert.AreEqual("qwen3.5:0.8b", selection.Variant.ModelId);
        Assert.IsFalse(selection.MemoryBudgetSatisfied);
        Assert.IsFalse(selection.UsesDedicatedGpuBudget);
    }

    [TestMethod]
    public void FallsBackToValidatedTwoBBaselineWhenHardwareTelemetryIsUnavailable()
    {
        var selection = QwenModelSelectionPolicy.Select(new QwenHardwareProfile(
            TotalRamBytes: null,
            AvailableRamBytes: null,
            LogicalProcessorCount: 0,
            TotalDedicatedGpuMemoryBytes: null,
            AvailableDedicatedGpuMemoryBytes: null,
            GpuName: "unknown"));

        Assert.AreEqual(QwenModelVariantCatalog.DefaultModelId, selection.Variant.ModelId);
        Assert.IsTrue(selection.MemoryBudgetSatisfied);
        Assert.IsFalse(selection.UsesDedicatedGpuBudget);
    }

    [TestMethod]
    public void RejectsModelTagsOutsideAllowListedVariantCatalog()
    {
        Assert.AreEqual(
            QwenModelVariantCatalog.DefaultModelId,
            QwenModelVariantCatalog.GetRequired("qwen3.5:27b").ModelId);
        Assert.IsFalse(QwenModelVariantCatalog.TryGet("qwen3.5:27b", out _));
    }
}
