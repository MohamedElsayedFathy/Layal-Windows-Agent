using System;
using System.Collections.Generic;
using System.Linq;
using Layal.Models;

namespace Layal.Services;

public static class ChatTranscriptFormatter
{
    public static string Format(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            messages
                .Where(message => !string.IsNullOrWhiteSpace(message.Content))
                .Select(message =>
                {
                    var role = string.IsNullOrWhiteSpace(message.DisplayRole)
                        ? message.Role
                        : message.DisplayRole;
                    return $"{role}: {message.Content.TrimEnd()}";
                }));
    }
}
