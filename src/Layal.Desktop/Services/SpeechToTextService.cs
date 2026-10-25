using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace Layal.Services;

/// <summary>Manual, bounded microphone capture and transcription. Raw audio stays in memory only.</summary>
public sealed class SpeechToTextService : IDisposable
{
    public const int MaximumRecordingSeconds = 120;
    public static readonly TimeSpan MaximumRecordingDuration = TimeSpan.FromSeconds(MaximumRecordingSeconds);
    private const int SampleRate = 16000;
    private const int MaxPcmBytes = SampleRate * 2 * MaximumRecordingSeconds;
    private const string TranscriptionPrompt =
        "Transcribe exactly what is spoken. Preserve Egyptian Arabic colloquial phrasing and any English words or code-switching; do not translate or formalize the Arabic. " +
        "اكتب العامية المصرية كما نُطقت دون تحويلها إلى الفصحى، واحتفظ بالكلمات الإنجليزية كما قيلت.";

    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly object _sync = new();
    private WaveIn? _capture;
    private MemoryStream? _audioBuffer;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<Exception?>? _recordingStopped;
    private bool _isRecording;
    private bool _maximumDurationReached;
    private int _recordedPcmBytes;

    public SpeechToTextService(Func<HttpClient>? httpClientFactory = null) =>
        _httpClientFactory = httpClientFactory ?? (() => SharedHttpClient);

    public bool IsRecording { get { lock (_sync) return _isRecording; } }

    public void StartRecording()
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
                    DeviceNumber = 0,
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

    public static Uri BuildTranscriptionUri(string baseUrl)
    {
        if (!Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Configure a valid HTTP or HTTPS AI provider URL before using speech input.");
        if (!string.IsNullOrEmpty(baseUri.UserInfo))
            throw new InvalidOperationException("The provider URL must not contain a username or password.");
        if (!string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new InvalidOperationException("The provider base URL must not contain a query string or fragment.");
        if (baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
            throw new InvalidOperationException("Microphone audio can only be sent over HTTPS. Plain HTTP is allowed only for a local loopback provider.");
        return new Uri(new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute), "audio/transcriptions");
    }

    public async Task<string> TranscribeAsync(
        string baseUrl, string apiKey, string modelId, byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wavBytes);
        if (wavBytes.Length <= 44) throw new ArgumentException("A non-empty WAV recording is required.", nameof(wavBytes));
        if (wavBytes.Length > 25 * 1024 * 1024)
            throw new ArgumentException("The WAV recording exceeds the provider's 25 MB upload limit.", nameof(wavBytes));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Save or enter a valid API key in Settings before transcribing speech.");
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("Configure the speech-to-text model ID in Settings.");

        var endpoint = BuildTranscriptionUri(baseUrl);
        var client = _httpClientFactory() ?? throw new InvalidOperationException("The speech HTTP client is unavailable.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(fileContent, "file", "recording.wav");
        multipart.Add(new StringContent(modelId.Trim(), Encoding.UTF8), "model");
        multipart.Add(new StringContent("json", Encoding.UTF8), "response_format");
        multipart.Add(new StringContent("ar", Encoding.UTF8), "languages[]");
        multipart.Add(new StringContent("en", Encoding.UTF8), "languages[]");
        multipart.Add(new StringContent(TranscriptionPrompt, Encoding.UTF8), "prompt");
        request.Content = multipart;

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = (int)response.StatusCode == 404
                ? "The provider may not support /audio/transcriptions or the configured speech model ID."
                : (int)response.StatusCode is 401 or 403
                    ? "Check the provider API key and speech-model permissions."
                    : "Check the provider configuration and try again.";
            throw new InvalidOperationException($"Speech transcription failed (HTTP {(int)response.StatusCode}). {detail}");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("text", out var transcript) || transcript.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("The speech provider returned an invalid transcription response.");
        var text = transcript.GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("No speech was recognized. Try speaking more clearly or reducing background noise.");
        return text;
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
