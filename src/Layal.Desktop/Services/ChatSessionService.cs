using System;
using System.Collections.Generic;
using System.Linq;
using Layal.Models;

namespace Layal.Services;

/// <summary>Encrypted, multi-conversation local history with bounded retention and message sizes.</summary>
public sealed class ChatSessionService
{
    public const int MaxSavedConversations = 50;
    public const int MaxMessagesPerConversation = 200;
    public const int MaxMessageCharacters = 16000;
    public const int MaxConversationCharacters = 100000;
    public const int MaxConversationTitleCharacters = 120;
    private const string TruncationMarker = "…[truncated]";

    private readonly ProtectedJsonStore<List<ChatConversation>> _store;
    private readonly ProtectedJsonStore<List<ChatMessage>> _legacyStore;
    private readonly List<ChatConversation> _sessions;
    private string _defaultTitle;
    private string _currentId = string.Empty;

    public ChatSessionService(string path, ProtectedJsonStore<List<ChatMessage>> legacyStore, string defaultTitle = "New conversation")
    {
        _store = new ProtectedJsonStore<List<ChatConversation>>(path);
        _legacyStore = legacyStore;
        _defaultTitle = string.IsNullOrWhiteSpace(defaultTitle) ? "New conversation" : BoundTitle(defaultTitle.Trim());
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
                    Messages = BoundMessages(legacyMessages)
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

    /// <summary>Updates the localized placeholder title without renaming user-authored conversations.</summary>
    public void SetDefaultTitle(string defaultTitle)
    {
        if (string.IsNullOrWhiteSpace(defaultTitle)) return;

        var previousDefault = _defaultTitle;
        var nextDefault = BoundTitle(defaultTitle.Trim());
        _defaultTitle = nextDefault;

        foreach (var session in _sessions)
        {
            if (session.Messages.Count == 0 &&
                (string.Equals(session.Title, previousDefault, StringComparison.Ordinal) ||
                 session.Title is "New conversation" or "محادثة جديدة"))
            {
                session.Title = nextDefault;
            }
        }

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
        ArgumentNullException.ThrowIfNull(messages);
        var current = FindCurrent();
        current.Messages = BoundMessages(messages);
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
        Title = BoundTitle(title),
        CreatedAt = DateTimeOffset.Now,
        UpdatedAt = DateTimeOffset.Now,
        Messages = new List<ChatMessage>()
    };

    private void Save()
    {
        ApplyRetention();
        _store.Save(_sessions);
    }

    private void ApplyRetention()
    {
        foreach (var session in _sessions)
        {
            session.Title = BoundTitle(session.Title);
            session.Messages = BoundMessages(session.Messages ?? new List<ChatMessage>());
        }

        if (_sessions.Count > MaxSavedConversations)
        {
            var ordered = _sessions
                .OrderByDescending(s => s.UpdatedAt)
                .ThenByDescending(s => s.CreatedAt)
                .ToList();
            var current = ordered.FirstOrDefault(s => s.Id == _currentId);
            var kept = new List<ChatConversation>(MaxSavedConversations);
            if (current is not null) kept.Add(current);

            foreach (var session in ordered)
            {
                if (kept.Count >= MaxSavedConversations) break;
                if (current is not null && session.Id == current.Id) continue;
                kept.Add(session);
            }

            _sessions.Clear();
            _sessions.AddRange(kept);
        }

        if (!_sessions.Any(s => s.Id == _currentId) && _sessions.Count > 0)
            _currentId = _sessions.OrderByDescending(s => s.UpdatedAt).First().Id;
    }

    private static List<ChatMessage> BoundMessages(IEnumerable<ChatMessage> messages)
    {
        var source = messages.ToList();
        var newestFirst = new List<ChatMessage>(Math.Min(source.Count, MaxMessagesPerConversation));
        var totalCharacters = 0;

        for (var i = source.Count - 1; i >= 0 && newestFirst.Count < MaxMessagesPerConversation; i--)
        {
            var message = source[i];
            if (message is null) continue;

            var originalContent = message.Content ?? string.Empty;
            var content = TruncateText(originalContent, MaxMessageCharacters);
            var remaining = MaxConversationCharacters - totalCharacters;
            if (content.Length > remaining)
            {
                if (newestFirst.Count > 0 || remaining <= 0) break;
                content = TruncateText(content, remaining);
            }

            newestFirst.Add(string.Equals(content, originalContent, StringComparison.Ordinal)
                ? message
                : CloneWithContent(message, content));
            totalCharacters += content.Length;
            if (totalCharacters >= MaxConversationCharacters) break;
        }

        newestFirst.Reverse();
        return newestFirst;
    }

    private static string TruncateText(string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters) return value;
        if (maxCharacters <= TruncationMarker.Length)
            return value[..maxCharacters];
        return value[..(maxCharacters - TruncationMarker.Length)] + TruncationMarker;
    }

    private static string BoundTitle(string? title)
    {
        var value = (title ?? string.Empty).Trim();
        return value.Length <= MaxConversationTitleCharacters
            ? value
            : value[..(MaxConversationTitleCharacters - 1)] + "…";
    }

    private static ChatMessage CloneWithContent(ChatMessage message, string content) => new()
    {
        Role = message.Role,
        Content = content,
        DisplayRole = message.DisplayRole,
        CreatedAt = message.CreatedAt,
        Direction = message.Direction
    };

    private static string BuildTitle(IEnumerable<ChatMessage> messages, string fallback)
    {
        var firstUserMessage = messages.FirstOrDefault(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content));
        if (firstUserMessage is null) return fallback;
        var title = string.Join(" ", firstUserMessage.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return title.Length <= 48 ? title : title[..48] + "…";
    }
}
