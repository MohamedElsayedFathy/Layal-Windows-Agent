using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ChatContextBuilderTests
{
    [TestMethod]
    public void BuildKeepsSystemPromptAndNewestMessagesWithinMessageLimit()
    {
        var messages = Enumerable.Range(1, 20)
            .Select(i => new ChatMessage { Role = i % 2 == 0 ? "assistant" : "user", Content = $"message-{i}" })
            .ToList();
        var result = ChatContextBuilder.Build("system prompt", messages, maxMessages: 4, maxCharacters: 1000);
        Assert.AreEqual("system", result[0].Role);
        Assert.AreEqual("system prompt", result[0].Content);
        Assert.AreEqual(5, result.Count);
        Assert.AreEqual("message-17", result[1].Content);
        Assert.AreEqual("message-20", result[^1].Content);
    }

    [TestMethod]
    public void BuildHonorsCharacterBudgetWithoutDroppingNewestMessage()
    {
        var messages = new[]
        {
            new ChatMessage { Role = "user", Content = new string('a', 12) },
            new ChatMessage { Role = "assistant", Content = new string('b', 12) },
            new ChatMessage { Role = "user", Content = "latest" }
        };
        var result = ChatContextBuilder.Build("system", messages, maxMessages: 10, maxCharacters: 15);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("latest", result[^1].Content);
    }


    [TestMethod]
    public void BuildTruncatesOversizedNewestMessageToHardCharacterBudgetWithoutMutatingHistory()
    {
        var originalContent = "prefix-" + new string('x', 100);
        var latest = new ChatMessage { Role = "user", Content = originalContent };
        var result = ChatContextBuilder.Build(
            "system",
            new[] { new ChatMessage { Role = "assistant", Content = "older" }, latest },
            maxMessages: 10,
            maxCharacters: 20);

        Assert.AreEqual(2, result.Count, "Older history should not be included when the newest message consumes the budget.");
        Assert.AreEqual(20, result[^1].Content.Length);
        Assert.IsTrue(result[^1].Content.StartsWith("prefix-x", StringComparison.Ordinal));
        Assert.IsTrue(result[^1].Content.EndsWith("…[truncated]", StringComparison.Ordinal));
        Assert.AreEqual(originalContent, latest.Content, "The persisted/original message must not be modified.");
        Assert.IsTrue(result.Skip(1).Sum(message => message.Content.Length) <= 20);
    }

    [TestMethod]
    public void BuildHandlesCharacterBudgetsShorterThanTruncationMarker()
    {
        var latest = new ChatMessage { Role = "user", Content = "a much longer question" };
        var result = ChatContextBuilder.Build("system", new[] { latest }, maxCharacters: 5);

        Assert.AreEqual(5, result[^1].Content.Length);
        Assert.AreEqual("a muc", result[^1].Content);
        Assert.AreEqual("a much longer question", latest.Content);
    }

    [TestMethod]
    public void BuildExcludesGmailDerivedMessagesByDefaultAndAllowsOnlyAnExplicitOneShotContext()
    {
        var priorGmailMessage = new ChatMessage
        {
            Role = "user",
            Content = "[Gmail email for review] prior private email",
            IsGmailEphemeralData = true
        };
        var currentGmailMessage = new ChatMessage
        {
            Role = "user",
            Content = "[Gmail email for review] current one-turn email",
            IsGmailEphemeralData = true
        };
        var normalMessage = new ChatMessage { Role = "user", Content = "ordinary conversation" };

        var defaultContext = ChatContextBuilder.Build(
            "system",
            new[] { priorGmailMessage, normalMessage, currentGmailMessage });

        Assert.AreEqual(2, defaultContext.Count);
        Assert.AreEqual("ordinary conversation", defaultContext[1].Content);

        var oneShotContext = ChatContextBuilder.Build(
            "system",
            new[] { normalMessage, currentGmailMessage },
            includeEphemeralGmailData: true);

        Assert.AreEqual(3, oneShotContext.Count);
        Assert.AreEqual("ordinary conversation", oneShotContext[1].Content);
        Assert.AreEqual("[Gmail email for review] current one-turn email", oneShotContext[2].Content);
        Assert.IsFalse(oneShotContext.Any(message => message.Content.Contains("prior private email", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void BuildIgnoresSystemAndStatusMessagesFromConversationHistory()
    {
        var messages = new[]
        {
            new ChatMessage { Role = "system", Content = "old system prompt" },
            new ChatMessage { Role = "status", Content = "noise" },
            new ChatMessage { Role = "user", Content = "actual question" }
        };
        var result = ChatContextBuilder.Build("trusted prompt", messages);
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("trusted prompt", result[0].Content);
        Assert.AreEqual("actual question", result[1].Content);
    }
}
