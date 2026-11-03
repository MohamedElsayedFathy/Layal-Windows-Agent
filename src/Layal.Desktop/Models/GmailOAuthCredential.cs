using System;

namespace Layal.Models;

/// <summary>Refresh token state for the read-only Gmail integration. Persisted in a DPAPI-protected store.</summary>
public sealed class GmailOAuthCredential
{
    public string ClientId { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.Now;
}

public sealed record GmailMessageSummary(
    string Id,
    string ThreadId,
    string From,
    string Subject,
    string Snippet,
    DateTimeOffset? ReceivedAt);

public sealed record GmailMessageDetail(
    string Id,
    string ThreadId,
    string From,
    string To,
    string Subject,
    string Snippet,
    DateTimeOffset? ReceivedAt,
    string PlainTextBody);
