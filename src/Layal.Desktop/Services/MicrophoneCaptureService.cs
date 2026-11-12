using NAudio.Wave;

namespace Layal.Services;

/// <summary>Manual, bounded microphone capture only. Raw audio stays in memory; no network or transcription API is available here.</summary>
public sealed class MicrophoneCaptureService : IDisposable
{
    public const int MaximumRecordingSeconds = 120;
    public static readonly TimeSpan MaximumRecordingDuration = TimeSpan.FromSeconds(MaximumRecordingSeconds);
    private const int SampleRate = 16000;
    private const int MaxPcmBytes = SampleRate * 2 * MaximumRecordingSeconds;
    private readonly object _sync = new();
    private WaveIn? _capture;
    private MemoryStream? _audioBuffer;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<Exception?>? _recordingStopped;
    private bool _isRecording;
    private bool _maximumDurationReached;
    private int _recordedPcmBytes;

    public MicrophoneCaptureService() { }

    public bool IsRecording { get { lock (_sync) return _isRecording; } }

    public void StartRecording(string? inputDeviceId = null)
    {
        lock (_sync)
        {
            if (_capture is not null)
                throw new InvalidOperationException("A microphone capture is already active.");

            WaveIn capture;
            MemoryStream audioBuffer;
            WaveFileWriter writer;
            try
            {
                capture = new WaveIn
                {
                    DeviceNumber = AudioDeviceService.ResolveInputDeviceNumber(inputDeviceId),
                    WaveFormat = new WaveFormat(SampleRate, 16, 1),
                    BufferMilliseconds = 100,
                    NumberOfBuffers = 3
                };
                audioBuffer = new MemoryStream();
                writer = new WaveFileWriter(audioBuffer, capture.WaveFormat);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not initialize the microphone. Check that a recording device is connected and available.", ex);
            }

            _capture = capture;
            _audioBuffer = audioBuffer;
            _writer = writer;
            _recordingStopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _recordedPcmBytes = 0;
            _maximumDurationReached = false;
            _isRecording = true;
            capture.DataAvailable += Capture_DataAvailable;
            capture.RecordingStopped += Capture_RecordingStopped;
            try { capture.StartRecording(); }
            catch (Exception ex)
            {
                CancelRecording();
                throw new InvalidOperationException(
                    "Windows could not start microphone recording. Check microphone permissions and the selected input device.", ex);
            }
        }
    }

    public async Task<SpeechRecordingResult> StopRecordingAsync(CancellationToken cancellationToken = default)
    {
        WaveIn capture;
        Task<Exception?> stoppedTask;
        lock (_sync)
        {
            if (!_isRecording || _capture is null || _recordingStopped is null)
                throw new InvalidOperationException("No microphone recording is active.");
            capture = _capture;
            stoppedTask = _recordingStopped.Task;
        }

        Exception? stopError = null;
        try { capture.StopRecording(); }
        catch (Exception ex) { stopError = ex; }

        Exception? callbackError = null;
        if (stopError is null)
        {
            try { callbackError = await stoppedTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { stopError = ex; }
        }

        byte[] wavBytes = [];
        bool maximumDurationReached;
        lock (_sync)
        {
            // Finalize the WAV header only after WinMM delivered its last data callback.
            _isRecording = false;
            var writer = _writer;
            _writer = null;
            var audioBuffer = _audioBuffer;
            _audioBuffer = null;
            maximumDurationReached = _maximumDurationReached;
            _maximumDurationReached = false;
            _capture = null;
            _recordingStopped = null;
            _recordedPcmBytes = 0;
            try
            {
                writer?.Dispose();
                if (audioBuffer is not null) wavBytes = audioBuffer.ToArray();
            }
            catch (Exception ex) { stopError ??= ex; }
            finally { ClearAndDisposeStream(audioBuffer); }
            capture.DataAvailable -= Capture_DataAvailable;
            capture.RecordingStopped -= Capture_RecordingStopped;
        }

        try { capture.Dispose(); }
        catch (Exception ex) { stopError ??= ex; }
        stopError ??= callbackError;
        if (stopError is not null)
        {
            Array.Clear(wavBytes, 0, wavBytes.Length);
            throw new InvalidOperationException("The microphone recording could not be finalized. Stop recording and try again.", stopError);
        }
        if (wavBytes.Length <= 44)
        {
            Array.Clear(wavBytes, 0, wavBytes.Length);
            throw new InvalidOperationException("No audio was captured. Check the microphone input and try again.");
        }
        return new SpeechRecordingResult(wavBytes, maximumDurationReached);
    }

    /// <summary>Stops and discards an active capture without writing it to disk.</summary>
    public void CancelRecording()
    {
        WaveIn? capture;
        WaveFileWriter? writer;
        MemoryStream? audioBuffer;
        lock (_sync)
        {
            capture = _capture;
            writer = _writer;
            audioBuffer = _audioBuffer;
            _capture = null;
            _writer = null;
            _audioBuffer = null;
            _recordingStopped = null;
            _isRecording = false;
            _maximumDurationReached = false;
            _recordedPcmBytes = 0;
        }

        if (capture is not null)
        {
            capture.DataAvailable -= Capture_DataAvailable;
            capture.RecordingStopped -= Capture_RecordingStopped;
            try { capture.StopRecording(); } catch { /* The device may already have stopped. */ }
            try { capture.Dispose(); } catch { /* Cleanup must not retain audio. */ }
        }
        try { writer?.Dispose(); } catch { /* A discard must remain safe. */ }
        ClearAndDisposeStream(audioBuffer);
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(sender, _capture) || !_isRecording || _writer is null || e.BytesRecorded <= 0)
                return;
            var remaining = MaxPcmBytes - _recordedPcmBytes;
            var bytesToWrite = Math.Max(0, Math.Min(e.BytesRecorded, remaining));
            bytesToWrite -= bytesToWrite % 2;
            if (bytesToWrite > 0)
            {
                _writer.Write(e.Buffer, 0, bytesToWrite);
                _recordedPcmBytes += bytesToWrite;
            }
            if (bytesToWrite < e.BytesRecorded) _maximumDurationReached = true;
        }
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            if (ReferenceEquals(sender, _capture))
                _recordingStopped?.TrySetResult(e.Exception);
        }
    }

    private static void ClearAndDisposeStream(MemoryStream? stream)
    {
        if (stream is null) return;
        try
        {
            if (stream.TryGetBuffer(out var segment) && segment.Array is not null)
                Array.Clear(segment.Array, segment.Offset, segment.Count);
        }
        catch { /* Best-effort memory clearing; recordings are never written to disk. */ }
        stream.Dispose();
    }

    public void Dispose() => CancelRecording();
}

public sealed record SpeechRecordingResult(byte[] WavBytes, bool MaximumDurationReached);
