using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Layal.Services;

public enum VoiceConversationStatus
{
    Connecting,
    Listening,
    UserSpeaking,
    AssistantSpeaking,
    Stopped
}

/// <summary>
/// Full-duplex voice conversation on the configured Realtime WebSocket. Microphone PCM is
/// streamed only while RunAsync is active; server VAD controls turns and interrupts.
/// </summary>
public sealed class VoiceConversationService : IDisposable
{
    public const int SampleRate = 24000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int BytesPerMillisecond = SampleRate * Channels * (BitsPerSample / 8) / 1000;
    private const int MaxEventBytes = 2 * 1024 * 1024;
    private const int CaptureQueueCapacity = 24;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private int _running;
    private int _disposed;

    public event Action<VoiceConversationStatus, string?>? StatusChanged;
    public event Action<string, string>? TranscriptReceived;

    /// <summary>Builds the provider session contract so VAD and interruption behavior are testable without a microphone.</summary>
    public static JsonElement BuildSessionUpdate(string modelId, string voiceId, string instructions)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("A Realtime voice model ID is required.");
        if (!LayalSpeechService.IsSupportedVoice(voiceId))
            throw new InvalidOperationException("Choose a supported Layal voice preset.");
        if (string.IsNullOrWhiteSpace(instructions))
            throw new InvalidOperationException("Voice conversation instructions are required.");

