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
            var length = message.Content.Length;
            // Always preserve the latest message, even if it exceeds the budget by itself.
            if (selectedNewestFirst.Count > 0 && characters + length > maxCharacters) break;
            selectedNewestFirst.Add(message);
            characters += length;
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
}
