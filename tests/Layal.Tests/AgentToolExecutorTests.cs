using System.Text.Json.Nodes;
using System.Threading;
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
        Assert.IsNotNull(definitions.FirstOrDefault(n => n?["function"]?["name"]?.GetValue<string>() == "web_search"));
        Assert.IsNotNull(definitions.FirstOrDefault(n => n?["function"]?["name"]?.GetValue<string>() == "fact_check_claim"));
        Assert.IsNotNull(definitions.FirstOrDefault(n => n?["function"]?["name"]?.GetValue<string>() == "browser_download"));
        Assert.IsNotNull(definitions.FirstOrDefault(n => n?["function"]?["name"]?.GetValue<string>() == "browser_upload_file"));

        Assert.AreEqual(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.IsFalse(names.Any(string.IsNullOrWhiteSpace));
        foreach (var definition in definitions)
            Assert.IsNotNull(definition?["function"]?["parameters"] as JsonObject);
    }

    [TestMethod]
    public async Task InvalidBrowserUploadPathIsRejectedBeforeConsentOrBrowserStartup()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        var dataRoot = System.IO.Path.Combine(temp.Path, "app-data");
        var executor = new AgentToolExecutor(
            workspace,
            new MemoryService(System.IO.Path.Combine(temp.Path, "memory.dpapi")),
            apiConnections: null,
            browser: new BrowserAutomationService(dataRoot));
        var approvalCalled = false;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            "browser_upload_file",
            """{"selector":"input[type=file]","path":"../outside.pdf"}""",
            (_, _, _) =>
            {
                approvalCalled = true;
                return Task.FromResult(true);
            },
            _ => { },
            CancellationToken.None));

        Assert.IsFalse(approvalCalled, "An invalid local path must be rejected before asking to upload it.");
        Assert.IsFalse(Directory.Exists(System.IO.Path.Combine(dataRoot, "BrowserProfile")),
            "Rejecting an invalid upload path must not start Edge or create its profile.");
    }

    [TestMethod]
    public async Task UnsupportedBrowserUploadTypeIsRejectedBeforeConsent()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(System.IO.Path.Combine(workspace, "payload.ps1"), "Write-Host unsafe");
        var executor = new AgentToolExecutor(
            workspace,
            new MemoryService(System.IO.Path.Combine(temp.Path, "memory.dpapi")),
            apiConnections: null,
            browser: new BrowserAutomationService(System.IO.Path.Combine(temp.Path, "app-data")));
        var approvalCalled = false;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            "browser_upload_file",
            """{"selector":"input[type=file]","path":"payload.ps1"}""",
            (_, _, _) =>
            {
                approvalCalled = true;
                return Task.FromResult(true);
            },
            _ => { },
            CancellationToken.None));

        Assert.IsFalse(approvalCalled);
    }

    [TestMethod]
    public async Task BrowserNavigationCancellationAfterApprovalDoesNotStartEdgeProfile()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        var dataRoot = Path.Combine(temp.Path, "app-data");
        using var cancellation = new CancellationTokenSource();
        var executor = new AgentToolExecutor(
            workspace,
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")),
            browser: new BrowserAutomationService(dataRoot));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
            "browser_navigate",
            """{"url":"https://example.com/"}""",
            (_, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            },
            _ => { },
            cancellation.Token));

        Assert.IsFalse(Directory.Exists(Path.Combine(dataRoot, "BrowserProfile")),
            "A cancelled browser action must not create or launch the dedicated Edge profile.");
    }

    [TestMethod]
    public async Task DotnetBuildCancellationTerminatesTheExternalProcessTree()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The process cancellation integration test requires Windows and dotnet.");

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var startedMarker = Path.Combine(workspace, "started.marker");
        var finishedMarker = Path.Combine(workspace, "finished.marker");
        File.WriteAllText(Path.Combine(workspace, "long-running.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <NuGetAudit>false</NuGetAudit>
                <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
              </PropertyGroup>
              <Target Name="WaitForAgentCancellation" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="started.marker" Lines="started" Overwrite="true" />
                <Exec Command="ping -n 40 127.0.0.1 &gt; nul" />
                <WriteLinesToFile File="finished.marker" Lines="finished" Overwrite="true" />
              </Target>
            </Project>
            """);

        using var cancellation = new CancellationTokenSource();
        var executor = new AgentToolExecutor(
            workspace,
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")),
            processTimeout: TimeSpan.FromSeconds(30));
        var operation = executor.ExecuteAsync(
            "run_dotnet_build",
            """{"project_file":"long-running.csproj","configuration":"Debug"}""",
            (_, _, _) => Task.FromResult(true),
            _ => { },
            cancellation.Token);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!File.Exists(startedMarker) && !operation.IsCompleted && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);

        Assert.IsTrue(File.Exists(startedMarker), "The test project must enter its long-running build target before cancellation.");
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await operation);
        await Task.Delay(250);
        Assert.IsFalse(File.Exists(finishedMarker),
            "The dotnet process tree must be terminated before the long-running child target can finish.");
    }

    [TestMethod]
    public void FactCheckSearchExtractsDirectHttpsSourcesAndRejectsRedirectorsAndLocalTargets()
    {
        var searchResult = """
            WEB SEARCH RESULTS
            Query: a factual claim
            Search URL: https://www.bing.com/search?q=a%20factual%20claim
            {"title":"Bing","url":"https://www.bing.com/search?q=a%20factual%20claim","links":[
              {"text":"Official source","href":"https://data.example.org/report?id=12#section"},
              {"text":"Bing tracking","href":"https://www.bing.com/ck/a?u=https%3A%2F%2Fbad.example"},
              {"text":"Plain HTTP source","href":"http://source.example.org/report"},
              {"text":"Local target","href":"https://127.0.0.1/private"},
              {"text":"Embedded credentials","href":"https://user:pass@source.example.org/report"},
              {"text":"Second source","href":"https://journal.example.org/study"},
              {"text":"Duplicate source","href":"https://data.example.org/report?id=12#different-section"}
            ]}
            """;

        var candidates = AgentToolExecutor.ExtractDirectSourceCandidates(searchResult);

        Assert.AreEqual(2, candidates.Count);
        Assert.AreEqual("data.example.org", candidates[0].Host);
        Assert.AreEqual("Official source", candidates[0].Title);
        Assert.IsTrue(candidates[0].Url.StartsWith("https://data.example.org/report?id=12", StringComparison.Ordinal));
        Assert.AreEqual("journal.example.org", candidates[1].Host);
    }

    [TestMethod]
    public void FactCheckSourceSummaryIncludesPageDeclaredAuthorAndSeparateUnverifiedDates()
    {
        var page = """
            {"title":"Quarterly report","url":"https://reports.example.org/q4","author":"Research Desk","publishedDate":"2026-10-30","modifiedDate":"2026-11-01","visibleTimeDate":"2026-10-28","siteName":"Example Reports","description":"A quarterly results summary.","capturedAtUtc":"2026-11-05T06:00:00.0000000Z","visibleText":"The report describes events during the quarter."}
            """;

        var summary = AgentToolExecutor.SummarizeDirectSourcePage(page);
        Assert.IsNotNull(summary);
        StringAssert.Contains(summary!, "Author (page-declared, unverified): Research Desk");
        StringAssert.Contains(summary!, "Publication date (page-declared raw value; unverified): 2026-10-30 (parseable raw value; still unverified)");
        StringAssert.Contains(summary!, "Modification date (page-declared raw value; unverified): 2026-11-01 (parseable raw value; still unverified)");
        StringAssert.Contains(summary!, "First visible <time datetime> value (meaning not inferred): 2026-10-28 (parseable raw value; still unverified)");
        StringAssert.Contains(summary!, "Captured by Layal at UTC (capture time, not source publication/event date): 2026-11-05T06:00:00.0000000Z");
        StringAssert.Contains(summary!, "Visible page text (untrusted)");
    }

    [TestMethod]
    public void FactCheckSourceSummaryKeepsMultipleVisibleTimeContextsAndFlagsInvalidDates()
    {
        var page = """
            {
              "title":"Event report",
              "url":"https://reports.example.org/event",
              "author":42,
              "publishedDate":"not-a-date",
              "modifiedDate":"2026-02-31",
              "visibleTimeDate":"2026-02-01",
              "visibleTimes":[
                {"datetime":"2026-02-01","context":"Event took place on February 1"},
                {"datetime":"2026-02-05","context":"Article published on February 5"},
                {"datetime":"2026-03-01","context":"Last updated March 1"}
              ],
              "visibleText":"The visible article discusses the event."
            }
            """;

        var summary = AgentToolExecutor.SummarizeDirectSourcePage(page);
        Assert.IsNotNull(summary);
        StringAssert.Contains(summary!, "Author (page-declared, unverified): not provided");
        StringAssert.Contains(summary!, "Publication date (page-declared raw value; unverified): not-a-date (unparsed/invalid raw value; unverified)");
        StringAssert.Contains(summary!, "Modification date (page-declared raw value; unverified): 2026-02-31 (unparsed/invalid raw value; unverified)");
        StringAssert.Contains(summary!, "Event took place on February 1");
        StringAssert.Contains(summary!, "Article published on February 5");
        StringAssert.Contains(summary!, "Last updated March 1");
        StringAssert.Contains(summary!, "A visible <time> value is not automatically an event date or a publication date.");
    }

    [TestMethod]
    public void FactCheckSourceSummaryHandlesMissingAndWrongTypedMetadataWithoutInventingValues()
    {
        var page = """{"title":17,"url":"https://reports.example.org/no-metadata","author":{"name":"Injected"},"publishedDate":2026,"modifiedDate":false,"visibleTimes":"not-an-array","visibleText":"Text without source metadata."}""";
        var summary = AgentToolExecutor.SummarizeDirectSourcePage(page);
        Assert.IsNotNull(summary);
        StringAssert.Contains(summary!, "Author (page-declared, unverified): not provided");
        StringAssert.Contains(summary!, "Publication date (page-declared raw value; unverified): not provided");
        StringAssert.Contains(summary!, "Modification date (page-declared raw value; unverified): not provided");
        StringAssert.Contains(summary!, "All visible <time datetime> values and nearby context (none means not provided):\nnot provided");
        Assert.IsNull(AgentToolExecutor.SummarizeDirectSourcePage("[]"));
    }

    [TestMethod]
    public void FactCheckSourceCandidateExtractionRejectsReservedHostsAndNonstandardPorts()
    {
        var result = """
            WEB SEARCH RESULTS
            Search URL: https://www.bing.com/search?q=claim
            {"links":[
              {"text":"Eligible","href":"https://news.example.org/report"},
              {"text":"Alternate port","href":"https://news.example.org:8443/report"},
              {"text":"Test domain","href":"https://report.example.test/story"},
              {"text":"Local host","href":"https://localhost/private"}
            ]}
            """;

        var candidates = AgentToolExecutor.ExtractDirectSourceCandidates(result);
        Assert.AreEqual(1, candidates.Count);
        Assert.AreEqual("news.example.org", candidates[0].Host);
    }

    [TestMethod]
    public void FactCheckSourceSummaryRejectsNonHttpsFinalUrl()
    {
        var page = """{"title":"Report","url":"http://reports.example.org/" ,"visibleText":"text"}""";
        var summary = AgentToolExecutor.SummarizeDirectSourcePage(page);
        Assert.AreEqual("The final page URL was not an eligible public HTTPS address on the standard port; source content omitted.", summary);
    }

    [TestMethod]
    public void FactCheckSearchWithoutPageJsonReturnsNoSources()
    {
        var candidates = AgentToolExecutor.ExtractDirectSourceCandidates(
            "WEB SEARCH RESULTS\nQuery: claim\nSearch URL: https://www.bing.com/search?q=claim\nAccess denied");
        Assert.AreEqual(0, candidates.Count);
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
    public async Task CancellingAfterMemoryDisclosureApprovalPreventsToolOutput()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var memory = new MemoryService(System.IO.Path.Combine(temp.Path, "memory.dpapi"));
        _ = memory.Save("Private note", "Do not disclose this after cancellation.");
        var executor = new AgentToolExecutor(temp.Path, memory);
        using var cancellation = new CancellationTokenSource();
        var approvalCount = 0;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
            "search_memory",
            "{\"query\":\"Private note\"}",
            (_, _, _) =>
            {
                approvalCount++;
                cancellation.Cancel();
                return Task.FromResult(true);
            },
            _ => { },
            cancellation.Token));

        Assert.AreEqual(1, approvalCount);
    }

    [TestMethod]
    public async Task DotnetOperationRevalidatesProjectFileAfterApproval()
    {
        using var temp = new TestTempDirectory();
        var projectPath = System.IO.Path.Combine(temp.Path, "TrustedProject.csproj");
        File.WriteAllText(projectPath, "<Project />");
        var executor = CreateExecutor(temp.Path);

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => executor.ExecuteAsync(
            "run_dotnet_build",
            "{\"project_file\":\"TrustedProject.csproj\",\"configuration\":\"Debug\"}",
            (_, _, _) =>
            {
                File.Delete(projectPath);
                return Task.FromResult(true);
            },
            _ => { },
            CancellationToken.None));
        Assert.IsFalse(File.Exists(projectPath));
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
    public void WorkspaceArtifactLookupAcceptsOnlyExistingFilesInsideWorkspace()
    {
        using var temp = new TestTempDirectory();
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(System.IO.Path.Combine(workspace, "reports"));
        var artifact = System.IO.Path.Combine(workspace, "reports", "report.txt");
        var outside = System.IO.Path.Combine(temp.Path, "outside.txt");
        File.WriteAllText(artifact, "report");
        File.WriteAllText(outside, "outside");

        var executor = CreateExecutor(workspace);

        Assert.IsTrue(executor.TryGetWorkspaceArtifactPath("reports/report.txt", out var normalized));
        Assert.AreEqual("reports/report.txt", normalized);
        Assert.IsFalse(executor.TryGetWorkspaceArtifactPath("../outside.txt", out _));
        Assert.IsFalse(executor.TryGetWorkspaceArtifactPath(outside, out _));
        Assert.IsFalse(executor.TryGetWorkspaceArtifactPath("reports/missing.txt", out _));
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
