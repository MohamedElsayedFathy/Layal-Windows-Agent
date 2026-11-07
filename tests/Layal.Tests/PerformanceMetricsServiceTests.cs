using System;
using System.Threading;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class PerformanceMetricsServiceTests
{
    [TestMethod]
    public void CpuPercentageIsNormalizedByLogicalProcessorCountAndBounded()
    {
        Assert.AreEqual(50d,
            PerformanceMetricsService.CalculateCpuPercent(
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1),
                logicalProcessorCount: 1),
            0.001d);

        Assert.AreEqual(25d,
            PerformanceMetricsService.CalculateCpuPercent(
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1),
                logicalProcessorCount: 2),
            0.001d);

        Assert.AreEqual(100d,
            PerformanceMetricsService.CalculateCpuPercent(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(1),
                logicalProcessorCount: 2),
            0.001d);
    }

    [TestMethod]
    public void InvalidCpuSamplesReturnZero()
    {
        Assert.AreEqual(0d, PerformanceMetricsService.CalculateCpuPercent(
            TimeSpan.Zero, TimeSpan.FromSeconds(1), 4));
        Assert.AreEqual(0d, PerformanceMetricsService.CalculateCpuPercent(
            TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
        Assert.AreEqual(0d, PerformanceMetricsService.CalculateCpuPercent(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0));
        Assert.AreEqual(0d, PerformanceMetricsService.CalculateCpuPercent(
            TimeSpan.FromMilliseconds(-1), TimeSpan.FromSeconds(1), 4));
        Assert.AreEqual(0d, PerformanceMetricsService.CalculateCpuPercent(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), -1));
    }

    [TestMethod]
    public async Task MeasurementReportsOnlyCurrentProcessMetrics()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var metrics = await PerformanceMetricsService.MeasureCurrentProcessAsync(
            TimeSpan.FromMilliseconds(150), timeout.Token);

        Assert.IsTrue(metrics.CpuPercent is >= 0 and <= 100);
        Assert.IsTrue(metrics.WorkingSetBytes > 0);
        Assert.IsTrue(metrics.PrivateBytes > 0);
        Assert.IsTrue(metrics.ManagedHeapBytes >= 0);
        Assert.IsTrue(metrics.ThreadCount > 0);
        Assert.IsTrue(metrics.SamplingDuration >= TimeSpan.FromMilliseconds(100));
        Assert.IsTrue(metrics.Gen0Collections >= 0);
        Assert.IsTrue(metrics.Gen1Collections >= 0);
        Assert.IsTrue(metrics.Gen2Collections >= 0,
            "GC counters must describe collections during the sample window, not lifetime totals.");
    }

    [TestMethod]
    public async Task SamplingWindowIsBoundedAndCancellationIsHonored()
    {
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            PerformanceMetricsService.MeasureCurrentProcessAsync(TimeSpan.FromMilliseconds(50)));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            PerformanceMetricsService.MeasureCurrentProcessAsync(TimeSpan.FromSeconds(5.1)));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellationObserved = false;
        try
        {
            await PerformanceMetricsService.MeasureCurrentProcessAsync(
                TimeSpan.FromSeconds(1),
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        Assert.IsTrue(cancellationObserved, "A cancelled measurement must not wait for the sampling window or return a result.");
    }
    [TestMethod]
    public void GarbageCollectionDeltaReportsOnlyCollectionsWithinTheSample()
    {
        Assert.AreEqual(3, PerformanceMetricsService.CalculateCollectionDelta(10, 13));
        Assert.AreEqual(0, PerformanceMetricsService.CalculateCollectionDelta(13, 13));
        Assert.AreEqual(0, PerformanceMetricsService.CalculateCollectionDelta(13, 2),
            "A counter reset or wrap must not produce a negative collection count.");
    }

    [TestMethod]
    public async Task CancellationDuringSamplingStopsTheMeasurement()
    {
        using var cancellation = new CancellationTokenSource();
        var measurement = PerformanceMetricsService.MeasureCurrentProcessAsync(
            TimeSpan.FromSeconds(2), cancellation.Token);
        await Task.Delay(50);
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => measurement);
    }

}
