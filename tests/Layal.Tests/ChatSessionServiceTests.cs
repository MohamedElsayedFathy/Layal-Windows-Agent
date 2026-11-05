using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ChatSessionServiceTests
{
    [TestMethod]
    public void LegacyHistoryMigratesAndStartingANewConversationPreservesPreviousMessages()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Encrypted session persistence uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var legacyPath = System.IO.Path.Combine(temp.Path, "legacy-chat.dpapi");
        var sessionPath = System.IO.Path.Combine(temp.Path, "chat-sessions.dpapi");
        var legacy = new ProtectedJsonStore<List<ChatMessage>>(legacyPath);
        legacy.Save(new List<ChatMessage>
        {
            new() { Role = "user", Content = "Old conversation should be kept" },
            new() { Role = "assistant", Content = "Saved reply" }
        });

        var service = new ChatSessionService(sessionPath, legacy, "New conversation");
        var migrated = service.GetCurrent();
        Assert.AreEqual("Old conversation should be kept", migrated.Title);
        Assert.AreEqual(2, migrated.Messages.Count);

        var newConversation = service.CreateNewConversation("New conversation");
        Assert.AreEqual(2, service.GetAll().Count);
        Assert.AreEqual(0, newConversation.Messages.Count);
        Assert.AreEqual(2, service.GetAll().Single(c => c.Id == migrated.Id).Messages.Count);

        service.SaveCurrentMessages(new List<ChatMessage>
        {
            new() { Role = "user", Content = "A new topic" }
        });
        var reloaded = new ChatSessionService(sessionPath, legacy, "New conversation");
        Assert.AreEqual(2, reloaded.GetAll().Count);
        Assert.AreEqual("A new topic", reloaded.GetCurrent().Title);
        Assert.AreEqual(2, reloaded.GetAll().Single(c => c.Id == migrated.Id).Messages.Count);
    }


    [TestMethod]
    public void GmailDerivedTurnsAreRemovedFromLegacyMigrationAndNeverSavedAgain()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Encrypted session persistence uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var legacyPath = System.IO.Path.Combine(temp.Path, "legacy-chat.dpapi");
        var sessionsPath = System.IO.Path.Combine(temp.Path, "chat-sessions.dpapi");
        var legacy = new ProtectedJsonStore<List<ChatMessage>>(legacyPath);
        legacy.Save(new List<ChatMessage>
        {
            new() { Role = "user", Content = "Summarize this email\n\n[Gmail email for review]\nsecret-gmail-body" },
            new() { Role = "assistant", Content = "secret-gmail-derived-summary" },
            new() { Role = "user", Content = "Keep this ordinary question" },
            new() { Role = "assistant", Content = "Keep this ordinary answer" }
        });

        var service = new ChatSessionService(sessionsPath, legacy, "New conversation");

        Assert.AreEqual(2, service.GetCurrent().Messages.Count);
        Assert.AreEqual("Keep this ordinary question", service.GetCurrent().Messages[0].Content);
        Assert.AreEqual("Keep this ordinary question", service.GetCurrent().Title);
        Assert.IsFalse(service.GetCurrent().Messages.Any(message =>
            message.Content.Contains("secret-gmail", StringComparison.Ordinal)));
        Assert.AreEqual(0, legacy.Load(new List<ChatMessage>()).Count,
            "The old single-history file must be deleted only after the sanitized migration has been saved.");

        var normalUser = new ChatMessage { Role = "user", Content = "A later ordinary question" };
        var transientEmail = new ChatMessage
        {
            Role = "user",
            Content = "[Gmail email for review]\nnew-secret-gmail-body",
            IsGmailEphemeralData = true
        };
        var transientAnswer = new ChatMessage
        {
            Role = "assistant",
            Content = "new-secret-gmail-derived-answer",
            IsGmailEphemeralData = true
        };
        var normalAnswer = new ChatMessage { Role = "assistant", Content = "Ordinary answer" };

        service.SaveCurrentMessages(new[] { normalUser, transientEmail, transientAnswer, normalAnswer });

        var reloaded = new ChatSessionService(sessionsPath, legacy, "New conversation");
        var savedMessages = reloaded.GetCurrent().Messages;
        Assert.AreEqual(2, savedMessages.Count);
        Assert.AreEqual("A later ordinary question", savedMessages[0].Content);
        Assert.AreEqual("Ordinary answer", savedMessages[1].Content);
        Assert.IsFalse(savedMessages.Any(message =>
            message.Content.Contains("gmail", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void SavingMessagesBoundsMessageCountAndKeepsNewestEntries()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Encrypted session persistence uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var legacy = new ProtectedJsonStore<List<ChatMessage>>(System.IO.Path.Combine(temp.Path, "legacy.dpapi"));
        var service = new ChatSessionService(System.IO.Path.Combine(temp.Path, "sessions.dpapi"), legacy);

        var messages = Enumerable.Range(0, ChatSessionService.MaxMessagesPerConversation + 5)
            .Select(i => new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = $"message-{i}" })
            .ToList();
        service.SaveCurrentMessages(messages);

        var saved = service.GetCurrent().Messages;
        Assert.AreEqual(ChatSessionService.MaxMessagesPerConversation, saved.Count);
        Assert.AreEqual("message-5", saved[0].Content);
        Assert.AreEqual($"message-{messages.Count - 1}", saved[^1].Content);
    }

    [TestMethod]
    public void SavingMessagesBoundsTotalCharactersAndSingleMessageSizeWithoutMutatingCallerMessages()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Encrypted session persistence uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var legacy = new ProtectedJsonStore<List<ChatMessage>>(System.IO.Path.Combine(temp.Path, "legacy.dpapi"));
        var service = new ChatSessionService(System.IO.Path.Combine(temp.Path, "sessions.dpapi"), legacy);
        var oversized = new ChatMessage { Role = "user", Content = new string('x', ChatSessionService.MaxMessageCharacters + 500) };

        service.SaveCurrentMessages(new[] { oversized });

        var saved = service.GetCurrent().Messages;
        Assert.AreEqual(1, saved.Count);
        Assert.AreEqual(ChatSessionService.MaxMessageCharacters, saved[0].Content.Length);
        Assert.IsTrue(saved[0].Content.EndsWith("…[truncated]", StringComparison.Ordinal));
        Assert.AreEqual(ChatSessionService.MaxMessageCharacters + 500, oversized.Content.Length);
        Assert.IsTrue(saved.Sum(message => message.Content.Length) <= ChatSessionService.MaxConversationCharacters);
    }


    [TestMethod]
    public void SavingMessagesBoundsAggregateCharactersPerConversation()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Encrypted session persistence uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var legacy = new ProtectedJsonStore<List<ChatMessage>>(System.IO.Path.Combine(temp.Path, "legacy.dpapi"));
        var service = new ChatSessionService(System.IO.Path.Combine(temp.Path, "sessions.dpapi"), legacy);

        var messages = Enumerable.Range(0, 10)
            .Select(i => new ChatMessage { Role = i % 2 == 0 ? "user" : "assistant", Content = $"message-{i}:" + new string('x', 12000) })
            .ToList();
        service.SaveCurrentMessages(messages);

        var saved = service.GetCurrent().Messages;
        Assert.IsTrue(saved.Count < messages.Count);
        Assert.IsTrue(saved.Sum(message => message.Content.Length) <= ChatSessionService.MaxConversationCharacters);
        Assert.AreEqual($"message-{messages.Count - 1}:" + new string('x', 12000), saved[^1].Content);
    }

    [TestMethod]
    public void CreatingConversationsPrunesOldestAndPreservesCurrentConversation()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Encrypted session persistence uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var legacy = new ProtectedJsonStore<List<ChatMessage>>(System.IO.Path.Combine(temp.Path, "legacy.dpapi"));
        var service = new ChatSessionService(System.IO.Path.Combine(temp.Path, "sessions.dpapi"), legacy);
        var oldestId = service.GetCurrent().Id;
        ChatConversation? newest = null;

        for (var i = 0; i < ChatSessionService.MaxSavedConversations + 5; i++)
            newest = service.CreateNewConversation($"Conversation {i}");

        var saved = service.GetAll();
        Assert.AreEqual(ChatSessionService.MaxSavedConversations, saved.Count);
        Assert.IsNotNull(newest);
        Assert.AreEqual(newest!.Id, service.GetCurrent().Id);
        Assert.IsTrue(saved.Any(conversation => conversation.Id == newest.Id));
        Assert.IsFalse(saved.Any(conversation => conversation.Id == oldestId));
    }

    [TestMethod]
    public void ClearAllDeletesArchivedSessionsAndCreatesFreshBlankConversation()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Encrypted session persistence uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var legacy = new ProtectedJsonStore<List<ChatMessage>>(System.IO.Path.Combine(temp.Path, "legacy-chat.dpapi"));
        var path = System.IO.Path.Combine(temp.Path, "chat-sessions.dpapi");
        var service = new ChatSessionService(path, legacy, "New conversation");
        service.SaveCurrentMessages(new List<ChatMessage> { new() { Role = "user", Content = "Private old text" } });
        service.CreateNewConversation("Another conversation");

        var fresh = service.ClearAll("New conversation");

        Assert.AreEqual(1, service.GetAll().Count);
        Assert.AreEqual(fresh.Id, service.GetCurrent().Id);
        Assert.AreEqual(0, service.GetCurrent().Messages.Count);
        Assert.AreEqual(0, legacy.Load(new List<ChatMessage>()).Count);
    }
}
