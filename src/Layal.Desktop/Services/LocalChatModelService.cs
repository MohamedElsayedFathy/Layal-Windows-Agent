using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Layal.Models;

namespace Layal.Services;

/// <summary>
/// Sends chat only to an explicitly configured loopback OpenAI-compatible endpoint.
/// This service deliberately rejects every non-loopback host so chat cannot silently become cloud traffic.
/// </summary>
public sealed class LocalChatModelService
{
    public const string DefaultBaseUrl = "http://127.0.0.1:11434/v1";
    private readonly OpenAiCompatibleClient _client;

    public LocalChatModelService() : this(new OpenAiCompatibleClient()) { }

    public LocalChatModelService(OpenAiCompatibleClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public static bool IsConfigured(string? baseUrl, string? modelId) =>
        !string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(modelId);

    public static string ValidateBaseUrl(string? baseUrl)
    {
        var value = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (value.Length == 0 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.IsLoopback ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "The local chat endpoint must be an absolute HTTP(S) loopback URL such as http://127.0.0.1:11434/v1. Remote hosts are blocked.");
        }

        return value;
    }

    public static string NormalizeBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return DefaultBaseUrl;
        try { return ValidateBaseUrl(baseUrl); }
        catch (InvalidOperationException) { return DefaultBaseUrl; }
    }

    public static string ValidateModelId(string? modelId)
    {
        var value = (modelId ?? string.Empty).Trim();
        if (value.Length is < 1 or > 200 || value.Any(char.IsControl))
            throw new InvalidOperationException("Enter a local model ID containing 1 to 200 printable characters.");
        return value;
    }

    public static string NormalizeModelId(string? modelId)
    {
        var value = (modelId ?? string.Empty).Trim();
        return value.Length <= 200 && !value.Any(char.IsControl) ? value : string.Empty;
    }

    public Task<string> TestConnectionAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        var validatedBaseUrl = ValidateBaseUrl(baseUrl);
        return _client.ProbeAsync(validatedBaseUrl, string.Empty, cancellationToken);
    }

    public async Task<string> RunModelSmokeTestAsync(
        string baseUrl,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var messages = new[]
        {
            new ChatMessage
            {
                Role = "user",
                Content = "Reply with a brief confirmation that the local model is ready."
            }
        };
        var reply = await GetReplyAsync(baseUrl, modelId, messages, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(reply) || reply.Length > 2048)
            throw new InvalidOperationException("The local model did not return a valid smoke-test response.");
        return reply.Trim();
    }

    public async Task<string> GetReplyAsync(
        string baseUrl,
        string modelId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
            throw new ArgumentException("At least one chat message is required.", nameof(messages));

        var validatedBaseUrl = ValidateBaseUrl(baseUrl);
        var validatedModelId = ValidateModelId(modelId);
        var response = await _client.ChatAsync(
            validatedBaseUrl,
            validatedModelId,
            string.Empty,
            messages,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidOperationException("The local model returned an empty reply.");
        return response.Trim();
    }
}
