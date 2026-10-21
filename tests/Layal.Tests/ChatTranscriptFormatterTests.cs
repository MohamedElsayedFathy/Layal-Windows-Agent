using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ChatTranscriptFormatterTests
{
    [TestMethod]
    public void FormatIncludesRoleAndContentForEachNonEmptyMessage()
    {
        var messages = new[]
        {
            new ChatMessage { Role = "user", DisplayRole = "You", Content = "Hello Layal" },
            new ChatMessage { Role = "assistant", DisplayRole = "Layal", Content = "Hello!" },
            new ChatMessage { Role = "status", DisplayRole = "Status", Content = " " }
        };

        var formatted = ChatTranscriptFormatter.Format(messages);
        var expected = $"You: Hello Layal{Environment.NewLine}{Environment.NewLine}Layal: Hello!";

        Assert.AreEqual(expected, formatted);
    }

    [TestMethod]
    public void FormatFallsBackToRoleWhenDisplayRoleIsMissing()
    {
        var messages = new[] { new ChatMessage { Role = "assistant", Content = "Answer" } };

        Assert.AreEqual("assistant: Answer", ChatTranscriptFormatter.Format(messages));
    }
}
