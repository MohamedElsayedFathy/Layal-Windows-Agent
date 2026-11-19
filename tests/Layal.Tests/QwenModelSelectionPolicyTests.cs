using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class QwenModelSelectionPolicyTests
{
    [TestMethod]
    public void SelectsLargestVariantThatFitsMeasuredNvidiaFreeVram()
    {
        var selection = QwenModelSelectionPolicy.Select(new QwenHardwareProfile(
            TotalRamBytes: 32L * 1024 * 1024 * 1024,
            AvailableRamBytes: 20L * 1024 * 1024 * 1024,
            LogicalProcessorCount: 16,
            TotalDedicatedGpuMemoryBytes: 12L * 1024 * 1024 * 1024,
            AvailableDedicatedGpuMemoryBytes: 10L * 1024 * 1024 * 1024,
            GpuName: "NVIDIA test adapter"));

        Assert.AreEqual("qwen3.5:9b", selection.Variant.ModelId);
        Assert.IsTrue(selection.MemoryBudgetSatisfied);
        Assert.IsTrue(selection.UsesDedicatedGpuBudget);
    }

    [TestMethod]
    public void SelectsFourBWhenEightGbGpuHasNoRoomForNineBHeadroom()
    {
        var selection = QwenModelSelectionPolicy.Select(new QwenHardwareProfile(
            TotalRamBytes: 24L * 1024 * 1024 * 1024,
            AvailableRamBytes: 16L * 1024 * 1024 * 1024,
            LogicalProcessorCount: 12,
            TotalDedicatedGpuMemoryBytes: 8L * 1024 * 1024 * 1024,
            AvailableDedicatedGpuMemoryBytes: 8L * 1024 * 1024 * 1024,
            GpuName: "NVIDIA test adapter"));

        Assert.AreEqual("qwen3.5:4b", selection.Variant.ModelId);
        Assert.IsTrue(selection.UsesDedicatedGpuBudget);
    }

    [TestMethod]
    public void FallsBackToCpuBudgetWhenDedicatedGpuMemoryIsUnknown()
    {
        var selection = QwenModelSelectionPolicy.Select(new QwenHardwareProfile(
            TotalRamBytes: 16L * 1024 * 1024 * 1024,
            AvailableRamBytes: 8L * 1024 * 1024 * 1024,
            LogicalProcessorCount: 8,
            TotalDedicatedGpuMemoryBytes: 24L * 1024 * 1024 * 1024,
            AvailableDedicatedGpuMemoryBytes: null,
            GpuName: "Adapter with unknown free VRAM"));

        Assert.AreEqual("qwen3.5:4b", selection.Variant.ModelId);
        Assert.IsTrue(selection.MemoryBudgetSatisfied);
        Assert.IsFalse(selection.UsesDedicatedGpuBudget);
    }

    [TestMethod]
    public void SelectsTwoBForCpuOnlyProfileWithModerateFreeRam()
    {
        var selection = QwenModelSelectionPolicy.Select(new QwenHardwareProfile(
            TotalRamBytes: 16L * 1024 * 1024 * 1024,
            AvailableRamBytes: 7L * 1024 * 1024 * 1024,
            LogicalProcessorCount: 4,
            TotalDedicatedGpuMemoryBytes: null,
            AvailableDedicatedGpuMemoryBytes: null,
            GpuName: "Integrated graphics"));

        Assert.AreEqual("qwen3.5:2b", selection.Variant.ModelId);
        Assert.IsTrue(selection.MemoryBudgetSatisfied);
        Assert.IsFalse(selection.UsesDedicatedGpuBudget);
    }

    [TestMethod]
    public void RefusesToCallSmallestModelSafeWhenFreeRamIsBelowItsBudget()
    {
        var selection = QwenModelSelectionPolicy.Select(new QwenHardwareProfile(
            TotalRamBytes: 4L * 1024 * 1024 * 1024,
            AvailableRamBytes: 3L * 1024 * 1024 * 1024,
            LogicalProcessorCount: 4,
            TotalDedicatedGpuMemoryBytes: null,
            AvailableDedicatedGpuMemoryBytes: null,
            GpuName: "Integrated graphics"));

        Assert.AreEqual("qwen3.5:0.8b", selection.Variant.ModelId);
        Assert.IsFalse(selection.MemoryBudgetSatisfied);
        StringAssert.Contains(selection.Rationale, "Free memory");
    }

    [TestMethod]
    public void UnknownOrUnapprovedConfiguredModelFallsBackToEstablishedTwoBBaseline()
    {
        Assert.AreEqual(
            "qwen3.5:2b",
            QwenModelVariantCatalog.GetRequired("qwen3.5:27b").ModelId);
        Assert.AreEqual(
            "qwen3.5:2b",
            QwenModelVariantCatalog.GetRequired(null).ModelId);
    }
}
