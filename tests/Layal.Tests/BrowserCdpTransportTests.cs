using System;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class BrowserCdpTransportTests
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [TestMethod]
    public void PageWebSocketValidationAcceptsOnlyActivePortLoopbackPageEndpoints()
    {
        var activePort = 9222;
        var transport = CreateTransport(() => activePort);

        Assert.IsTrue(IsTrustedPageEndpoint(transport, "ws://127.0.0.1:9222/devtools/page/page-1"));
        Assert.IsTrue(IsTrustedPageEndpoint(transport, "ws://localhost:9222/devtools/page/page-2"));

        Assert.IsFalse(IsTrustedPageEndpoint(transport, "wss://127.0.0.1:9222/devtools/page/page-1"));
        Assert.IsFalse(IsTrustedPageEndpoint(transport, "http://127.0.0.1:9222/devtools/page/page-1"));
        Assert.IsFalse(IsTrustedPageEndpoint(transport, "ws://192.0.2.10:9222/devtools/page/page-1"));
        Assert.IsFalse(IsTrustedPageEndpoint(transport, "ws://127.0.0.1:9223/devtools/page/page-1"));
        Assert.IsFalse(IsTrustedPageEndpoint(transport, "ws://user@127.0.0.1:9222/devtools/page/page-1"));
        Assert.IsFalse(IsTrustedPageEndpoint(transport, "ws://127.0.0.1:9222/devtools/browser/session"));
        Assert.IsFalse(IsTrustedPageEndpoint(transport, "ws://127.0.0.1:9222/json/list"));
    }

    [TestMethod]
    public void PageWebSocketValidationTracksTheCurrentDebugPort()
    {
        var activePort = 9222;
        var transport = CreateTransport(() => activePort);

        Assert.IsTrue(IsTrustedPageEndpoint(transport, "ws://127.0.0.1:9222/devtools/page/page-1"));

        activePort = 9333;

        Assert.IsFalse(IsTrustedPageEndpoint(transport, "ws://127.0.0.1:9222/devtools/page/page-1"));
        Assert.IsTrue(IsTrustedPageEndpoint(transport, "ws://127.0.0.1:9333/devtools/page/page-2"));
    }

    [TestMethod]
    public async Task UploadPageConnectionRejectsNonLoopbackEndpointsBeforeConnecting()
    {
        var transport = CreateTransport(() => 9222);
        var targetType = typeof(BrowserAutomationService).Assembly.GetType("Layal.Services.CdpPageTarget", throwOnError: true)!;
        var target = Activator.CreateInstance(
            targetType,
            InstanceMembers,
            binder: null,
            args: new object[] { "page-1", "title", "https://example.com/", "ws://192.0.2.10:9222/devtools/page/page-1" },
            culture: null)!;

        var connect = transport.GetType().GetMethod("ConnectPageAsync", InstanceMembers)!;
        var task = (Task)connect.Invoke(transport, new object[] { target, CancellationToken.None })!;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await task);
    }

    [TestMethod]
    public async Task OrdinaryPageCommandRejectsUntrustedEndpointsBeforeConnecting()
    {
        var transport = CreateTransport(() => 9222);
        var targetType = typeof(BrowserAutomationService).Assembly.GetType("Layal.Services.CdpPageTarget", throwOnError: true)!;
        var target = Activator.CreateInstance(
            targetType,
            InstanceMembers,
            binder: null,
            args: new object[] { "page-1", "title", "https://example.com/", "http://192.0.2.10:9222/devtools/page/page-1" },
            culture: null)!;

        var send = transport.GetType().GetMethod("SendCommandAsync", InstanceMembers)!;
        var task = (Task)send.Invoke(transport, new object[] { target, "Runtime.evaluate", new JsonObject(), CancellationToken.None })!;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await task);
    }

    private static object CreateTransport(Func<int> getDebugPort)
    {
        var type = typeof(BrowserAutomationService).Assembly.GetType("Layal.Services.BrowserCdpTransport", throwOnError: true)!;
        return Activator.CreateInstance(
            type,
            InstanceMembers,
            binder: null,
            args: new object[] { getDebugPort },
            culture: null)!;
    }

    private static bool IsTrustedPageEndpoint(object transport, string value)
    {
        var method = transport.GetType().GetMethod("IsTrustedBrowserWebSocketUrl", InstanceMembers)!;
        return (bool)method.Invoke(transport, new object[] { value })!;
    }
}
