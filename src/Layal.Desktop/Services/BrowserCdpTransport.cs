using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

/// <summary>
/// Owns bounded WebSocket command transport to a page endpoint on the active local Edge
/// DevTools port. Browser process lifecycle, approval prompts, and page-operation policy
/// remain in BrowserAutomationService.
/// </summary>
internal sealed class BrowserCdpTransport
{
    private const int MaxCdpMessageBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan CdpRequestTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<int> _getDebugPort;

    internal BrowserCdpTransport(Func<int> getDebugPort)
    {
        ArgumentNullException.ThrowIfNull(getDebugPort);
        _getDebugPort = getDebugPort;
    }

    internal bool IsTrustedBrowserWebSocketUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != _getDebugPort() ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var loopbackHost = uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
        return loopbackHost && uri.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal);
    }

    internal async Task<ClientWebSocket> ConnectPageAsync(CdpPageTarget page, CancellationToken token)
    {
        if (!IsTrustedBrowserWebSocketUrl(page.WebSocketUrl))
            throw new InvalidOperationException("The browser DevTools page endpoint is not an approved loopback endpoint.");

        var socket = new ClientWebSocket();
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectTimeout.CancelAfter(CdpRequestTimeout);
            await socket.ConnectAsync(new Uri(page.WebSocketUrl), connectTimeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal async Task<JsonObject> SendCommandAsync(CdpPageTarget page, string method, JsonObject parameters, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(CdpRequestTimeout);
        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri(page.WebSocketUrl), timeout.Token);
            var request = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method, @params = parameters });
            await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[8192];
            while (true)
            {
                using var message = new MemoryStream();
                while (true)
                {
                    var received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("The browser closed the DevTools connection.");
                    if (message.Length + received.Count > MaxCdpMessageBytes)
                        throw new InvalidOperationException("The browser DevTools response exceeded 2 MB.");
                    message.Write(buffer, 0, received.Count);
                    if (received.EndOfMessage) break;
                }

                using var doc = JsonDocument.Parse(message.ToArray());
                var root = doc.RootElement;
                // Chromium sends asynchronous protocol events on the same socket. Ignore them
                // until the response carrying this command's request id arrives.
                if (!root.TryGetProperty("id", out var id) || id.GetInt32() != 1)
                    continue;
                if (root.TryGetProperty("error", out var error))
                    throw new InvalidOperationException("Browser DevTools command failed: " + error.GetRawText());
                return JsonNode.Parse(root.GetRawText()) as JsonObject ?? new JsonObject();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"Browser DevTools request '{method}' timed out after {CdpRequestTimeout.TotalSeconds:0} seconds.");
        }
    }

    internal async Task<JsonObject> SendCommandOnSocketAsync(
        ClientWebSocket socket,
        int commandId,
        string method,
        JsonObject parameters,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(CdpRequestTimeout);
        try
        {
            var request = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = commandId,
                method,
                @params = parameters
            });
            await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[8192];
            while (true)
            {
                using var message = new MemoryStream();
                while (true)
                {
                    var received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("The browser closed the DevTools upload connection.");
                    if (message.Length + received.Count > MaxCdpMessageBytes)
                        throw new InvalidOperationException("The browser DevTools upload response exceeded 2 MB.");
                    message.Write(buffer, 0, received.Count);
                    if (received.EndOfMessage) break;
                }

                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var id) || id.GetInt32() != commandId)
                    continue;
                if (root.TryGetProperty("error", out var error))
                    throw new InvalidOperationException("Browser DevTools upload command failed: " + error.GetRawText());
                return JsonNode.Parse(root.GetRawText()) as JsonObject ?? new JsonObject();
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"Browser DevTools upload command '{method}' timed out after {CdpRequestTimeout.TotalSeconds:0} seconds.");
        }
    }
}
