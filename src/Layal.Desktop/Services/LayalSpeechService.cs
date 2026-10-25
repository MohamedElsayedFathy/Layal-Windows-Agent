using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Layal.Services;

/// <summary>
/// Renders Layal's completed text replies as PCM audio through the OpenAI Realtime API.
/// Audio is buffered only in memory and is cleared after playback.
/// </summary>
public sealed class LayalSpeechService
{
    public const string DefaultModelId = "gpt-realtime-2.1-mini";
    public const string DefaultVoiceId = "marin";
    private const int SampleRate = 24000;
    private const int MaxEventBytes = 2 * 1024 * 1024;
    private const int MaxPcmBytes = 32 * 1024 * 1024;
    private const int MaxTextChunk = 3500;

    private static readonly string[] SupportedVoices =
        ["marin", "cedar", "coral", "shimmer", "alloy", "ash", "ballad", "echo", "sage", "verse"];

    private static readonly char[] SentenceBoundaries =
        ['\n', '.', '!', '?', '؟', '۔', '،', ','];

    public const string VoiceCharacterDescription =
        "distinctly childlike and feminine, like a young-girl character, with a naturally youthful, light upper register; warm, soft, bright, and conversational. " +
        "Let pitch, rhythm, emphasis, and pauses vary naturally with the meaning. Avoid a forced high pitch, sing-song delivery, squeakiness, exaggerated baby-talk, over-enunciation, or a robotic, evenly timed cadence; do not sound like a mature adult woman.";

    private const string VoiceInstructions =
        "Read the user's supplied text aloud exactly as written. Do not answer it, paraphrase, summarize, translate, add a greeting, or add any other words. " +
        "You are Layal, a fictional young-girl character. Your voice should be " + VoiceCharacterDescription + " " +
        "Use natural Egyptian Arabic when the text is Egyptian colloquial, clear Modern Standard Arabic when it is formal Arabic, and natural English for English text. " +
        "Use relaxed conversational timing and connected speech. Let sentence endings, emphasis, and short pauses follow the meaning instead of using the same cadence for every sentence. " +
        "Keep the warmth and light playfulness subtle rather than performed or overly cute. " +
        "The voice is AI-generated, not a human recording.";

    public static bool IsSupportedVoice(string? voiceId) =>
        !string.IsNullOrWhiteSpace(voiceId) &&
        SupportedVoices.Contains(voiceId.Trim(), StringComparer.OrdinalIgnoreCase);

    public static Uri BuildRealtimeUri(string baseUrl, string modelId = DefaultModelId)
    {
        if (!Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new InvalidOperationException(
                "Enter a valid HTTP/HTTPS API base URL without credentials, query strings, or fragments.");
        }

        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("A Realtime speech model ID is required.");

        if (baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
            throw new InvalidOperationException(
                "Realtime speech requires HTTPS for remote providers. Plain HTTP is permitted only for a local loopback provider.");

        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = baseUri.IsDefaultPort ? -1 : baseUri.Port,
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/realtime",
            Query = "model=" + Uri.EscapeDataString(modelId.Trim()),
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    /// <summary>Speak text in bounded chunks; each PCM buffer is erased after playback.</summary>
    public async Task SpeakAsync(
        string baseUrl,
        string apiKey,
        string modelId,
        string voiceId,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Save a valid API key in Settings before using Layal's voice.");
        if (!IsSupportedVoice(voiceId))
            throw new InvalidOperationException("Choose a supported Layal voice preset in Settings.");

        var endpoint = BuildRealtimeUri(baseUrl, modelId);
        var preparedText = PrepareTextForSpeech(text);
        var chunks = SplitTextForSpeech(preparedText, MaxTextChunk);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pcm = await GeneratePcmChunkAsync(endpoint, apiKey.Trim(), modelId.Trim(), voiceId.Trim(), chunk, cancellationToken);
            try
            {
                await PlayPcmAsync(pcm, cancellationToken);
            }
            finally
            {
                Array.Clear(pcm, 0, pcm.Length);
            }
        }
    }

    public static string PrepareTextForSpeech(string? text)
    {
        var result = text ?? string.Empty;
        result = Regex.Replace(result, @"(?s)\x60\x60\x60.*?\x60\x60\x60", " ");
        result = Regex.Replace(result, @"\x60([^\x60]*)\x60", "$1");
        result = Regex.Replace(result, @"!\[([^\]]*)\]\([^)]+\)", "$1");
        result = Regex.Replace(result, @"\[([^\]]+)\]\([^)]+\)", "$1");
        result = Regex.Replace(result, @"https?://\S+", " ");
        result = Regex.Replace(result, @"(?m)^\s{0,3}#{1,6}\s*", string.Empty);
        result = Regex.Replace(result, @"(?m)^\s*[-*+]\s+", string.Empty);
        result = Regex.Replace(result, @"(?m)^\s*\d+[.)]\s+", string.Empty);
        result = Regex.Replace(result, @"[*_~]", string.Empty);
        result = Regex.Replace(result, @"[ \t]{2,}", " ");
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        return result.Trim();
    }

    public static string[] SplitTextForSpeech(string? text, int maximumCharacters = MaxTextChunk)
    {
        if (maximumCharacters < 100)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters), "Chunk size must be at least 100 characters.");

        var remaining = (text ?? string.Empty).Trim();
        if (remaining.Length == 0) return [];

