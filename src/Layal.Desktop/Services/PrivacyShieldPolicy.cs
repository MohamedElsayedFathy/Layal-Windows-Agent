namespace Layal.Services;

/// <summary>Fail-closed decisions for captured audio while privacy or lifecycle state changes.</summary>
public static class PrivacyShieldPolicy
{
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
}
