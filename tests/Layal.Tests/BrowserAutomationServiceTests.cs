using System.IO;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class BrowserAutomationServiceTests
{
    [TestMethod]
    public void HttpUrlValidationAllowsOnlyAbsoluteHttpAndHttpsWithoutEmbeddedCredentials()
    {
        Assert.AreEqual("https://example.com/path?q=1", BrowserAutomationService.ValidateHttpUrl(" https://example.com/path?q=1 ").AbsoluteUri);
        Assert.AreEqual("http://127.0.0.1:8080/health", BrowserAutomationService.ValidateHttpUrl("http://127.0.0.1:8080/health").AbsoluteUri);

        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateHttpUrl("javascript:alert(1)"));
        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateHttpUrl("file:///C:/private.txt"));
        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateHttpUrl("https://user:secret@example.com/"));
        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateHttpUrl("not a url"));
    }

    [TestMethod]
    public void BrowserDownloadValidationAllowsOnlySameOriginHttpsDocumentsAndImages()
    {
        var page = "https://docs.example.com/articles/guide";
        var valid = SafeBrowserDownloadService.ValidateDownloadTarget(
            page,
            "https://docs.example.com/files/guide.pdf?version=2");
        Assert.AreEqual("https", valid.Scheme);
        Assert.AreEqual("docs.example.com", valid.Host);
        Assert.AreEqual(".pdf", Path.GetExtension(valid.AbsolutePath));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SafeBrowserDownloadService.ValidateDownloadTarget(page, "https://cdn.example.com/guide.pdf"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SafeBrowserDownloadService.ValidateDownloadTarget(page, "http://docs.example.com/guide.pdf"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SafeBrowserDownloadService.ValidateDownloadTarget(page, "https://docs.example.com/setup.exe"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SafeBrowserDownloadService.ValidateDownloadTarget(page, "https://user:pass@docs.example.com/guide.pdf"));
    }

    [TestMethod]
    public void BrowserDownloadPublicIpFilterRejectsLocalPrivateAndSpecialRanges()
    {
        foreach (var blocked in new[]
        {
            "127.0.0.1",
            "10.1.2.3",
            "172.16.0.1",
            "192.168.1.10",
            "169.254.10.20",
            "100.64.1.2",
            "198.18.0.1",
            "203.0.113.10",
            "::1",
            "fc00::1",
            "fe80::1",
            "2001:db8::1",
            "::ffff:192.168.1.1"
        })
        {
            Assert.IsFalse(
                SafeBrowserDownloadService.IsPublicAddress(System.Net.IPAddress.Parse(blocked)),
                $"{blocked} must be blocked.");
        }

        Assert.IsTrue(SafeBrowserDownloadService.IsPublicAddress(System.Net.IPAddress.Parse("8.8.8.8")));
        Assert.IsTrue(SafeBrowserDownloadService.IsPublicAddress(System.Net.IPAddress.Parse("2001:4860:4860::8888")));
    }

    [TestMethod]
    public void BrowserDownloadFilenameSanitizationRemovesTraversalAndReservedDeviceNames()
    {
        Assert.AreEqual("guide.pdf", SafeBrowserDownloadService.SanitizeFileName("../../guide.pdf", ".pdf"));
        Assert.AreEqual("download.txt", SafeBrowserDownloadService.SanitizeFileName(@"C:\temp\CON.txt", ".txt"));
        Assert.AreEqual("download.pdf", SafeBrowserDownloadService.SanitizeFileName("payload.exe", ".pdf"));
    }

    [TestMethod]
    public void CssSelectorValidationRejectsEmptyControlCharactersAndExcessiveLength()
    {
        Assert.AreEqual("form#login input[name=email]", BrowserAutomationService.ValidateCssSelector(" form#login input[name=email] "));
        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateCssSelector(""));
        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateCssSelector("button\n#danger"));
        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateCssSelector(new string('a', 501)));
    }

    [TestMethod]
    public void BrowserUploadValidationAcceptsOnlyHtmlFileInputs()
    {
        BrowserAutomationService.ValidateBrowserUploadTarget("input", "file");
        BrowserAutomationService.ValidateBrowserUploadTarget("INPUT", "FILE");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserUploadTarget("input", "text"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserUploadTarget("input", "password"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserUploadTarget("button", "file"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserUploadTarget("div", "file"));
    }

    [TestMethod]
    public void BrowserControlRejectsAmbiguousTabsInsteadOfChoosingOneByListOrder()
    {
        BrowserAutomationService.ValidateSinglePageSession(1);
        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateSinglePageSession(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateSinglePageSession(2));
        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateSinglePageSession(5));
    }


    [TestMethod]
    public void BrowserNavigationSummaryContainsOnlyBoundedPageIdentityAndRequiresSeparateReadApproval()
    {
        var identity = new BrowserPageIdentity(
            "page-target",
            "https://example.com/account",
            "Example account page\\r\\nUntrusted extra line",
            "complete");

        var summary = identity.ToNavigationSummary();

        StringAssert.Contains(summary, "https://example.com/account");
        StringAssert.Contains(summary, "Example account page Untrusted extra line");
        StringAssert.Contains(summary, "Page content was not read or shared");
        StringAssert.Contains(summary, "browser_read_page");
        Assert.IsFalse(summary.Contains("visibleText", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(summary.Contains("page body", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void BrowserUploadRespectsTheFileInputAcceptAttribute()
    {
        BrowserAutomationService.ValidateBrowserUploadAccept("", ".pdf");
        BrowserAutomationService.ValidateBrowserUploadAccept(".pdf,.docx", ".pdf");
        BrowserAutomationService.ValidateBrowserUploadAccept("application/pdf", ".pdf");
        BrowserAutomationService.ValidateBrowserUploadAccept("image/*", ".webp");
        BrowserAutomationService.ValidateBrowserUploadAccept("text/*", ".csv");
        BrowserAutomationService.ValidateBrowserUploadAccept("application/*", ".xlsx");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserUploadAccept("image/*", ".pdf"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserUploadAccept(".png,.jpg", ".docx"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserUploadAccept("application/pdf", ".exe"));
    }

    [TestMethod]
    public async Task DeclinedNavigationDoesNotLaunchOrCreateBrowserProfile()
    {
        using var temp = new TestTempDirectory();
        var dataRoot = Path.Combine(temp.Path, "app-data");
        var browser = new BrowserAutomationService(dataRoot);
        var executor = new AgentToolExecutor(
            Path.Combine(temp.Path, "workspace"),
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")),
            apiConnections: null,
            browser: browser);
        var approvalCount = 0;

        var result = await executor.ExecuteAsync(
            "browser_navigate",
            "{\"url\":\"https://example.com/\"}",
            (title, preview, token) =>
            {
                approvalCount++;
                StringAssert.Contains(title, "browser");
                StringAssert.Contains(preview, "https://example.com/");
                return Task.FromResult(false);
            },
            _ => { },
            CancellationToken.None);

        Assert.AreEqual(1, approvalCount);
        StringAssert.Contains(result, "declined");
        Assert.IsFalse(Directory.Exists(Path.Combine(dataRoot, "BrowserProfile")));
    }

    [TestMethod]
    public async Task DeclinedPageDisclosureDoesNotLaunchOrReadBrowser()
    {
        using var temp = new TestTempDirectory();
        var dataRoot = Path.Combine(temp.Path, "app-data");
        var browser = new BrowserAutomationService(dataRoot);
        var executor = new AgentToolExecutor(
            Path.Combine(temp.Path, "workspace"),
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")),
            apiConnections: null,
            browser: browser);

        var result = await executor.ExecuteAsync(
            "browser_read_page",
            "{}",
            (_, _, _) => Task.FromResult(false),
            _ => { },
            CancellationToken.None);

        StringAssert.Contains(result, "declined");
        Assert.IsFalse(Directory.Exists(Path.Combine(dataRoot, "BrowserProfile")));
    }

    [TestMethod]
    public void BingSearchUrlIsHttpsBoundedNormalizedAndEncoded()
    {
        var url = BrowserAutomationService.BuildBingSearchUri("  live   web search بالعربية  ");

        Assert.AreEqual("https", url.Scheme);
        Assert.AreEqual("www.bing.com", url.Host);
        Assert.AreEqual("/search", url.AbsolutePath);
        Assert.AreEqual("live web search بالعربية",
            Uri.UnescapeDataString(url.Query["?q=".Length..]));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.BuildBingSearchUri("ab"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.BuildBingSearchUri(new string('x', 501)));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.BuildBingSearchUri("web\nsearch"));
    }

    [TestMethod]
    public void BingDomainValidationRejectsLookalikesCredentialsAndPlainHttp()
    {
        Assert.IsTrue(BrowserAutomationService.IsBingDomain(new Uri("https://www.bing.com/search?q=test")));
        Assert.IsTrue(BrowserAutomationService.IsBingDomain(new Uri("https://cn.bing.com/search?q=test")));
        Assert.IsFalse(BrowserAutomationService.IsBingDomain(new Uri("http://www.bing.com/search?q=test")));
        Assert.IsFalse(BrowserAutomationService.IsBingDomain(new Uri("https://bing.com.example.org/search?q=test")));
        Assert.IsFalse(BrowserAutomationService.IsBingDomain(new Uri("https://user@bing.com/search?q=test")));
        Assert.IsFalse(BrowserAutomationService.IsBingDomain(new Uri("https://example.org/search?q=test")));
    }

    [TestMethod]
    public void BingSearchPageClassificationDistinguishesResultsNoResultsChallengesAndRedirects()
    {
        Assert.AreEqual("results_available", BrowserAutomationService.ClassifyBingSearchPage(
            "https://www.bing.com/search?q=claim", "Bing", "Result one\nOfficial source"));
        Assert.AreEqual("no_results", BrowserAutomationService.ClassifyBingSearchPage(
            "https://www.bing.com/search?q=claim", "Bing", "There are no results for this query."));
        Assert.AreEqual("blocked_or_challenge", BrowserAutomationService.ClassifyBingSearchPage(
            "https://www.bing.com/sorry/index", "Bing", "We detected unusual traffic. Verify you are not a robot."));
        Assert.AreEqual("consent_required", BrowserAutomationService.ClassifyBingSearchPage(
            "https://www.bing.com/", "Bing", "Before you continue, choose your privacy settings."));
        Assert.AreEqual("empty_page", BrowserAutomationService.ClassifyBingSearchPage(
            "https://www.bing.com/search?q=claim", "Bing", "  "));
        Assert.AreEqual("unexpected_redirect", BrowserAutomationService.ClassifyBingSearchPage(
            "https://attacker.example/search", "External page", "Private page contents"));
    }

    [TestMethod]
    public async Task InvalidWebSearchQueryIsRejectedBeforeConsentOrBrowserStartup()
    {
        using var temp = new TestTempDirectory();
        var dataRoot = Path.Combine(temp.Path, "app-data");
        var executor = new AgentToolExecutor(
            Path.Combine(temp.Path, "workspace"),
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")),
            apiConnections: null,
            browser: new BrowserAutomationService(dataRoot));
        var approvalCount = 0;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            "web_search",
            """{"query":"ab"}""",
            (_, _, _) =>
            {
                approvalCount++;
                return Task.FromResult(false);
            },
            _ => { },
            CancellationToken.None));

        Assert.AreEqual(0, approvalCount);
        Assert.IsFalse(Directory.Exists(Path.Combine(dataRoot, "BrowserProfile")));
    }

    [TestMethod]
    public async Task DeclinedWebSearchDoesNotLaunchOrCreateBrowserProfile()
    {
        using var temp = new TestTempDirectory();
        var dataRoot = Path.Combine(temp.Path, "app-data");
        var executor = new AgentToolExecutor(
            Path.Combine(temp.Path, "workspace"),
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")),
            apiConnections: null,
            browser: new BrowserAutomationService(dataRoot));
        var approvalCount = 0;

        var result = await executor.ExecuteAsync(
            "web_search",
            "{\"query\":\"how to validate an engineering claim\"}",
            (title, preview, token) =>
            {
                approvalCount++;
                StringAssert.Contains(title, "Search");
                StringAssert.Contains(preview, "bing.com/search");
                StringAssert.Contains(preview, "how to validate an engineering claim");
                return Task.FromResult(false);
            },
            _ => { },
            CancellationToken.None);

        Assert.AreEqual(1, approvalCount);
        StringAssert.Contains(result, "declined");
        Assert.IsFalse(Directory.Exists(Path.Combine(dataRoot, "BrowserProfile")),
            "Declining search must not launch Edge or create its isolated profile.");
    }

    [TestMethod]
    public async Task DeclinedFactCheckDoesNotStartTheSecondQueryOrLaunchBrowser()
    {
        using var temp = new TestTempDirectory();
        var dataRoot = Path.Combine(temp.Path, "app-data");
        var executor = new AgentToolExecutor(
            Path.Combine(temp.Path, "workspace"),
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")),
            apiConnections: null,
            browser: new BrowserAutomationService(dataRoot));
        var approvalCount = 0;

        var result = await executor.ExecuteAsync(
            "fact_check_claim",
            "{\"claim\":\"The release was published in 2025\"}",
            (title, preview, token) =>
            {
                approvalCount++;
                StringAssert.Contains(title, "Fact-check: search the claim");
                StringAssert.Contains(preview, "The release was published in 2025");
                return Task.FromResult(false);
            },
            _ => { },
            CancellationToken.None);

        Assert.AreEqual(1, approvalCount, "A declined first query must not trigger the contrary-evidence query.");
        StringAssert.Contains(result, "declined");
        Assert.IsFalse(Directory.Exists(Path.Combine(dataRoot, "BrowserProfile")),
            "Declining a fact-check query must not launch Edge or create its isolated profile.");
    }

    [TestMethod]
    public async Task InvalidFactCheckClaimIsRejectedBeforeApprovalOrBrowserStartup()
    {
        using var temp = new TestTempDirectory();
        var dataRoot = Path.Combine(temp.Path, "app-data");
        var executor = new AgentToolExecutor(
            Path.Combine(temp.Path, "workspace"),
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")),
            apiConnections: null,
            browser: new BrowserAutomationService(dataRoot));
        var approvalCount = 0;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            "fact_check_claim",
            "{\"claim\":\"tiny\"}",
            (_, _, _) =>
            {
                approvalCount++;
                return Task.FromResult(false);
            },
            _ => { },
            CancellationToken.None));

        Assert.AreEqual(0, approvalCount, "Invalid claims must be rejected before requesting consent.");
        Assert.IsFalse(Directory.Exists(Path.Combine(dataRoot, "BrowserProfile")));
    }

    [TestMethod]
    public void BrowserClickValidationBlocksSubmitResetAndFormAssociatedTargets()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserClickTarget("button", "submit", "", false));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserClickTarget("button", "reset", "", false));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserClickTarget("input", "submit", "", false));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserClickTarget("input", "reset", "", false));

        // Untyped buttons inside forms submit by default. Other form-contained controls may also
        // execute page handlers that submit a form or mutate remote data.
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserClickTarget("button", "", "", true));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserClickTarget("button", "button", "", true));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserClickTarget("a", "", "", true));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserClickTarget("div", "", "button", true));
    }

    [TestMethod]
    public void BrowserClickValidationAllowsStandaloneNonSubmittingTargets()
    {
        BrowserAutomationService.ValidateBrowserClickTarget("a", "", "", false);
        BrowserAutomationService.ValidateBrowserClickTarget("button", "button", "", false);
        BrowserAutomationService.ValidateBrowserClickTarget("button", "", "", false);
        BrowserAutomationService.ValidateBrowserClickTarget("input", "button", "", false);
        BrowserAutomationService.ValidateBrowserClickTarget("summary", "", "", false);
        BrowserAutomationService.ValidateBrowserClickTarget("div", "", "button", false);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BrowserAutomationService.ValidateBrowserClickTarget("input", "text", "", false));
    }

    [TestMethod]
    public void BrowserToolsAreRegisteredWithTypedSchemas()
    {
        using var temp = new TestTempDirectory();
        var executor = new AgentToolExecutor(
            Path.Combine(temp.Path, "workspace"),
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")),
            apiConnections: null,
            browser: new BrowserAutomationService(Path.Combine(temp.Path, "app-data")));

        var definitions = executor.GetToolDefinitions();
        var names = definitions.Select(x => x?["function"]?["name"]?.GetValue<string>()).ToArray();

        foreach (var expected in new[] { "browser_navigate", "browser_read_page", "browser_click", "browser_fill", "browser_download", "browser_upload_file" })
            Assert.IsTrue(names.Contains(expected), $"Missing browser tool {expected}.");
        foreach (var definition in definitions)
            Assert.IsNotNull(definition?["function"]?["parameters"] as System.Text.Json.Nodes.JsonObject);
    }

    [TestMethod]
    public void FactCheckSourceUrlRequiresPublicHttpsOnTheStandardPort()
    {
        Assert.AreEqual("https://news.example.org/report", 
            BrowserAutomationService.ValidateFactCheckSourceUrl("https://news.example.org/report").AbsoluteUri);

        foreach (var blocked in new[]
        {
            "http://news.example.org/report",
            "https://news.example.org:8443/report",
            "https://127.0.0.1/private",
            "https://localhost/private",
            "https://printer.local/status",
            "https://metadata.internal/source",
            "https://claim.example.test/article",
            "https://user:password@news.example.org/report"
        })
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => BrowserAutomationService.ValidateFactCheckSourceUrl(blocked),
                $"'{blocked}' must not be accepted as a direct fact-check source.");
        }
    }

    [TestMethod]
    public void FactCheckPublisherGroupingTreatsSubdomainsAsOnePublisherCandidate()
    {
        Assert.AreEqual("example.com", BrowserAutomationService.GetRegistrableDomain("www.example.com"));
        Assert.AreEqual("example.com", BrowserAutomationService.GetRegistrableDomain("opinion.example.com"));
        Assert.IsTrue(BrowserAutomationService.AreSameRegistrableDomain("news.example.com", "opinion.example.com"));
        Assert.IsFalse(BrowserAutomationService.AreSameRegistrableDomain("example.com", "example.net"));

        Assert.AreEqual("bbc.co.uk", BrowserAutomationService.GetRegistrableDomain("www.bbc.co.uk"));
        Assert.AreEqual("bbc.co.uk", BrowserAutomationService.GetRegistrableDomain("news.bbc.co.uk"));
        Assert.IsTrue(BrowserAutomationService.AreSameRegistrableDomain("news.bbc.co.uk", "www.bbc.co.uk"));

        Assert.AreEqual("portal.gov.eg", BrowserAutomationService.GetRegistrableDomain("portal.gov.eg"));
        Assert.AreEqual("portal.gov.eg", BrowserAutomationService.GetRegistrableDomain("research.portal.gov.eg"));
    }

}
