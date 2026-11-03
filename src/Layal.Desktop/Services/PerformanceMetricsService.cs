using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

public sealed record PerformanceMeasurement(
    DateTimeOffset CapturedAt,
    TimeSpan SamplingDuration,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    long ManagedHeapBytes,
    int ThreadCount,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

/// <summary>
/// Measures only the current Layal process. No system-wide process inventory or GPU telemetry is collected.
/// </summary>
public static class PerformanceMetricsService
{
    public static async Task<PerformanceMeasurement> MeasureCurrentProcessAsync(
        TimeSpan samplingWindow,
        CancellationToken cancellationToken = default)
    {
        if (samplingWindow < TimeSpan.FromMilliseconds(100) ||
            samplingWindow > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(samplingWindow), "Performance sampling must be between 100 ms and 5 seconds.");

        var before = Capture();
        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(samplingWindow, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        var after = Capture();
        var cpu = CalculateCpuPercent(after.CpuTime - before.CpuTime, stopwatch.Elapsed, Environment.ProcessorCount);

        return new PerformanceMeasurement(
            after.CapturedAt,
            stopwatch.Elapsed,
            cpu,
            after.WorkingSetBytes,
            after.PrivateBytes,
            after.ManagedHeapBytes,
            after.ThreadCount,
            after.Gen0Collections,
            after.Gen1Collections,
            after.Gen2Collections);
    }

    public static double CalculateCpuPercent(TimeSpan cpuTimeDelta, TimeSpan elapsed, int logicalProcessorCount)
    {
        if (cpuTimeDelta <= TimeSpan.Zero || elapsed <= TimeSpan.Zero || logicalProcessorCount <= 0)
            return 0;

        var percentage = cpuTimeDelta.TotalMilliseconds /
            (elapsed.TotalMilliseconds * logicalProcessorCount) * 100.0;
        if (double.IsNaN(percentage) || double.IsInfinity(percentage))
            return 0;
        return Math.Clamp(percentage, 0, 100);
    }

    private static ProcessSnapshot Capture()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProcessSnapshot(
            DateTimeOffset.UtcNow,
            process.TotalProcessorTime,
            Math.Max(0, process.WorkingSet64),
            Math.Max(0, process.PrivateMemorySize64),
            Math.Max(0, GC.GetTotalMemory(forceFullCollection: false)),
            Math.Max(0, process.Threads.Count),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    private sealed record ProcessSnapshot(
        DateTimeOffset CapturedAt,
        TimeSpan CpuTime,
        long WorkingSetBytes,
        long PrivateBytes,
        long ManagedHeapBytes,
        int ThreadCount,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections);
}
