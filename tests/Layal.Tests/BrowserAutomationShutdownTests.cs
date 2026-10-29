using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
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

        // Simulate a CDP operation in progress and another queued request without launching Edge.
        await gate.WaitAsync();
        Task shutdown;
        var queuedOperation = browser.ReadCurrentPageAsync();
        try
        {
            Assert.IsFalse(queuedOperation.IsCompleted, "The simulated request must be waiting for the gate.");
            shutdown = browser.ShutdownAsync();

            // A new call is rejected immediately, and an already-queued call is canceled.
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
                browser.NavigateAsync("https://example.com/"));
            var queuedOperationCancelled = false;
            try { await queuedOperation; }
            catch (OperationCanceledException) { queuedOperationCancelled = true; }
            Assert.IsTrue(queuedOperationCancelled, "Shutdown must cancel operations waiting for the browser gate.");
            Assert.IsFalse(shutdown.IsCompleted, "Shutdown must wait for the active operation to release the gate.");
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
    public async Task ShutdownSendsBrowserCloseForAnAlreadyAttachedDedicatedSession()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverTask = Task.Run(async () =>
        {
            var versionContext = await listener.GetContextAsync().WaitAsync(serverTimeout.Token);
            Assert.AreEqual("/json/version", versionContext.Request.Url?.AbsolutePath);
            var versionJson = JsonSerializer.Serialize(new
            {
                webSocketDebuggerUrl = $"ws://127.0.0.1:{port}/devtools/browser/test"
            });
            var versionBytes = Encoding.UTF8.GetBytes(versionJson);
            versionContext.Response.ContentType = "application/json";
            versionContext.Response.ContentLength64 = versionBytes.Length;
            await versionContext.Response.OutputStream.WriteAsync(versionBytes, serverTimeout.Token);
            versionContext.Response.Close();

            var websocketContext = await listener.GetContextAsync().WaitAsync(serverTimeout.Token);
            Assert.IsTrue(websocketContext.Request.IsWebSocketRequest);
            using var websocket = (await websocketContext.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[4096];
            var received = await websocket.ReceiveAsync(buffer.AsMemory(), serverTimeout.Token);
            Assert.AreEqual(WebSocketMessageType.Text, received.MessageType);
            return Encoding.UTF8.GetString(buffer, 0, received.Count);
        });

        using var temp = new TestTempDirectory();
        var browser = new BrowserAutomationService(Path.Combine(temp.Path, "app-data"));
        var portField = typeof(BrowserAutomationService).GetField("_debugPort", BindingFlags.Instance | BindingFlags.NonPublic);
        var attachedField = typeof(BrowserAutomationService).GetField("_attachedToExistingSession", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(portField);
        Assert.IsNotNull(attachedField);
        portField!.SetValue(browser, port);
        attachedField!.SetValue(browser, true);

        await browser.ShutdownAsync();
        var command = await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var commandDocument = JsonDocument.Parse(command);
        Assert.AreEqual("Browser.close", commandDocument.RootElement.GetProperty("method").GetString());
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
            browser.ClickElementAsync(new BrowserElementPreview(
                "click", "page", "https://example.com/", "button.ok", "fingerprint",
                "button", "button", "ok", "", "OK", "OK", "", false, false)));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            browser.FillElementAsync(new BrowserElementPreview(
                "fill", "page", "https://example.com/", "input[name=q]", "fingerprint",
                "input", "text", "", "q", "Search", "", "", false, false), "value"));

        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Path, "app-data", "BrowserProfile")));
    }
}
