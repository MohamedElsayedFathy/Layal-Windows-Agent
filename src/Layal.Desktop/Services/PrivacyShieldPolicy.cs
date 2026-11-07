using System;

namespace Layal.Services;

/// <summary>Fail-closed decisions for captured audio while privacy or lifecycle state changes.</summary>
public static class PrivacyShieldPolicy
{
    /// <summary>Fail-closed gate for starting any new local capture source.</summary>
    public static bool CanStartCapture(bool privacyShieldEnabled, bool applicationExiting) =>
        !privacyShieldEnabled && !applicationExiting;

    /// <summary>Rejects a captured image if the shield, shutdown, or cancellation wins a race.</summary>
    public static bool ShouldDiscardCapturedImage(
        bool privacyShieldEnabled, bool applicationExiting, bool cancellationRequested) =>
        privacyShieldEnabled || applicationExiting || cancellationRequested;

    /// <summary>Best-effort zeroing for mutable capture buffers owned by the application.</summary>
    public static void ClearCapturedBuffer(byte[]? buffer)
    {
        if (buffer is { Length: > 0 })
            Array.Clear(buffer, 0, buffer.Length);
    }
    /// <summary>
    /// Returns true when a captured audio buffer must be zeroed without transcription or upload.
    /// This is intentionally evaluated after asynchronous recording finalization and again after
    /// any awaited local setup step.
    /// </summary>
    public static bool ShouldDiscardCapturedAudio(
        bool privacyShieldEnabled,
        bool windowVisible,
        bool applicationExiting,
        bool cancellationRequested) =>
        privacyShieldEnabled || !windowVisible || applicationExiting || cancellationRequested;

    /// <summary>
    /// Fail-closed policy for an agent task that may still invoke a screen, window, clipboard,
    /// or camera inspection tool after the master privacy shield is enabled.
    /// </summary>
    public static bool ShouldCancelAgentTask(bool privacyShieldEnabled) => privacyShieldEnabled;
}
