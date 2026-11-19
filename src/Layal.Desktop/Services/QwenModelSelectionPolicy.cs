using System;
using System.Collections.Generic;
using System.Linq;

namespace Layal.Services;

public sealed record QwenModelVariant(
    string ModelId,
    string SizeDescription,
    long MaximumDownloadBytes,
    long MinimumGpuWorkingSetBytes,
    long MinimumCpuWorkingSetBytes,
    int MinimumLogicalProcessors,
    bool GpuOnly);

/// <summary>
/// Device-supported Qwen3.5 variants. Byte ceilings use the official Ollama library's
/// published download-size ranges; working-set headroom is a conservative Layal policy,
/// not a vendor hardware guarantee.
/// </summary>
public static class QwenModelVariantCatalog
{
    public const string DefaultModelId = "qwen3.5:2b";

    private static readonly IReadOnlyList<QwenModelVariant> Variants =
        Array.AsReadOnly(new[]
        {
            Create("qwen3.5:0.8b", "1.2–1.3 GB", 1_300_000_000, minimumLogicalProcessors: 2),
            Create("qwen3.5:2b", "2.7–3.1 GB", 3_100_000_000, minimumLogicalProcessors: 4),
            Create("qwen3.5:4b", "3.3–4.0 GB", 4_000_000_000, minimumLogicalProcessors: 6),
            Create("qwen3.5:9b", "6.6–7.6 GB", 7_600_000_000, minimumLogicalProcessors: 8, gpuOnly: true)
        });

    private static QwenModelVariant Create(
        string modelId,
        string sizeDescription,
        long maximumDownloadBytes,
        int minimumLogicalProcessors,
        bool gpuOnly = false)
    {
        const long cpuHeadroomBytes = 2L * 1024 * 1024 * 1024;
        const long gpuHeadroomBytes = 1536L * 1024 * 1024;
        return new QwenModelVariant(
            modelId,
            sizeDescription,
            maximumDownloadBytes,
            checked(maximumDownloadBytes + gpuHeadroomBytes),
            checked(maximumDownloadBytes + cpuHeadroomBytes),
            minimumLogicalProcessors,
            gpuOnly);
    }

    public static IReadOnlyList<QwenModelVariant> All => Variants;

    public static QwenModelVariant GetRequired(string? modelId)
    {
        var normalized = (modelId ?? string.Empty).Trim();
        return Variants.FirstOrDefault(item =>
                   string.Equals(item.ModelId, normalized, StringComparison.OrdinalIgnoreCase))
               ?? Variants.First(item => item.ModelId == DefaultModelId);
    }

    public static bool TryGet(string? modelId, out QwenModelVariant? variant)
    {
        variant = Variants.FirstOrDefault(item =>
            string.Equals(item.ModelId, (modelId ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        return variant is not null;
    }
}

public sealed record QwenHardwareProfile(
    long? TotalRamBytes,
    long? AvailableRamBytes,
    int LogicalProcessorCount,
    long? TotalDedicatedGpuMemoryBytes,
    long? AvailableDedicatedGpuMemoryBytes,
    string GpuName);

public sealed record QwenModelSelection(
    QwenModelVariant Variant,
    bool MemoryBudgetSatisfied,
    bool UsesDedicatedGpuBudget,
    string Rationale);

/// <summary>
/// Recommends the largest Qwen3.5 variant that fits the measured free-memory budget.
/// NVIDIA VRAM is used only when a live nvidia-smi free-memory reading is available.
/// Otherwise selection uses current system RAM and CPU capacity; it never treats a GPU
/// name or a truncated WMI AdapterRAM value as proof of available VRAM.
/// </summary>
public static class QwenModelSelectionPolicy
{
    private const long MinimumSystemHeadroomForGpuBytes = 3L * 1024 * 1024 * 1024;

    public static QwenModelSelection Select(QwenHardwareProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var processors = Math.Max(0, profile.LogicalProcessorCount);
        if (profile.AvailableDedicatedGpuMemoryBytes is > 0 &&
            profile.AvailableRamBytes is long ramAvailable &&
            ramAvailable >= MinimumSystemHeadroomForGpuBytes)
        {
            var gpuCandidate = QwenModelVariantCatalog.All
                .Where(item => processors >= item.MinimumLogicalProcessors)
                .Where(item => profile.AvailableDedicatedGpuMemoryBytes.Value >= item.MinimumGpuWorkingSetBytes)
                .OrderByDescending(item => item.MaximumDownloadBytes)
                .FirstOrDefault();

            if (gpuCandidate is not null)
            {
                return new QwenModelSelection(
                    gpuCandidate,
                    true,
                    true,
                    $"Measured free NVIDIA VRAM: {Format(profile.AvailableDedicatedGpuMemoryBytes.Value)}; " +
                    $"measured available system RAM: {Format(ramAvailable)}. " +
                    $"The selection reserves at least 1.5 GiB beyond the published model download ceiling.");
            }
        }

        var availableRam = profile.AvailableRamBytes;
        var ramWasEstimated = false;
        if (availableRam is null && profile.TotalRamBytes is long totalRam && totalRam > 0)
        {
            const long operatingSystemReserveBytes = 3L * 1024 * 1024 * 1024;
            availableRam = Math.Max(0, totalRam - operatingSystemReserveBytes);
            ramWasEstimated = true;
        }

        if (availableRam is long measuredRam && measuredRam > 0)
        {
            var cpuCandidate = QwenModelVariantCatalog.All
                .Where(item => !item.GpuOnly)
                .Where(item => processors >= item.MinimumLogicalProcessors)
                .Where(item => measuredRam >= item.MinimumCpuWorkingSetBytes)
                .OrderByDescending(item => item.MaximumDownloadBytes)
                .FirstOrDefault();

            if (cpuCandidate is not null)
            {
                var source = ramWasEstimated ? "estimated from total system RAM" : "measured available system RAM";
                return new QwenModelSelection(
                    cpuCandidate,
                    true,
                    false,
                    $"{source}: {Format(measuredRam)}; logical processors: {processors}. " +
                    $"The selection reserves at least 2 GiB beyond the published model download ceiling. " +
                    "GPU capacity was not used because a reliable free-VRAM reading was unavailable or insufficient.");
            }

            var smallest = QwenModelVariantCatalog.GetRequired("qwen3.5:0.8b");
            return new QwenModelSelection(
                smallest,
                false,
                false,
                $"Available system RAM is {Format(measuredRam)}; even the smallest local model needs a conservative " +
                $"working-set budget of {Format(smallest.MinimumCpuWorkingSetBytes)}. Free memory or close other applications before full setup.");
        }

        var fallback = QwenModelVariantCatalog.GetRequired(QwenModelVariantCatalog.DefaultModelId);
        return new QwenModelSelection(
            fallback,
            true,
            false,
            "Memory telemetry is unavailable. The established Qwen3.5 2B baseline is retained; setup must still pass the actual local model health check.");
    }

    private static string Format(long bytes) =>
        CompleteSetupPreflightService.FormatBytes(bytes);
}
