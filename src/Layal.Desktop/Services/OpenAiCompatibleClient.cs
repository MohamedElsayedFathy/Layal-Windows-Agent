using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Layal.Models;

namespace Layal.Services;

/// <summary>Client for OpenAI-compatible chat completions, including optional tool/function calling.</summary>
public sealed class OpenAiCompatibleClient
{
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await FormatError(response, cancellationToken));

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await FormatError(response, cancellationToken));

        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await using var jsonBody = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(jsonBody, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("choices", out var jsonChoices) || jsonChoices.GetArrayLength() == 0)
                throw new InvalidOperationException("The provider returned no choices.");
            var jsonMessage = jsonChoices[0].GetProperty("message");
            if (!jsonMessage.TryGetProperty("content", out var jsonContent))
                throw new InvalidOperationException("The provider response has no message content.");
            var fullText = ReadContent(jsonContent);
            if (!string.IsNullOrEmpty(fullText)) onDelta(fullText);
            return fullText;
        }

        var result = new StringBuilder();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(body);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await FormatError(response, cancellationToken));

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
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
            foreach (var node in toolCalls)
            {
                if (node is not JsonObject call) continue;
                var id = call["id"]?.GetValue<string>() ?? string.Empty;
                var function = call["function"] as JsonObject;
                var name = function?["name"]?.GetValue<string>() ?? string.Empty;
                var argsNode = function?["arguments"];
                var argsJson = argsNode is JsonValue value && value.TryGetValue<string>(out var raw)
                    ? raw ?? "{}"
                    : argsNode?.ToJsonString() ?? "{}";
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                calls.Add(new AgentToolCall(id, name, argsJson));
            }
        }

        return new AgentTurnResult((JsonObject)message.DeepClone(), content, calls);
    }

    private static string ExtractContent(JsonNode? content)
    {
        if (content is null) return string.Empty;
        if (content is JsonValue value && value.TryGetValue<string>(out var text)) return text ?? string.Empty;
        if (content is JsonArray array)
        {
            return string.Join(Environment.NewLine, array
                .OfType<JsonObject>()
                .Where(part => part["text"] is not null)
                .Select(part => part["text"]!.GetValue<string>()));
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
        try { body = await response.Content.ReadAsStringAsync(token); }
        catch { body = string.Empty; }
        if (body.Length > 800) body = body[..800] + "…";
        return string.IsNullOrWhiteSpace(body)
            ? $"Provider request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
            : $"Provider request failed with HTTP {(int)response.StatusCode}: {body}";
    }
}

public sealed record AgentToolCall(string Id, string Name, string ArgumentsJson);
public sealed record AgentTurnResult(JsonObject AssistantMessage, string Content, IReadOnlyList<AgentToolCall> ToolCalls);
