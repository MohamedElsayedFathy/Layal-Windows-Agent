using System;
using System.Collections.Generic;
using System.Linq;
using Layal.Models;

namespace Layal.Services;

/// <summary>Builds bounded recent chat context to avoid resending the entire saved transcript on each turn.</summary>
public static class ChatContextBuilder
{
    public const int DefaultMaxMessages = 14;
    public const int DefaultMaxCharacters = 12000;
    private const string TruncationMarker = "…[truncated]";

    public static IReadOnlyList<ChatMessage> Build(
        string systemPrompt,
        IEnumerable<ChatMessage> conversation,
        int maxMessages = DefaultMaxMessages,
        int maxCharacters = DefaultMaxCharacters)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(conversation);
        if (maxMessages < 1) throw new ArgumentOutOfRangeException(nameof(maxMessages));
        if (maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters));

        var eligible = conversation.Where(message => message.Role is "user" or "assistant").ToList();
        var selectedNewestFirst = new List<ChatMessage>();
        var characters = 0;
        for (var i = eligible.Count - 1; i >= 0 && selectedNewestFirst.Count < maxMessages; i--)
        {
            var message = eligible[i];
            var content = message.Content ?? string.Empty;

            // Keep the latest message, but never let one oversized message bypass the context budget.
            // The marker makes truncation visible to the model and the caller's original message is unchanged.
            if (selectedNewestFirst.Count == 0 && content.Length > maxCharacters)
            {
                selectedNewestFirst.Add(CloneWithContent(message, TruncateToBudget(content, maxCharacters)));
                break;
            }

            // Older messages are included only whole; stop rather than exceeding the remaining budget.
            if (content.Length > maxCharacters - characters) break;

            selectedNewestFirst.Add(message);
            characters += content.Length;
            if (characters >= maxCharacters) break;
        }

        selectedNewestFirst.Reverse();
        var result = new List<ChatMessage>(selectedNewestFirst.Count + 1)
        {
            new() { Role = "system", Content = systemPrompt }
        };
        result.AddRange(selectedNewestFirst);
        return result;
    }

    private static string TruncateToBudget(string content, int maxCharacters)
    {
        if (content.Length <= maxCharacters) return content;
        if (maxCharacters <= TruncationMarker.Length)
            return content[..maxCharacters];

        return content[..(maxCharacters - TruncationMarker.Length)] + TruncationMarker;
    }

    private static ChatMessage CloneWithContent(ChatMessage message, string content) => new()
    {
        Role = message.Role,
        Content = content,
        DisplayRole = message.DisplayRole,
        CreatedAt = message.CreatedAt,
        Direction = message.Direction
    };
}
