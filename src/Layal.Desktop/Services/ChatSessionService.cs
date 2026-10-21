using System;
using System.Collections.Generic;
using System.Linq;
using Layal.Models;

namespace Layal.Services;

/// <summary>Encrypted, multi-conversation local history with one active conversation at a time.</summary>
public sealed class ChatSessionService
{
    private readonly ProtectedJsonStore<List<ChatConversation>> _store;
    private readonly ProtectedJsonStore<List<ChatMessage>> _legacyStore;
    private readonly List<ChatConversation> _sessions;
    private readonly string _defaultTitle;
    private string _currentId = string.Empty;

    public ChatSessionService(string path, ProtectedJsonStore<List<ChatMessage>> legacyStore, string defaultTitle = "New conversation")
    {
        _store = new ProtectedJsonStore<List<ChatConversation>>(path);
        _legacyStore = legacyStore;
        _defaultTitle = string.IsNullOrWhiteSpace(defaultTitle) ? "New conversation" : defaultTitle.Trim();
        _sessions = _store.Load(new List<ChatConversation>());

        if (_sessions.Count == 0)
        {
            var legacyMessages = _legacyStore.Load(new List<ChatMessage>());
            if (legacyMessages.Count > 0)
            {
                var migrated = new ChatConversation
                {
                    Title = BuildTitle(legacyMessages, _defaultTitle),
                    CreatedAt = legacyMessages.Min(m => m.CreatedAt),
                    UpdatedAt = legacyMessages.Max(m => m.CreatedAt),
                    Messages = legacyMessages.ToList()
                };
                _sessions.Add(migrated);
                _store.Save(_sessions);
                // Delete the old encrypted single-history file only after migration has been saved.
                _legacyStore.Delete();
            }
        }

        if (_sessions.Count == 0)
            _sessions.Add(CreateRecord(_defaultTitle));

        _currentId = _sessions.OrderByDescending(s => s.UpdatedAt).First().Id;
        Save();
    }

    public ChatConversation GetCurrent() => FindCurrent();

    public IReadOnlyList<ChatConversation> GetAll() =>
        _sessions.OrderByDescending(s => s.UpdatedAt).ToList();

    public ChatConversation CreateNewConversation(string? title = null)
    {
        var conversation = CreateRecord(string.IsNullOrWhiteSpace(title) ? _defaultTitle : title.Trim());
        _sessions.Add(conversation);
        _currentId = conversation.Id;
        Save();
        return conversation;
    }

    public ChatConversation SelectConversation(string id)
    {
        var conversation = _sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The selected saved conversation no longer exists.");
        _currentId = conversation.Id;
        conversation.UpdatedAt = DateTimeOffset.Now;
        Save();
        return conversation;
    }

    public void SaveCurrentMessages(IReadOnlyCollection<ChatMessage> messages)
    {
        var current = FindCurrent();
        current.Messages = messages.ToList();
        if (current.Messages.Count > 0)
            current.Title = BuildTitle(current.Messages, _defaultTitle);
        current.UpdatedAt = DateTimeOffset.Now;
        Save();
    }

    /// <summary>Deletes all saved conversations and starts one blank conversation.</summary>
    public ChatConversation ClearAll(string? title = null)
    {
        _sessions.Clear();
        _legacyStore.Delete();
        var fresh = CreateRecord(string.IsNullOrWhiteSpace(title) ? _defaultTitle : title.Trim());
        _sessions.Add(fresh);
        _currentId = fresh.Id;
        Save();
        return fresh;
    }

    private ChatConversation FindCurrent()
    {
        var current = _sessions.FirstOrDefault(s => string.Equals(s.Id, _currentId, StringComparison.Ordinal));
        if (current is not null) return current;
        current = _sessions.OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
        if (current is null)
        {
            current = CreateRecord(_defaultTitle);
            _sessions.Add(current);
        }
        _currentId = current.Id;
        Save();
        return current;
    }

    private ChatConversation CreateRecord(string title) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Title = title,
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now,
        Messages = new List<ChatMessage>()
    };

    private void Save() => _store.Save(_sessions);

    private static string BuildTitle(IEnumerable<ChatMessage> messages, string fallback)
    {
        var firstUserMessage = messages.FirstOrDefault(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content));
        if (firstUserMessage is null) return fallback;
        var title = string.Join(" ", firstUserMessage.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return title.Length <= 48 ? title : title[..48] + "…";
    }
}