        return JsonSerializer.SerializeToElement(new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                model = modelId.Trim(),
                output_modalities = new[] { "audio" },
                instructions = instructions.Trim(),
                audio = new
                {
                    input = new
                    {
                        format = new { type = "audio/pcm", rate = SampleRate },
                        transcription = new { model = "gpt-4o-mini-transcribe" },
                        noise_reduction = new { type = "near_field" },
                        turn_detection = new
                        {
                            type = "server_vad",
                            threshold = 0.5,
                            prefix_padding_ms = 300,
                            silence_duration_ms = 500,
                            create_response = true,
                            interrupt_response = true
                        }
                    },
                    output = new
                    {
                        format = new { type = "audio/pcm", rate = SampleRate },
                        voice = voiceId.Trim().ToLowerInvariant()
                    }
                }
            }
        });
    }

    public async Task RunAsync(
        string baseUrl,
        string apiKey,
        string modelId,
        string voiceId,
        string instructions,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("A voice conversation is already running.");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Interlocked.Exchange(ref _running, 0);
            throw new InvalidOperationException("Save a valid API key in Settings before starting a voice conversation.");
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = lifetime.Token;
        var captureQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(CaptureQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        Exception? inputPipelineError = null;
        ClientWebSocket? socket = null;
        WaveIn? microphone = null;
        WaveOut? output = null;
        BufferedWaveProvider? playback = null;
        EventHandler<WaveInEventArgs>? dataHandler = null;
        EventHandler<StoppedEventArgs>? stoppedHandler = null;
        Task? microphoneSender = null;
        Task? playbackMonitor = null;

        RaiseStatus(VoiceConversationStatus.Connecting, null);
        try
        {
            var endpoint = LayalSpeechService.BuildRealtimeUri(baseUrl, modelId);
            socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey.Trim());
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await socket.ConnectAsync(endpoint, token);

            using (var created = await ReadUntilEventAsync(socket, "session.created", token))
                _ = created.RootElement.ValueKind;
            await SendJsonAsync(socket, BuildSessionUpdate(modelId, voiceId, instructions), token);
            using (var updated = await ReadUntilEventAsync(socket, "session.updated", token))
                _ = updated.RootElement.ValueKind;

            playback = new BufferedWaveProvider(new WaveFormat(SampleRate, BitsPerSample, Channels))
            {
                // Keep up to ten seconds of PCM queued; BufferDuration is read-only in NAudio.
                BufferLength = SampleRate * Channels * (BitsPerSample / 8) * 10,
                DiscardOnBufferOverflow = false
            };
            output = new WaveOut();
            output.Init(playback);

            microphone = new WaveIn
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                BufferMilliseconds = 50,
                NumberOfBuffers = 3
            };

            dataHandler = (_, args) =>
            {
                if (token.IsCancellationRequested || args.BytesRecorded <= 0)
                    return;

                var count = args.BytesRecorded - (args.BytesRecorded % (BitsPerSample / 8));
                if (count <= 0) return;
                var frame = new byte[count];
                Buffer.BlockCopy(args.Buffer, 0, frame, 0, count);
                if (!captureQueue.Writer.TryWrite(frame))
                {
                    Array.Clear(frame, 0, frame.Length);
                    Interlocked.CompareExchange(ref inputPipelineError,
                        new InvalidOperationException("Realtime audio sending fell behind microphone capture. Stop and restart the voice conversation."), null);
                    lifetime.Cancel();
                }
            };
            stoppedHandler = (_, args) =>
            {
                if (args.Exception is null || token.IsCancellationRequested) return;
                Interlocked.CompareExchange(ref inputPipelineError,
                    new InvalidOperationException("Windows stopped microphone capture unexpectedly.", args.Exception), null);
                lifetime.Cancel();
            };
            microphone.DataAvailable += dataHandler;
            microphone.RecordingStopped += stoppedHandler;

            microphoneSender = SendMicrophoneFramesAsync(socket, captureQueue.Reader, token, error =>
            {
                Interlocked.CompareExchange(ref inputPipelineError, error, null);
                lifetime.Cancel();
            });

            try
            {
                microphone.StartRecording();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not start the live microphone. Check Windows microphone permissions and your default recording device.", ex);
            }

            RaiseStatus(VoiceConversationStatus.Listening, null);
            var playbackState = new PlaybackState(playback, output);
            playbackMonitor = MonitorPlaybackDrainAsync(playbackState, token);

            while (!token.IsCancellationRequested)
            {
                using var message = await ReceiveJsonAsync(socket, token);
                var root = message.RootElement;
                var eventType = GetString(root, "type");

                if (eventType == "error")
                    throw new InvalidOperationException(ReadServerError(root));

                switch (eventType)
                {
                    case "input_audio_buffer.speech_started":
                        // Server VAD cancels generation; the WebSocket client must also stop
                        // local playback and truncate audio that has not reached the speaker.
                        playbackState.UserSpeaking = true;
                        RaiseStatus(VoiceConversationStatus.UserSpeaking, null);
                        await InterruptPlaybackAsync(socket, playbackState, token);
                        break;

                    case "input_audio_buffer.speech_stopped":
                        playbackState.UserSpeaking = false;
                        RaiseStatus(VoiceConversationStatus.Listening, null);
                        break;

                    case "response.created":
                        // A new server turn starts a fresh output item. Do not let an early VAD
                        // interrupt truncate a completed item from the previous turn.
                        if (playbackState.Buffer.BufferedBytes == 0)
                        {
                            playbackState.ItemId = null;
                            playbackState.ContentIndex = 0;
                            playbackState.ReceivedBytes = 0;
                            playbackState.OutputStarted = false;
                        }
                        playbackState.ResponseInProgress = true;
                        RaiseStatus(VoiceConversationStatus.AssistantSpeaking, null);
                        break;

                    case "response.output_audio.delta":
                        AppendAssistantAudio(root, playbackState);
                        RaiseStatus(VoiceConversationStatus.AssistantSpeaking, null);
                        break;

                    case "response.output_audio_transcript.done":
                        var responseTranscript = GetString(root, "transcript").Trim();
                        if (responseTranscript.Length > 0)
                        {
                            var itemId = GetString(root, "item_id");
                            if (itemId.Length == 0 || playbackState.ReportedAssistantTranscriptItems.Add(itemId))
                                RaiseTranscript("assistant", responseTranscript);
                        }
                        break;

                    case "conversation.item.input_audio_transcription.completed":
                        var userTranscript = GetString(root, "transcript").Trim();
                        if (userTranscript.Length > 0)
                            RaiseTranscript("user", userTranscript);
                        break;

                    case "response.cancelled":
                        playbackState.ResponseInProgress = false;
                        break;

                    case "response.done":
                        playbackState.ResponseInProgress = false;
                        if (playbackState.Buffer.BufferedBytes == 0)
                            RaiseStatus(VoiceConversationStatus.Listening, null);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (Volatile.Read(ref inputPipelineError) is { } inputError)
        {
            throw new InvalidOperationException("The live microphone stream failed.", inputError);
        }
        finally
        {
            lifetime.Cancel();
            captureQueue.Writer.TryComplete();

            if (microphone is not null)
            {
                if (dataHandler is not null) microphone.DataAvailable -= dataHandler;
                if (stoppedHandler is not null) microphone.RecordingStopped -= stoppedHandler;
                try { microphone.StopRecording(); } catch { /* A cancelled WinMM stream may already be stopped. */ }
                try { microphone.Dispose(); } catch { /* Release native capture resources best-effort. */ }
            }

            if (microphoneSender is not null)
            {
                try { await microphoneSender; } catch { /* The primary session or cancellation error wins. */ }
            }
            if (playbackMonitor is not null)
            {
                try { await playbackMonitor; } catch { /* The primary session or cancellation error wins. */ }
            }
            while (captureQueue.Reader.TryRead(out var pendingFrame))
                Array.Clear(pendingFrame, 0, pendingFrame.Length);

            if (playback is not null)
            {
                try { playback.ClearBuffer(); } catch { /* Dispose below releases the in-memory PCM queue. */ }
            }
            if (output is not null)
            {
                try { output.Stop(); } catch { /* The audio device may have disconnected. */ }
                try { output.Dispose(); } catch { /* Release native playback resources best-effort. */ }
            }

            socket?.Dispose();
            Interlocked.Exchange(ref _running, 0);
            RaiseStatus(VoiceConversationStatus.Stopped, null);
        }
    }

    private async Task SendMicrophoneFramesAsync(
        ClientWebSocket socket,
        ChannelReader<byte[]> frames,
        CancellationToken cancellationToken,
        Action<Exception> reportFailure)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await SendJsonAsync(socket, new
                    {
                        type = "input_audio_buffer.append",
                        audio = Convert.ToBase64String(frame)
                    }, cancellationToken);
                }
                finally
                {
                    Array.Clear(frame, 0, frame.Length);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal when the user presses Stop or the session is interrupted by an error.
        }
        catch (Exception ex)
        {
            reportFailure(ex);
        }
    }

    private async Task MonitorPlaybackDrainAsync(PlaybackState state, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!state.UserSpeaking && !state.ResponseInProgress &&
                    state.OutputStarted && state.Buffer.BufferedBytes == 0)
                {
                    state.OutputStarted = false;
                    RaiseStatus(VoiceConversationStatus.Listening, null);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal when the user stops the live conversation.
        }
    }

    private static void AppendAssistantAudio(JsonElement root, PlaybackState state)
    {
        var encoded = GetString(root, "delta");
        if (encoded.Length == 0) return;

        byte[] frame;
        try { frame = Convert.FromBase64String(encoded); }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("The Realtime provider returned malformed audio data.", ex);
        }

        try
        {
            var itemId = GetString(root, "item_id");
            if (itemId.Length > 0 && !string.Equals(itemId, state.ItemId, StringComparison.Ordinal))
            {
                state.ItemId = itemId;
                state.ContentIndex = GetInt32(root, "content_index");
                state.ReceivedBytes = 0;
            }

            state.Buffer.AddSamples(frame, 0, frame.Length);
            state.ReceivedBytes += frame.Length;
            if (!state.OutputStarted)
            {
                state.Output.Play();
                state.OutputStarted = true;
            }
        }
        finally
        {
            Array.Clear(frame, 0, frame.Length);
        }
    }

    private async Task InterruptPlaybackAsync(
        ClientWebSocket socket,
        PlaybackState state,
        CancellationToken cancellationToken)
    {
        state.Output.Pause();
        var queuedBytes = Math.Max(0, state.Buffer.BufferedBytes);
        var shouldTruncate = !string.IsNullOrWhiteSpace(state.ItemId) &&
                             (state.ResponseInProgress || queuedBytes > 0);
        if (shouldTruncate)
        {
            var playedBytes = Math.Clamp(state.ReceivedBytes - queuedBytes, 0L, state.ReceivedBytes);
            var audioEndMs = (int)Math.Min(int.MaxValue, playedBytes / BytesPerMillisecond);
            await SendJsonAsync(socket, new
            {
                type = "conversation.item.truncate",
                item_id = state.ItemId,
                content_index = state.ContentIndex,
                audio_end_ms = audioEndMs
            }, cancellationToken);
        }

        state.Buffer.ClearBuffer();
        state.ItemId = null;
        state.ContentIndex = 0;
        state.ReceivedBytes = 0;
        state.OutputStarted = false;
    }

    private async Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendGate.Release();
            }
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    private static async Task<JsonDocument> ReadUntilEventAsync(
        ClientWebSocket socket,
        string expectedType,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var message = await ReceiveJsonAsync(socket, cancellationToken);
            var root = message.RootElement;
            var type = GetString(root, "type");
            if (type == "error")
                throw new InvalidOperationException(ReadServerError(root));
            if (type == expectedType)
                return JsonDocument.Parse(root.GetRawText());
        }
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("The Realtime provider closed the voice conversation unexpectedly.");
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidOperationException("The Realtime provider returned an unsupported event type.");
                if (message.Length + result.Count > MaxEventBytes)
                    throw new InvalidOperationException("The Realtime provider returned an unexpectedly large event.");
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }
            return JsonDocument.Parse(message.ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The Realtime provider returned malformed event data.", ex);
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            if (message.TryGetBuffer(out var segment) && segment.Array is not null)
                Array.Clear(segment.Array, segment.Offset, segment.Count);
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt32(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var number)
            ? number
            : 0;

    private static string ReadServerError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            var message = GetString(error, "message");
            var code = GetString(error, "code");
            if (message.Length > 1000) message = message[..1000];
            if (message.Length == 0) message = "The Realtime provider could not continue the voice session.";
            return code.Length == 0 ? message : code + ": " + message;
        }
        return "The Realtime provider returned an error.";
    }

    private void RaiseStatus(VoiceConversationStatus status, string? detail)
    {
        var handlers = StatusChanged;
        if (handlers is null) return;
        foreach (Action<VoiceConversationStatus, string?> handler in handlers.GetInvocationList())
        {
            try { handler(status, detail); } catch { /* UI listeners must not break audio cleanup. */ }
        }
    }

    private void RaiseTranscript(string role, string transcript)
    {
        var handlers = TranscriptReceived;
        if (handlers is null) return;
        foreach (Action<string, string> handler in handlers.GetInvocationList())
        {
            try { handler(role, transcript); } catch { /* A UI listener must not break the live audio stream. */ }
        }
    }

    private sealed class PlaybackState(BufferedWaveProvider buffer, WaveOut output)
    {
        public BufferedWaveProvider Buffer { get; } = buffer;
        public WaveOut Output { get; } = output;
        public string? ItemId { get; set; }
        public int ContentIndex { get; set; }
        public long ReceivedBytes { get; set; }
        public bool ResponseInProgress { get; set; }
        public bool UserSpeaking { get; set; }
        public bool OutputStarted { get; set; }
        public System.Collections.Generic.HashSet<string> ReportedAssistantTranscriptItems { get; } =
            new(StringComparer.Ordinal);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _sendGate.Dispose();
    }
}