        var chunks = new System.Collections.Generic.List<string>();
        while (remaining.Length > maximumCharacters)
        {
            var breakAt = remaining.LastIndexOfAny(SentenceBoundaries, maximumCharacters - 1, maximumCharacters);
            if (breakAt < maximumCharacters / 2)
                breakAt = remaining.LastIndexOf(' ', maximumCharacters - 1, maximumCharacters);
            if (breakAt < maximumCharacters / 2)
                breakAt = maximumCharacters;

            var splitLength = breakAt;
            if (breakAt < maximumCharacters && breakAt < remaining.Length &&
                Array.IndexOf(SentenceBoundaries, remaining[breakAt]) >= 0)
                splitLength = breakAt + 1;
            if (splitLength <= 0 || splitLength > remaining.Length)
                splitLength = maximumCharacters;

            var chunk = remaining[..splitLength].Trim();
            if (chunk.Length > 0) chunks.Add(chunk);
            remaining = remaining[splitLength..].Trim();
        }

        if (remaining.Length > 0) chunks.Add(remaining);
        return chunks.ToArray();
    }

    private static async Task<byte[]> GeneratePcmChunkAsync(
        Uri endpoint,
        string apiKey,
        string modelId,
        string voiceId,
        string text,
        CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(endpoint, cancellationToken);

        using (var created = await ReadUntilEventAsync(socket, "session.created", cancellationToken))
            _ = created.RootElement.ValueKind;

        await SendJsonAsync(socket, new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                model = modelId,
                output_modalities = new[] { "audio" },
                audio = new
                {
                    output = new
                    {
                        format = new { type = "audio/pcm", rate = SampleRate },
                        voice = voiceId
                    }
                },
                instructions = VoiceInstructions
            }
        }, cancellationToken);

        using (var updated = await ReadUntilEventAsync(socket, "session.updated", cancellationToken))
            _ = updated.RootElement.ValueKind;

        await SendJsonAsync(socket, new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new[] { new { type = "input_text", text } }
            }
        }, cancellationToken);

        await SendJsonAsync(socket, new
        {
            type = "response.create",
            response = new { output_modalities = new[] { "audio" } }
        }, cancellationToken);

        using var pcm = new MemoryStream();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var message = await ReceiveJsonAsync(socket, cancellationToken);
                var root = message.RootElement;
                var type = GetString(root, "type");

                if (type == "error")
                    throw new InvalidOperationException(ReadServerError(root));

                if (type == "response.output_audio.delta")
                {
                    var encoded = GetString(root, "delta");
                    if (string.IsNullOrWhiteSpace(encoded)) continue;
                    byte[] audioChunk;
                    try { audioChunk = Convert.FromBase64String(encoded); }
                    catch (FormatException ex)
                    {
                        throw new InvalidOperationException("The voice provider returned malformed audio data.", ex);
                    }

                    try
                    {
                        if (pcm.Length + audioChunk.Length > MaxPcmBytes)
                            throw new InvalidOperationException("The generated voice response exceeded the in-memory playback limit.");
                        pcm.Write(audioChunk, 0, audioChunk.Length);
                    }
                    finally
                    {
                        Array.Clear(audioChunk, 0, audioChunk.Length);
                    }
                }
                else if (type == "response.done")
                {
                    if (root.TryGetProperty("response", out var response) &&
                        !string.Equals(GetString(response, "status"), "completed", StringComparison.OrdinalIgnoreCase))
                    {
                        var details = response.TryGetProperty("status_details", out var statusDetails)
                            ? ReadErrorObject(statusDetails)
                            : "The Realtime provider could not generate audio.";
                        throw new InvalidOperationException(details);
                    }
                    break;
                }
            }

            if (pcm.Length == 0)
                throw new InvalidOperationException("The Realtime provider completed without returning audio.");
            return pcm.ToArray();
        }
        finally
        {
            if (pcm.TryGetBuffer(out var buffer))
                Array.Clear(buffer.Array!, buffer.Offset, buffer.Count);
        }
    }

    private static async Task PlayPcmAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pcm.Length == 0 || pcm.Length % 2 != 0)
            throw new InvalidOperationException("The voice provider returned an invalid PCM audio buffer.");

        using var stream = new MemoryStream(pcm, writable: false);
        using var source = new RawSourceWaveStream(stream, new WaveFormat(SampleRate, 16, 1));
        using var output = new WaveOut();
        var stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<StoppedEventArgs> handler = (_, args) => stopped.TrySetResult(args);
        output.PlaybackStopped += handler;

        try
        {
            output.Init(source);
            using var registration = cancellationToken.Register(() =>
            {
                try { output.Stop(); } catch { /* The output may already be stopped. */ }
            });
            output.Play();
            var result = await stopped.Task.WaitAsync(cancellationToken);
            if (result.Exception is not null)
                throw new InvalidOperationException("Windows could not play Layal's voice audio.", result.Exception);
        }
        finally
        {
            output.PlaybackStopped -= handler;
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
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
                    throw new InvalidOperationException("The voice provider closed the Realtime connection unexpectedly.");
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidOperationException("The voice provider returned an unsupported Realtime message type.");
                if (message.Length + result.Count > MaxEventBytes)
                    throw new InvalidOperationException("The voice provider returned an unexpectedly large Realtime event.");
                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }

            return JsonDocument.Parse(message.ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The voice provider returned malformed Realtime event data.", ex);
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            if (message.TryGetBuffer(out var bufferSegment))
                Array.Clear(bufferSegment.Array!, bufferSegment.Offset, bufferSegment.Count);
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadServerError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
            return ReadErrorObject(error);
        return "The voice provider returned a Realtime API error.";
    }

    private static string ReadErrorObject(JsonElement element)
    {
        var message = GetString(element, "message");
        var code = GetString(element, "code");
        if (message.Length == 0) message = "The voice provider could not generate audio.";
        if (message.Length > 1000) message = message[..1000];
        return code.Length == 0 ? message : code + ": " + message;
    }
}
