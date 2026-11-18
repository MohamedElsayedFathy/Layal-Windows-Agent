using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

/// <summary>
/// Coordinates an explicitly requested model update. The previous model is copied
/// before mutation and is restored whenever pull, validation, health-check, or pin
/// commit fails. Rollback deliberately ignores the caller cancellation token.
/// </summary>
internal static class OllamaModelUpdateTransaction
{
    internal static async Task<T> ExecuteAsync<T>(
        string rollbackModelId,
        Func<CancellationToken, Task> createRollbackCopyAsync,
        Func<CancellationToken, Task<T>> applyUpdateAsync,
        Func<T, CancellationToken, Task<bool>> verifyCandidateAsync,
        Func<T, CancellationToken, Task> commitPinAsync,
        Func<CancellationToken, Task> restoreRollbackCopyAsync,
        Func<CancellationToken, Task> verifyRollbackAsync,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rollbackModelId);
        ArgumentNullException.ThrowIfNull(createRollbackCopyAsync);
        ArgumentNullException.ThrowIfNull(applyUpdateAsync);
        ArgumentNullException.ThrowIfNull(verifyCandidateAsync);
        ArgumentNullException.ThrowIfNull(commitPinAsync);
        ArgumentNullException.ThrowIfNull(restoreRollbackCopyAsync);
        ArgumentNullException.ThrowIfNull(verifyRollbackAsync);

        // No model mutation has happened yet; if backup creation fails, stop here.
        await createRollbackCopyAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = await applyUpdateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!await verifyCandidateAsync(candidate, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException(
                    "The updated Qwen model did not match the Registry digest or failed its local health check.");

            cancellationToken.ThrowIfCancellationRequested();
            // Pin replacement is the commit point and happens only after digest + health checks.
            await commitPinAsync(candidate, cancellationToken).ConfigureAwait(false);
            return candidate;
        }
        catch (Exception updateError)
        {
            try
            {
                await restoreRollbackCopyAsync(CancellationToken.None).ConfigureAwait(false);
                await verifyRollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new InvalidOperationException(
                    "Qwen model update failed and rollback could not be verified. " +
                    "Do not delete the rollback copy '" + rollbackModelId +
                    "'; the application must remain closed until the previous model is restored.",
                    new AggregateException(updateError, rollbackError));
            }

            ExceptionDispatchInfo.Capture(updateError).Throw();
            throw;
        }
    }
}
