using System.Reflection;
using System.Threading;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class BrowserAutomationShutdownTests
{
    [TestMethod]
    public async Task ShutdownWaitsForTheBrowserOperationGateBeforeCleaningResources()
    {
        using var temp = new TestTempDirectory();
        var browser = new BrowserAutomationService(Path.Combine(temp.Path, "app-data"));
        var gateField = typeof(BrowserAutomationService).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(gateField, "Browser operations must remain serialized by a gate.");
        var gate = (SemaphoreSlim)gateField!.GetValue(browser)!;

        // Simulate an in-flight CDP operation holding the service gate without launching a real browser.
        await gate.WaitAsync();
        Task shutdown;
        try
        {
            shutdown = browser.ShutdownAsync();

            // Waiting for this rejection proves shutdown has set its terminal state before resource cleanup.
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
                browser.NavigateAsync("https://example.com/"));
            Assert.IsFalse(shutdown.IsCompleted, "Shutdown must not dispose resources while another operation owns the gate.");
        }
        finally
        {
            gate.Release();
        }

        await shutdown;
        await browser.ShutdownAsync(); // repeated shutdown is safe and idempotent
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "app-data", "BrowserProfile")),
            "The shutdown test must not create or launch a real browser profile.");
    }

    [TestMethod]
    public async Task ShutdownRejectsEveryPublicBrowserOperationWithoutLaunchingEdge()
    {
        using var temp = new TestTempDirectory();
        var browser = new BrowserAutomationService(Path.Combine(temp.Path, "app-data"));
        await browser.ShutdownAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => browser.NavigateAsync("https://example.com/"));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => browser.ReadCurrentPageAsync());
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            browser.PreviewElementAsync("button.ok", "click"));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            browser.ClickElementAsync(new BrowserElementPreview
            {
                Action = "click",
                PageTargetId = "page",
                Selector = "button.ok",
                Description = "test"
            }));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            browser.FillElementAsync(new BrowserElementPreview
            {
                Action = "fill",
                PageTargetId = "page",
                Selector = "input[name=q]",
                Description = "test"
            }, "value"));

        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "app-data", "BrowserProfile")));
    }
}
