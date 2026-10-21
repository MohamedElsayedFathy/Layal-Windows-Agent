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
