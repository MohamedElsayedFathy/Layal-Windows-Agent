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
    public void CssSelectorValidationRejectsEmptyControlCharactersAndExcessiveLength()
    {
        Assert.AreEqual("form#login input[name=email]", BrowserAutomationService.ValidateCssSelector(" form#login input[name=email] "));
        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateCssSelector(""));
        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateCssSelector("button\n#danger"));
        Assert.ThrowsExactly<InvalidOperationException>(() => BrowserAutomationService.ValidateCssSelector(new string('a', 501)));
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

        foreach (var expected in new[] { "browser_navigate", "browser_read_page", "browser_click", "browser_fill" })
            Assert.IsTrue(names.Contains(expected), $"Missing browser tool {expected}.");
        foreach (var definition in definitions)
            Assert.IsNotNull(definition?["function"]?["parameters"] as System.Text.Json.Nodes.JsonObject);
    }
}
