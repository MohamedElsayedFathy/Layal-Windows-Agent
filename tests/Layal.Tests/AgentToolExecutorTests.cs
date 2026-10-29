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
        Assert.IsNotNull(definitions.FirstOrDefault(n => n?["function"]?["name"]?.GetValue<string>() == "set_ui_checkbox"));

        Assert.AreEqual(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.IsFalse(names.Any(string.IsNullOrWhiteSpace));
        foreach (var definition in definitions)
            Assert.IsNotNull(definition?["function"]?["parameters"] as JsonObject);
    }

    [TestMethod]
    public void ToolDefinitionsIncludeSearchForLargeApiCatalogs()
    {
        using var temp = new TestTempDirectory();
        var executor = CreateExecutor(temp.Path);
        var definitions = executor.GetToolDefinitions();
        Assert.IsNotNull(definitions.FirstOrDefault(n =>
            n?["function"]?["name"]?.GetValue<string>() == "list_app_api_connections"));
    }

    [TestMethod]
    public async Task ApiCatalogSearchRequiresApprovalAndNeverReturnsCredentials()
    {
        using var temp = new TestTempDirectory();
        var apiService = new ApiConnectionService(System.IO.Path.Combine(temp.Path, "api-connections.dpapi"));
        apiService.Save(new Layal.Models.ApiConnectionProfile
        {
            Name = "Private test integration",
            BaseUrl = "https://private.example.test/v1",
            AuthenticationType = "Bearer",
            AuthSecret = "sensitive-token-not-for-model"
        });
        var executor = CreateExecutor(temp.Path, apiService);
        var approvalCount = 0;
        string approvalDetails = string.Empty;

        var result = await executor.ExecuteAsync(
            "list_app_api_connections",
            "{\"query\":\"Private test integration\"}",
            (title, details, _) =>
            {
                approvalCount++;
                approvalDetails = title + "\n" + details;
                return Task.FromResult(true);
            },
            _ => { },
            CancellationToken.None);

        Assert.AreEqual(1, approvalCount);
        StringAssert.Contains(approvalDetails, "API keys and authentication secrets are not included");
        StringAssert.Contains(result, "Private test integration");
        StringAssert.Contains(result, "https://private.example.test/v1/");
        Assert.IsFalse(result.Contains("sensitive-token-not-for-model", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DeclinedApiCatalogSearchDoesNotReturnConnectionDetails()
    {
        using var temp = new TestTempDirectory();
        var apiService = new ApiConnectionService(System.IO.Path.Combine(temp.Path, "api-connections.dpapi"));
        apiService.Save(new Layal.Models.ApiConnectionProfile
        {
            Name = "Private test integration",
            BaseUrl = "https://private.example.test/v1",
            AuthenticationType = "None"
        });
        var executor = CreateExecutor(temp.Path, apiService);

        var result = await executor.ExecuteAsync(
            "list_app_api_connections",
            "{\"query\":\"Private test integration\"}",
            (_, _, _) => Task.FromResult(false),
            _ => { },
            CancellationToken.None);

        StringAssert.Contains(result, "declined");
        Assert.IsFalse(result.Contains("private.example.test", StringComparison.Ordinal));
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
    public async Task CheckboxActionRequiresExplicitWindowInspectionOptIn()
    {
        var previousSettings = Layal.AppRuntime.Settings;
        try
        {
            Layal.AppRuntime.Settings = new Layal.Models.UserSettings { AllowWindowTitleInspection = false };
            using var temp = new TestTempDirectory();
            var executor = CreateExecutor(temp.Path);
            var approvalCalled = false;

            var result = await executor.ExecuteAsync(
                "set_ui_checkbox",
                "{\"window_title\":\"Nonexistent test window\",\"automation_id\":\"sampleCheckBox\",\"checked\":true}",
                (_, _, _) => { approvalCalled = true; return Task.FromResult(false); },
                _ => { },
                CancellationToken.None);

            StringAssert.Contains(result, "disabled in Layal settings");
            Assert.IsFalse(approvalCalled, "No approval prompt or native window lookup should happen until the user opts in.");
        }
        finally
        {
            Layal.AppRuntime.Settings = previousSettings;
        }
    }

    [TestMethod]
    public async Task CheckboxActionRejectsAStringInsteadOfAJsonBoolean()
    {
        using var temp = new TestTempDirectory();
        var executor = CreateExecutor(temp.Path);
        var approvalCalled = false;

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            "set_ui_checkbox",
            "{\"window_title\":\"Some Window\",\"automation_id\":\"sampleCheckBox\",\"checked\":\"true\"}",
            (_, _, _) => { approvalCalled = true; return Task.FromResult(true); },
            _ => { },
            CancellationToken.None));

        StringAssert.Contains(ex.Message, "checked");
        Assert.IsFalse(approvalCalled);
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
    public async Task FileCreationApprovalShowsProposedContentBeforeWriting()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var executor = CreateExecutor(workspace);
        var destination = System.IO.Path.Combine(workspace, "new-note.txt");
        var content = "first line" + Environment.NewLine + "second line";
        var args = new JsonObject { ["path"] = "new-note.txt", ["content"] = content }.ToJsonString();
        string? approvalDetails = null;

        var result = await executor.ExecuteAsync("create_text_file", args,
            (_, details, _) =>
            {
                approvalDetails = details;
                Assert.IsFalse(File.Exists(destination), "The preview must be shown before writing.");
                return Task.FromResult(true);
            }, _ => { }, CancellationToken.None);

        var details = approvalDetails ?? throw new AssertFailedException("No approval preview was shown.");
        StringAssert.Contains(details, "Proposed file content preview (max 4000 characters):");
        StringAssert.Contains(details, content);
        StringAssert.Contains(result, "File created");
        Assert.AreEqual(content, File.ReadAllText(destination));
    }

    [TestMethod]
    public async Task FileCreationApprovalPreviewIsBoundedAndDeclineHasNoSideEffect()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var executor = CreateExecutor(workspace);
        var destination = System.IO.Path.Combine(workspace, "large-note.txt");
        var content = new string('x', 5_000);
        var args = new JsonObject { ["path"] = "large-note.txt", ["content"] = content }.ToJsonString();
        string? approvalDetails = null;

        var result = await executor.ExecuteAsync("create_text_file", args,
            (_, details, _) => { approvalDetails = details; return Task.FromResult(false); },
            _ => { }, CancellationToken.None);

        var details = approvalDetails ?? throw new AssertFailedException("No approval preview was shown.");
        const string heading = "Proposed file content preview (max 4000 characters):";
        var headingStart = details.IndexOf(heading, StringComparison.Ordinal);
        Assert.IsTrue(headingStart >= 0, "The approval preview heading was missing.");
        var previewStart = headingStart + heading.Length + Environment.NewLine.Length;
        var preview = details[previewStart..];
        Assert.IsTrue(preview.Length <= 4_000, $"Preview length {preview.Length} exceeded the limit.");
        StringAssert.Contains(preview, "…[preview truncated; remaining content omitted]");
        Assert.IsFalse(details.Contains(content, StringComparison.Ordinal));
        StringAssert.Contains(result, "declined");
        Assert.IsFalse(File.Exists(destination));
    }

    [TestMethod]
    public async Task DeclinedReplacementPreviewShowsActualLinesAndLeavesFileUnchanged()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var path = System.IO.Path.Combine(workspace, "notes.txt");
        const string oldText = "old line one" + "\n" + "old line two";
        const string newText = "new line one" + "\n" + "new line two";
        var original = "before" + Environment.NewLine + oldText + Environment.NewLine + "after";
        File.WriteAllText(path, original);
        var executor = CreateExecutor(workspace);
        string? approvalDetails = null;

        var result = await executor.ExecuteAsync("replace_text_in_file",
            new JsonObject { ["path"] = "notes.txt", ["old_text"] = oldText, ["new_text"] = newText }.ToJsonString(),
            (_, details, _) => { approvalDetails = details; return Task.FromResult(false); },
            _ => { }, CancellationToken.None);

        var details = approvalDetails ?? throw new AssertFailedException("No approval preview was shown.");
        StringAssert.Contains(details, "Proposed replacement preview:");
        StringAssert.Contains(details, "--- Removed (max 1800 characters) ---");
        StringAssert.Contains(details, "--- Added (max 1800 characters) ---");
        StringAssert.Contains(details, oldText);
        StringAssert.Contains(details, newText);
        StringAssert.Contains(result, "declined");
        Assert.AreEqual(original, File.ReadAllText(path));
        Assert.IsFalse(Directory.Exists(System.IO.Path.Combine(workspace, ".layal-backups")));
    }

    [TestMethod]
    public async Task DeclinedOversizedReplacementPreviewBoundsBothSidesAndDoesNotCreateBackup()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var path = System.IO.Path.Combine(workspace, "notes.txt");
        var oldText = new string('o', 2_000);
        var newText = new string('n', 2_200);
        File.WriteAllText(path, oldText);
        var executor = CreateExecutor(workspace);
        string? approvalDetails = null;

        var result = await executor.ExecuteAsync("replace_text_in_file",
            new JsonObject { ["path"] = "notes.txt", ["old_text"] = oldText, ["new_text"] = newText }.ToJsonString(),
            (_, details, _) => { approvalDetails = details; return Task.FromResult(false); },
            _ => { }, CancellationToken.None);

        var details = approvalDetails ?? throw new AssertFailedException("No approval preview was shown.");
        StringAssert.Contains(details, "--- Removed (max 1800 characters) ---");
        StringAssert.Contains(details, "--- Added (max 1800 characters) ---");
        Assert.AreEqual(2, details.Split("…[preview truncated; remaining content omitted]", StringSplitOptions.None).Length - 1);
        Assert.IsFalse(details.Contains(oldText, StringComparison.Ordinal));
        Assert.IsFalse(details.Contains(newText, StringComparison.Ordinal));
        StringAssert.Contains(result, "declined");
        Assert.AreEqual(oldText, File.ReadAllText(path));
        Assert.IsFalse(Directory.Exists(System.IO.Path.Combine(workspace, ".layal-backups")));
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

    [TestMethod]
    public async Task ReadFileRejectsTargetReplacedBySymbolicLinkDuringApproval()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        var outside = System.IO.Path.Combine(temp.Path, "outside.txt");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(System.IO.Path.Combine(workspace, "notes.txt"), "approved preview");
        File.WriteAllText(outside, "outside secret");
        var executor = CreateExecutor(workspace);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            "read_text_file", "{\"path\":\"notes.txt\"}",
            (_, _, _) =>
            {
                File.Delete(System.IO.Path.Combine(workspace, "notes.txt"));
                File.CreateSymbolicLink(System.IO.Path.Combine(workspace, "notes.txt"), outside);
                return Task.FromResult(true);
            }, _ => { }, CancellationToken.None));

        Assert.AreEqual("outside secret", File.ReadAllText(outside));
    }

    [TestMethod]
    public async Task FileCreationRejectsParentReplacedBySymbolicLinkDuringApproval()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        var originalParent = System.IO.Path.Combine(workspace, "sub");
        var outside = System.IO.Path.Combine(temp.Path, "outside");
        Directory.CreateDirectory(originalParent);
        Directory.CreateDirectory(outside);
        var executor = CreateExecutor(workspace);
        var destinationOutside = System.IO.Path.Combine(outside, "new-note.txt");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            "create_text_file", "{\"path\":\"sub/new-note.txt\",\"content\":\"must stay inside workspace\"}",
            (_, _, _) =>
            {
                Directory.Move(originalParent, System.IO.Path.Combine(workspace, "sub-original"));
                Directory.CreateSymbolicLink(originalParent, outside);
                return Task.FromResult(true);
            }, _ => { }, CancellationToken.None));

        Assert.IsFalse(File.Exists(destinationOutside), "No file may be created outside the approved workspace.");
    }

    [TestMethod]
    public async Task ReplaceTextRejectsTargetReplacedBySymbolicLinkDuringApproval()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        var outside = System.IO.Path.Combine(temp.Path, "outside.txt");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(System.IO.Path.Combine(workspace, "notes.txt"), "replace this phrase");
        File.WriteAllText(outside, "outside secret");
        var executor = CreateExecutor(workspace);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            "replace_text_in_file",
            "{\"path\":\"notes.txt\",\"old_text\":\"replace this phrase\",\"new_text\":\"changed\"}",
            (_, _, _) =>
            {
                File.Delete(System.IO.Path.Combine(workspace, "notes.txt"));
                File.CreateSymbolicLink(System.IO.Path.Combine(workspace, "notes.txt"), outside);
                return Task.FromResult(true);
            }, _ => { }, CancellationToken.None));

        Assert.AreEqual("outside secret", File.ReadAllText(outside), "The outside target must remain unchanged.");
    }

    private static AgentToolExecutor CreateExecutor(string root, ApiConnectionService? apiConnections = null) =>
        new(root, new MemoryService(System.IO.Path.Combine(root, "memory.dpapi")), apiConnections);
}
