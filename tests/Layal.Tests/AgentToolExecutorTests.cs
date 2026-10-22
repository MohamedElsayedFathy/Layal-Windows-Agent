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
        Assert.IsNotNull(definitions.FirstOrDefault(n => n?["function"]?["name"]?.GetValue<string>() == "click_ui_control"));
        Assert.IsNotNull(definitions.FirstOrDefault(n => n?["function"]?["name"]?.GetValue<string>() == "fill_ui_text"));

        Assert.AreEqual(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.IsFalse(names.Any(string.IsNullOrWhiteSpace));
        foreach (var definition in definitions)
            Assert.IsNotNull(definition?["function"]?["parameters"] as JsonObject);
    }

    [TestMethod]
    public async Task NativeTextEntryRequiresWindowInspectionOptInBeforeLookingForAWindow()
    {
        var previousSettings = Layal.AppRuntime.Settings;
        try
        {
            Layal.AppRuntime.Settings = new Layal.Models.UserSettings { AllowWindowTitleInspection = false };
            using var temp = new TestTempDirectory();
            var executor = CreateExecutor(temp.Path);
            var approvalCalled = false;

            var result = await executor.ExecuteAsync(
                "fill_ui_text",
                "{\"window_title\":\"Nonexistent test window\",\"automation_id\":\"inputText\",\"text\":\"hello\"}",
                (_, _, _) => { approvalCalled = true; return Task.FromResult(false); },
                _ => { },
                CancellationToken.None);

            StringAssert.Contains(result, "disabled in Layal settings");
            Assert.IsFalse(approvalCalled, "No approval prompt or native window lookup should occur until the opt-in setting is enabled.");
        }
        finally
        {
            Layal.AppRuntime.Settings = previousSettings;
        }
    }

    [TestMethod]
    public void ClickToolRequiresAnExactSupportedSelector()
    {
        Assert.ThrowsExactly<ArgumentException>(() => WindowsUiAutomationService.NormalizeInvokeControlType("Edit"));
        Assert.ThrowsExactly<ArgumentException>(() => WindowsUiAutomationService.ValidateAutomationId(""));
        Assert.AreEqual("okButton", WindowsUiAutomationService.ValidateAutomationId("okButton"));
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

    private static AgentToolExecutor CreateExecutor(string root) => new(root, new MemoryService(System.IO.Path.Combine(root, "memory.dpapi")));
}
