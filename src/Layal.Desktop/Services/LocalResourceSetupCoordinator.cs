using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

/// <summary>
/// Applies the persistent resource lifecycle to installers and verified downloads.
/// Ready is written only after the resource-specific integrity and health checks succeed.
/// </summary>
public sealed class LocalResourceSetupCoordinator
{
    private readonly LocalResourceManager _manager;
    private readonly LocalResourceDownloadService? _downloader;

    public LocalResourceSetupCoordinator(
        LocalResourceManager manager,
        LocalResourceDownloadService? downloader = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _downloader = downloader;
    }

    public Task<IReadOnlyList<LocalResourceStateEntry>> GetSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        _manager.GetSnapshotAsync(cancellationToken);

    public Task<LocalResourceStateEntry> ReportDownloadProgressAsync(
        string resourceId,
        long completedBytes,
        long totalBytes,
        double bytesPerSecond,
        double? remainingSeconds,
        CancellationToken cancellationToken = default) =>
        _manager.ReportDownloadProgressAsync(
            resourceId, completedBytes, totalBytes, bytesPerSecond, remainingSeconds, cancellationToken);


    public async Task ExecuteAsync(
        string resourceId,
        Func<CancellationToken, Task> installOrDownload,
        Func<CancellationToken, Task<bool>> verifyAndHealthCheck,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentNullException.ThrowIfNull(installOrDownload);
        ArgumentNullException.ThrowIfNull(verifyAndHealthCheck);

        await _manager.BeginDownloadAsync(resourceId, null, cancellationToken).ConfigureAwait(false);
        try
        {
            await installOrDownload(cancellationToken).ConfigureAwait(false);
            await CompleteVerificationAsync(resourceId, verifyAndHealthCheck, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ResetAfterCancellationAsync(resourceId).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await MarkFailureWithoutMaskingAsync(resourceId, exception).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Revalidates an already-present resource without downloading or replacing it.
    /// This reconciles old installs and persisted states with the current pinned health checks.
    /// </summary>
    public async Task EnsureReadyAsync(
        string resourceId,
        Func<CancellationToken, Task<bool>> verifyAndHealthCheck,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentNullException.ThrowIfNull(verifyAndHealthCheck);

        try
        {
            await CompleteVerificationAsync(resourceId, verifyAndHealthCheck, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ResetAfterCancellationAsync(resourceId).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await MarkFailureWithoutMaskingAsync(resourceId, exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DownloadAsync(
        LocalResourceDefinition resource,
        string destinationPath,
        IProgress<LocalResourceDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<bool>>? verifyInstalledResource = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var downloader = _downloader ?? throw new InvalidOperationException(
            "This resource coordinator was created without a direct-download service.");

        await _manager.BeginDownloadAsync(
            resource.Id, resource.ExpectedSizeBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            await downloader.DownloadAsync(
                resource,
                destinationPath,
                progress,
                cancellationToken,
                async (downloadProgress, token) =>
                {
                    await _manager.ReportDownloadProgressAsync(
                        resource.Id,
                        downloadProgress.DownloadedBytes,
                        downloadProgress.TotalBytes,
                        downloadProgress.BytesPerSecond,
                        downloadProgress.RemainingSeconds,
                        token).ConfigureAwait(false);
                }).ConfigureAwait(false);

            await CompleteVerificationAsync(
                resource.Id,
                async token =>
                {
                    if (!await LocalResourceDownloadService.VerifyFileAsync(
                            resource, destinationPath, token).ConfigureAwait(false))
                        return false;

                    return verifyInstalledResource is null ||
                           await verifyInstalledResource(token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ResetAfterCancellationAsync(resource.Id).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await MarkFailureWithoutMaskingAsync(resource.Id, exception).ConfigureAwait(false);
            throw;
        }
    }

    private async Task CompleteVerificationAsync(
        string resourceId,
        Func<CancellationToken, Task<bool>> verifyAndHealthCheck,
        CancellationToken cancellationToken)
    {
        await _manager.BeginVerificationAsync(resourceId, cancellationToken).ConfigureAwait(false);
        var verified = await verifyAndHealthCheck(cancellationToken).ConfigureAwait(false);
        if (!verified)
            throw new InvalidOperationException(
                "The resource did not pass its pinned integrity and health checks: " + resourceId);

        await _manager.MarkInstalledAsync(resourceId, cancellationToken).ConfigureAwait(false);
        await _manager.MarkReadyAsync(resourceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task ResetAfterCancellationAsync(string resourceId)
    {
        try
        {
            await _manager.ResetAsync(resourceId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the cancellation result. Any state-store failure is reported on the next validation.
        }
    }

    private async Task MarkFailureWithoutMaskingAsync(string resourceId, Exception exception)
    {
        try
        {
            var safeMessage = string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message;
            await _manager.MarkFailedAsync(
                resourceId, safeMessage, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original installer/download exception.
        }
    }
}
