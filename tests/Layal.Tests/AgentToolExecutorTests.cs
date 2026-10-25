using System.Text.Json.Nodes;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class AgentToolExecutorTests
{
    [TestMethod]
    public void ToolDefinitionsAreValidAndUnique()
    {
        using var temp = new TestTempDirectory();
        var executor = CreateExecutor(temp.Path);
        var definitions = executor.GetToolDefinitions();
        var names = definitions.Select(n => n?["function"]?["name"]?.GetValue<string>()).ToList();

        Assert.IsTrue(definitions.Count >= 10);
        Assert.AreEqual(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.IsFalse(names.Any(string.IsNullOrWhiteSpace));
        foreach (var definition in definitions)
            Assert.IsNotNull(definition?["function"]?["parameters"] as JsonObject);
    }

    [TestMethod]
    public async Task CurrentTimeToolRunsWithoutRequestingApproval()
    {
        using var temp = new TestTempDirectory();
        var executor = CreateExecutor(temp.Path);
        var approvalCalled = false;

        var result = await executor.ExecuteAsync("get_current_time", "{}", (_, _, _) => { approvalCalled = true; return Task.FromResult(false); }, _ => { }, CancellationToken.None);

        Assert.IsFalse(string.IsNullOrWhiteSpace(result));
        Assert.IsFalse(approvalCalled);
    }

    [TestMethod]
    public async Task TraversalPathIsRejected()
    {
        using var temp = new TestTempDirectory();
        var executor = CreateExecutor(temp.Path);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            "list_workspace_files", "{\"directory\":\"../outside\"}", (_, _, _) => Task.FromResult(true), _ => { }, CancellationToken.None));
    }

    [TestMethod]
    public async Task DeclinedFileCreationHasNoSideEffect()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var executor = CreateExecutor(workspace);
        var destination = System.IO.Path.Combine(workspace, "new-note.txt");

        var result = await executor.ExecuteAsync("create_text_file", "{\"path\":\"new-note.txt\",\"content\":\"hello\"}",
            (_, _, _) => Task.FromResult(false), _ => { }, CancellationToken.None);

        StringAssert.Contains(result, "declined");
        Assert.IsFalse(File.Exists(destination));
    }

    [TestMethod]
    public async Task ApprovedFileCreationWritesExactlyRequestedContent()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var executor = CreateExecutor(workspace);
        var approvalCount = 0;

        var result = await executor.ExecuteAsync("create_text_file", "{\"path\":\"new-note.txt\",\"content\":\"hello layal\"}",
            (_, _, _) => { approvalCount++; return Task.FromResult(true); }, _ => { }, CancellationToken.None);

        Assert.AreEqual(1, approvalCount);
        StringAssert.Contains(result, "File created");
        Assert.AreEqual("hello layal", File.ReadAllText(System.IO.Path.Combine(workspace, "new-note.txt")));
    }

    [TestMethod]
    public async Task FileCreationCannotOverwriteExistingFile()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var path = System.IO.Path.Combine(workspace, "existing.txt");
        File.WriteAllText(path, "original");
        var executor = CreateExecutor(workspace);

        await Assert.ThrowsExactlyAsync<IOException>(() => executor.ExecuteAsync("create_text_file", "{\"path\":\"existing.txt\",\"content\":\"replacement\"}",
            (_, _, _) => Task.FromResult(true), _ => { }, CancellationToken.None));
        Assert.AreEqual("original", File.ReadAllText(path));
    }

    [TestMethod]
    public async Task BuildToolRejectsNonProjectFileBeforeAskingForApproval()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(System.IO.Path.Combine(workspace, "notes.txt"), "not a project");
        var executor = CreateExecutor(workspace);
        var approvalCalled = false;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteAsync("run_dotnet_build",
            "{\"project_file\":\"notes.txt\",\"configuration\":\"Release\"}",
            (_, _, _) => { approvalCalled = true; return Task.FromResult(true); }, _ => { }, CancellationToken.None));
        Assert.IsFalse(approvalCalled);
    }

    [TestMethod]
    [DataRow("notes.txt:hidden")]
    [DataRow("nul.txt")]
    [DataRow("sub/COM1")]
    public async Task AlternateStreamsAndReservedDeviceNamesAreRejected(string path)
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var executor = CreateExecutor(workspace);
        var args = new JsonObject { ["path"] = path, ["content"] = "x" }.ToJsonString();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            "create_text_file", args, (_, _, _) => Task.FromResult(true), _ => { }, CancellationToken.None));
    }

    private static AgentToolExecutor CreateExecutor(string root) => new(root, new MemoryService(System.IO.Path.Combine(root, "memory.dpapi")));
}
