using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

/// <summary>
/// Bounded, non-mutating clipboard read policy. Windows may temporarily deny clipboard
/// access while another process owns it; only CLIPBRD_E_CANT_OPEN is retried.
/// </summary>
public static class ClipboardReadPolicy
{
    private const int ClipboardCannotOpenHResult = unchecked((int)0x800401D0);
    private const int MaximumAttempts = 4;

    public static async Task<T> ReadWithRetryAsync<T>(
        Func<T> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return read();
            }
            catch (ExternalException exception) when (
                IsClipboardBusy(exception) && attempt < MaximumAttempts)
            {
                // Keep the retry window short and bounded (40 + 80 + 120 ms).
                // The caller's UI synchronization context is intentionally preserved:
                // WPF Clipboard access must remain on the STA dispatcher thread.
                await Task.Delay(TimeSpan.FromMilliseconds(40 * attempt), cancellationToken);
            }
        }
    }

    public static bool IsClipboardBusy(Exception exception) =>
        exception is ExternalException externalException &&
        externalException.ErrorCode == ClipboardCannotOpenHResult;

    public static ClipboardTextLimit LimitText(string text, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

        if (text.Length <= maximumCharacters)
            return new ClipboardTextLimit(text, WasTruncated: false);

        var length = maximumCharacters;
        // Do not leave an unpaired UTF-16 high surrogate at the attachment boundary.
        if (length > 0 && char.IsHighSurrogate(text[length - 1]) &&
            length < text.Length && char.IsLowSurrogate(text[length]))
        {
            length--;
        }

        return new ClipboardTextLimit(text[..length], WasTruncated: true);
    }
}

public readonly record struct ClipboardTextLimit(string Text, bool WasTruncated);
