using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class PermissionBrokerTests
{
    [TestMethod]
    public void FileReadClassificationDistinguishesLocalProcessingFromExternalDisclosure()
    {
        var arguments = JsonNode.Parse("""{"path":"notes/plan.md","start_character":0}""")!.AsObject();

        var local = PermissionRequestClassifier.Create("read_text_file", arguments, true, "Read file locally", "private contents");
        var external = PermissionRequestClassifier.Create("read_text_file", arguments, false, "Read and share file", "private contents");

        Assert.AreEqual(PermissionActionCategory.Read | PermissionActionCategory.LocalProcessing, local.Category);
        Assert.AreEqual(PermissionActionCategory.Read | PermissionActionCategory.ExternalShare, external.Category);
        Assert.AreEqual("notes/plan.md", local.Target);
    }

    [TestMethod]
    public void ApiDeleteIsClassifiedAsDeletionAndAuditTargetOmitsQueryData()
    {
        var arguments = JsonNode.Parse("""{"method":"DELETE","connection_name":"Tracker","path":"/issues/7","query_json":"{\"token\":\"private\"}"}""")!.AsObject();

        var request = PermissionRequestClassifier.Create("call_app_api", arguments, false, "Call API", "private body");

        Assert.AreEqual(PermissionActionCategory.Delete | PermissionActionCategory.ExternalShare, request.Category);
        Assert.AreEqual("DELETE Tracker /issues/7", request.Target);
        Assert.IsFalse(request.Target.Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UrlAuditTargetDropsCredentialsQueryAndFragment()
    {
        var arguments = JsonNode.Parse("""{"url":"https://user:pass@example.test/path?token=secret#private"}""")!.AsObject();

        var request = PermissionRequestClassifier.Create("browser_navigate", arguments, false, "Navigate", "full dialog details");

        Assert.AreEqual("https://example.test/path", request.Target);
    }

    [TestMethod]
    public async Task BrokerPassesExactPromptAndAuditsDecisionWithoutLoggingSensitiveDetails()
    {
        var broker = new PermissionBroker();
        var events = new List<string>();
        string? receivedTitle = null;
        string? receivedDetails = null;

        var approved = await broker.RequestAsync(
            new PermissionRequest("create_text_file", PermissionActionCategory.Modify, "draft.md", "Create file", "secret document body"),
            (title, details, _) =>
            {
                receivedTitle = title;
                receivedDetails = details;
                return Task.FromResult(true);
            },
            events.Add,
            CancellationToken.None);

        Assert.IsTrue(approved);
        Assert.AreEqual("Create file", receivedTitle);
        Assert.AreEqual("secret document body", receivedDetails);
        Assert.AreEqual(2, events.Count);
        StringAssert.Contains(events[0], "Permission requested");
        StringAssert.Contains(events[0], "actor=local user");
        StringAssert.Contains(events[1], "Permission approved");
        StringAssert.Contains(events[1], "target=draft.md");
        Assert.IsFalse(string.Join(Environment.NewLine, events).Contains("secret document body", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DeclinedFileCreationIsAuditedAndCreatesNoFileOrContentLog()
    {
        using var temp = new TestTempDirectory();
        var executor = new AgentToolExecutor(
            temp.Path,
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")));
        var events = new List<string>();
        var prompted = false;

        var result = await executor.ExecuteAsync(
            "create_text_file",
            """{"path":"draft.md","content":"secret document body"}""",
            (_, details, _) =>
            {
                prompted = true;
                StringAssert.Contains(details, "secret document body");
                return Task.FromResult(false);
            },
            events.Add,
            CancellationToken.None);

        Assert.IsTrue(prompted);
        StringAssert.Contains(result, "declined");
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "draft.md")));
        StringAssert.Contains(string.Join(Environment.NewLine, events), "category=Modify");
        StringAssert.Contains(string.Join(Environment.NewLine, events), "Permission denied");
        Assert.IsFalse(string.Join(Environment.NewLine, events).Contains("secret document body", StringComparison.Ordinal));
    }
}

