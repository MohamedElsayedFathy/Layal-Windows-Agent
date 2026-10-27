using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Layal.Models;

namespace Layal.Services;

/// <summary>Client for OpenAI-compatible chat completions, including optional tool/function calling.</summary>
public sealed class OpenAiCompatibleClient
{
    private const int MaxJsonResponseBytes = 4 * 1024 * 1024;
    private const int MaxErrorResponseBytes = 32 * 1024;
    private const int MaxStreamResponseBytes = 8 * 1024 * 1024;
    private const int MaxSseLineCharacters = 64 * 1024;
    private const int MaxStreamedTextCharacters = 2 * 1024 * 1024;

    private static readonly HttpClient SharedProbeClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly HttpClient SharedChatClient = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly Func<TimeSpan, HttpClient> _clientFactory;

    // Reuse connection pools in production; tests can still inject short-lived deterministic clients.
    public OpenAiCompatibleClient() : this(timeout =>
        timeout <= TimeSpan.FromSeconds(15) ? SharedProbeClient : SharedChatClient) { }

    private static IDisposable? GetClientLifetime(HttpClient client) =>
        ReferenceEquals(client, SharedProbeClient) || ReferenceEquals(client, SharedChatClient) ? null : client;

    /// <summary>Allows deterministic HTTP-handler injection for automated tests.</summary>
    public OpenAiCompatibleClient(Func<TimeSpan, HttpClient> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public async Task<string> ProbeAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        var uri = BuildUri(baseUrl, "models");
        var client = _clientFactory(TimeSpan.FromSeconds(15));
        using var clientLifetime = GetClientLifetime(client);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (apiKey ?? string.Empty).Trim());
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await FormatError(response, cancellationToken));
        return $"Connection successful (HTTP {(int)response.StatusCode}).";
    }

    public async Task<string> ChatAsync(string baseUrl, string model, string apiKey, IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0) throw new ArgumentException("At least one message is required.", nameof(messages));
        var uri = BuildUri(baseUrl, "chat/completions");
        var payload = new
        {
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = false
        };
        var client = _clientFactory(TimeSpan.FromSeconds(120));
        using var clientLifetime = GetClientLifetime(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (apiKey ?? string.Empty).Trim());
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await FormatError(response, cancellationToken));

        using var document = await ReadJsonDocumentAsync(response.Content, MaxJsonResponseBytes, cancellationToken);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("The provider returned no choices.");
        var message = choices[0].GetProperty("message");
        if (!message.TryGetProperty("content", out var content))
            throw new InvalidOperationException("The provider response has no message content.");
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind == JsonValueKind.Null) return string.Empty;
        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = content.EnumerateArray()
                .Where(p => p.TryGetProperty("text", out _))
                .Select(p => p.GetProperty("text").GetString())
                .Where(s => !string.IsNullOrEmpty(s));
            return string.Join(Environment.NewLine, parts);
        }
        throw new InvalidOperationException("The provider returned an unsupported content format.");
    }


    /// <summary>Sends one user-selected image to an OpenAI-compatible vision-capable chat-completions endpoint.</summary>
    public async Task<string> AnalyzeImageAsync(
        string baseUrl,
        string model,
        string apiKey,
        IReadOnlyList<ChatMessage> messages,
        byte[] imageBytes,
        string imageContentType,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
            throw new ArgumentException("At least one message is required.", nameof(messages));
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0)
            throw new ArgumentException("The selected image is empty.", nameof(imageBytes));
        if (imageBytes.Length > 10 * 1024 * 1024)
            throw new ArgumentException("Images must be 10 MB or smaller.", nameof(imageBytes));
        if (imageContentType is not ("image/jpeg" or "image/png" or "image/webp" or "image/bmp" or "image/gif"))
            throw new ArgumentException("The image type must be JPEG, PNG, WebP, BMP, or GIF.", nameof(imageContentType));
        if (!string.Equals(messages[^1].Role, "user", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The final chat message must be from the user.", nameof(messages));

        var dataUrl = $"data:{imageContentType};base64,{Convert.ToBase64String(imageBytes)}";
        var payloadMessages = new JsonArray();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (index == messages.Count - 1)
            {
                var parts = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = message.Content
                    },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = dataUrl,
                            ["detail"] = "high"
                        }
                    }
                };
                payloadMessages.Add(new JsonObject { ["role"] = message.Role, ["content"] = parts });
            }
            else
            {
                payloadMessages.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Content });
            }
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = payloadMessages,
            ["stream"] = false
        };

        var client = _clientFactory(TimeSpan.FromSeconds(120));
        using var clientLifetime = GetClientLifetime(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (apiKey ?? string.Empty).Trim());
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await FormatError(response, cancellationToken);
            var looksLikeVisionError =
                response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity ||
                detail.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("image", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("multimodal", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("image_url", StringComparison.OrdinalIgnoreCase);
            if (looksLikeVisionError)
                throw new InvalidOperationException(
                    "The configured provider or model could not accept this image. Choose a model that supports image/vision input and verify that the provider's chat-completions endpoint accepts image_url content. Provider detail: " + detail);
            throw new InvalidOperationException("Image analysis failed: " + detail);
        }

        using var document = await ReadJsonDocumentAsync(response.Content, MaxJsonResponseBytes, cancellationToken);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("The vision model returned no answer. The provider may not support image analysis.");

        var messageNode = choices[0].GetProperty("message");
        if (!messageNode.TryGetProperty("content", out var content))
            throw new InvalidOperationException("The vision model did not return an answer. Check that the selected model supports image analysis.");

        var answer = ReadContent(content);
        if (string.IsNullOrWhiteSpace(answer))
            throw new InvalidOperationException("The vision model returned an empty answer. It may not support image analysis or could not interpret this image.");
        return answer;
    }

    /// <summary>Streams response text from OpenAI-compatible providers, with JSON fallback.</summary>
    public async Task<string> ChatStreamAsync(
        string baseUrl,
        string model,
        string apiKey,
        IReadOnlyList<ChatMessage> messages,
        Action<string> onDelta,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0) throw new ArgumentException("At least one message is required.", nameof(messages));
        ArgumentNullException.ThrowIfNull(onDelta);

        var payload = new
        {
            model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            stream = true
        };
        var client = _clientFactory(TimeSpan.FromSeconds(120));
        using var clientLifetime = GetClientLifetime(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (apiKey ?? string.Empty).Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await FormatError(response, cancellationToken));

        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            using var document = await ReadJsonDocumentAsync(response.Content, MaxJsonResponseBytes, cancellationToken);
            if (!document.RootElement.TryGetProperty("choices", out var jsonChoices) || jsonChoices.GetArrayLength() == 0)
                throw new InvalidOperationException("The provider returned no choices.");
            var jsonMessage = jsonChoices[0].GetProperty("message");
            if (!jsonMessage.TryGetProperty("content", out var jsonContent))
                throw new InvalidOperationException("The provider response has no message content.");
            var fullText = ReadContent(jsonContent);
            if (!string.IsNullOrEmpty(fullText)) onDelta(fullText);
            return fullText;
        }

        if (response.Content.Headers.ContentLength is > MaxStreamResponseBytes)
            throw new InvalidOperationException("The provider stream exceeds the 8 MB safety limit.");

        var result = new StringBuilder();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limitedBody = new ResponseLimitStream(body, MaxStreamResponseBytes);
        using var reader = new StreamReader(limitedBody, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length > MaxSseLineCharacters)
                throw new InvalidOperationException("The provider sent an SSE line larger than the 64 KB safety limit.");
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0) continue;
            if (data.Equals("[DONE]", StringComparison.Ordinal)) break;

            JsonDocument chunk;
            try { chunk = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }

            using (chunk)
            {
                if (!chunk.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    continue;

                var choice = choices[0];
                if (!choice.TryGetProperty("delta", out var delta)) continue;
                var text = delta.TryGetProperty("content", out var content)
                    ? ReadContent(content)
                    : choice.TryGetProperty("text", out var legacyText) && legacyText.ValueKind == JsonValueKind.String
                        ? legacyText.GetString() ?? string.Empty
                        : string.Empty;
                if (text.Length == 0) continue;
                if (text.Length > MaxStreamedTextCharacters - result.Length)
                    throw new InvalidOperationException("The provider response text exceeds the 2 MB safety limit.");
                result.Append(text);
                onDelta(text);
            }
        }

        return result.ToString();
    }

    private static string ReadContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return string.Empty;
        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Join(Environment.NewLine, content.EnumerateArray()
                .Where(part => part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").ValueKind == JsonValueKind.String
                    ? part.GetProperty("text").GetString()
                    : null)
                .Where(part => !string.IsNullOrEmpty(part)));
        }
        return string.Empty;
    }

    /// <summary>Sends a tool-capable agent turn. The provider must implement OpenAI-compatible tool calling.</summary>
    public async Task<AgentTurnResult> AgentTurnAsync(
        string baseUrl,
        string model,
        string apiKey,
        JsonArray messages,
        JsonArray toolDefinitions,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0) throw new ArgumentException("At least one message is required.", nameof(messages));
        if (toolDefinitions.Count == 0) throw new ArgumentException("At least one tool definition is required.", nameof(toolDefinitions));

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = JsonNode.Parse(messages.ToJsonString())!.AsArray(),
            ["tools"] = JsonNode.Parse(toolDefinitions.ToJsonString())!.AsArray(),
            ["tool_choice"] = "auto",
            ["stream"] = false
        };

        var client = _clientFactory(TimeSpan.FromSeconds(120));
        using var clientLifetime = GetClientLifetime(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(baseUrl, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (apiKey ?? string.Empty).Trim());
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await FormatError(response, cancellationToken));

        var (responseBytes, _) = await ReadLimitedResponseBytesAsync(
            response.Content, MaxJsonResponseBytes, cancellationToken, rejectOversized: true);
        string responseJson;
        try { responseJson = Encoding.UTF8.GetString(responseBytes); }
        finally { Array.Clear(responseBytes, 0, responseBytes.Length); }
        JsonObject root;
        try { root = JsonNode.Parse(responseJson) as JsonObject ?? throw new JsonException("Root is not an object."); }
        catch (JsonException) { throw new InvalidOperationException("The provider returned invalid JSON."); }

        var message = root["choices"] is JsonArray choices && choices.Count > 0
            ? choices[0]?["message"] as JsonObject
            : null;
        if (message is null) throw new InvalidOperationException("The provider returned no assistant message.");

        var content = ExtractContent(message["content"]);
        var calls = new List<AgentToolCall>();
        if (message["tool_calls"] is JsonArray toolCalls)
        {
            var validNodes = new JsonArray();
            foreach (var node in toolCalls)
            {
                if (node is not JsonObject call) continue;
                var id = ReadStringLoose(call["id"]);
                var function = call["function"] as JsonObject;
                var name = ReadStringLoose(function?["name"]);
                var argsNode = function?["arguments"];
                var argsJson = argsNode is JsonValue value && value.TryGetValue<string>(out var raw)
                    ? (string.IsNullOrWhiteSpace(raw) ? "{}" : raw)
                    : argsNode?.ToJsonString() ?? "{}";
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                calls.Add(new AgentToolCall(id, name, argsJson));
                validNodes.Add(node.DeepClone());
            }

            // Never echo a tool call we did not answer back to the provider: an assistant message whose
            // tool_calls have no matching tool result is rejected with HTTP 400 by OpenAI-compatible servers.
            if (validNodes.Count != toolCalls.Count || validNodes.Count == 0)
            {
                if (validNodes.Count == 0) message.Remove("tool_calls");
                else message["tool_calls"] = validNodes;
            }
        }

        return new AgentTurnResult((JsonObject)message.DeepClone(), content, calls);
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var (bytes, _) = await ReadLimitedResponseBytesAsync(content, maxBytes, cancellationToken, rejectOversized: true);
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    /// <summary>
    /// Reads at most maxBytes from an HTTP response body. Oversized successful payloads fail closed;
    /// error payloads may return a bounded prefix for diagnostics. Unknown-length bodies that reach
    /// the cap are treated as truncated rather than waiting indefinitely for another byte.
    /// </summary>
    private static async Task<(byte[] Bytes, bool Truncated)> ReadLimitedResponseBytesAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken,
        bool rejectOversized)
    {
        var declaredLength = content.Headers.ContentLength;
        if (rejectOversized && declaredLength.HasValue && declaredLength.Value > maxBytes)
            throw new InvalidOperationException($"The provider response exceeds the {maxBytes / 1024} KB safety limit.");

        var initialCapacity = declaredLength.HasValue && declaredLength.Value >= 0 && declaredLength.Value <= maxBytes
            ? (int)declaredLength.Value
            : Math.Min(maxBytes, 16 * 1024);
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(initialCapacity);
        var buffer = new byte[16 * 1024];
        var truncated = declaredLength.HasValue && declaredLength.Value > maxBytes;
        try
        {
            while (output.Length < maxBytes)
            {
                var remaining = maxBytes - (int)output.Length;
                var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) break;
                output.Write(buffer, 0, read);
            }

            if (output.Length == maxBytes &&
                (!declaredLength.HasValue || declaredLength.Value != maxBytes))
                truncated = true;

            if (rejectOversized && truncated)
                throw new InvalidOperationException($"The provider response exceeds the {maxBytes / 1024} KB safety limit.");

            return (output.ToArray(), truncated);
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
            if (output.TryGetBuffer(out var segment) && segment.Array is not null)
                Array.Clear(segment.Array, segment.Offset, segment.Count);
        }
    }

    private sealed class ResponseLimitStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _bytesRead;
        private bool _endChecked;

        public ResponseLimitStream(Stream inner, long maxBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            _maxBytes = maxBytes;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            if (_bytesRead < _maxBytes)
            {
                var count = (int)Math.Min(buffer.Length, _maxBytes - _bytesRead);
                var read = _inner.Read(buffer[..count]);
                _bytesRead += read;
                return read;
            }

            if (_endChecked) return 0;
            _endChecked = true;
            if (_inner.ReadByte() >= 0)
                throw new InvalidDataException("The provider stream exceeded the 8 MB safety limit.");
            return 0;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ReadAsyncCore(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (buffer.Length == 0) return 0;
            if (_bytesRead < _maxBytes)
            {
                var count = (int)Math.Min(buffer.Length, _maxBytes - _bytesRead);
                var read = await _inner.ReadAsync(buffer[..count], cancellationToken);
                _bytesRead += read;
                return read;
            }

            if (_endChecked) return 0;
            _endChecked = true;
            var probe = new byte[1];
            try
            {
                if (await _inner.ReadAsync(probe.AsMemory(), cancellationToken) > 0)
                    throw new InvalidDataException("The provider stream exceeded the 8 MB safety limit.");
                return 0;
            }
            finally
            {
                Array.Clear(probe, 0, probe.Length);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }

    private static string ReadStringLoose(JsonNode? node)
    {
        if (node is not JsonValue value) return string.Empty;
        if (value.TryGetValue<string>(out var text)) return text ?? string.Empty;
        // Some compatible providers emit numeric ids; accept them instead of failing the whole task.
        return value.ToJsonString().Trim('"');
    }

    private static string ExtractContent(JsonNode? content)
    {
        if (content is null) return string.Empty;
        if (content is JsonValue value && value.TryGetValue<string>(out var text)) return text ?? string.Empty;
        if (content is JsonArray array)
        {
            return string.Join(Environment.NewLine, array
                .OfType<JsonObject>()
                .Select(part => part["text"] is JsonValue t && t.TryGetValue<string>(out var partText) ? partText : null)
                .Where(partText => !string.IsNullOrEmpty(partText)));
        }
        return string.Empty;
    }

    private static Uri BuildUri(string baseUrl, string endpoint)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Base URL must be a valid HTTP or HTTPS URL.", nameof(baseUrl));
        if (!string.IsNullOrEmpty(baseUri.UserInfo))
            throw new ArgumentException("Do not put usernames or passwords inside the base URL.", nameof(baseUrl));
        return new Uri(baseUri, endpoint);
    }

    private static async Task<string> FormatError(HttpResponseMessage response, CancellationToken token)
    {
        string body;
        var truncated = false;
        byte[]? bytes = null;
        try
        {
            var read = await ReadLimitedResponseBytesAsync(
                response.Content, MaxErrorResponseBytes, token, rejectOversized: false);
            bytes = read.Bytes;
            truncated = read.Truncated;
            body = Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            body = string.Empty;
        }
        finally
        {
            if (bytes is not null) Array.Clear(bytes, 0, bytes.Length);
        }

        if (body.Length > 800) body = body[..800] + "…";
        if (truncated) body += " [error body truncated at 32 KB]";
        return string.IsNullOrWhiteSpace(body)
            ? $"Provider request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
            : $"Provider request failed with HTTP {(int)response.StatusCode}: {body}";
    }
}

public sealed record AgentToolCall(string Id, string Name, string ArgumentsJson);
public sealed record AgentTurnResult(JsonObject AssistantMessage, string Content, IReadOnlyList<AgentToolCall> ToolCalls);
