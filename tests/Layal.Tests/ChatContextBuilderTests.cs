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
