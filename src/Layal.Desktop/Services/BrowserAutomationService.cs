using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Layal.Services;

/// <summary>
/// Controls a dedicated Microsoft Edge profile through its loopback-only Chromium DevTools Protocol.
/// It never attaches to the user's normal browser profile. Every navigation, page disclosure, click,
/// and text-entry operation must be gated by the calling agent's local approval callback.
/// </summary>
public sealed class BrowserAutomationService
{
    private const int MaxCdpMessageBytes = 2 * 1024 * 1024;
    private const int MaxPageTextChars = 20_000;
    private const int MaxElementTextChars = 160;
    private static readonly TimeSpan BrowserStartTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CdpRequestTimeout = TimeSpan.FromSeconds(10);
    private readonly string _profileDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private Process? _browserProcess;
    private int _debugPort;

    public BrowserAutomationService(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _profileDirectory = Path.Combine(Path.GetFullPath(dataRoot), "BrowserProfile");
    }

    public static Uri ValidateHttpUrl(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length is 0 or > 4096 ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Host.Length == 0)
            throw new InvalidOperationException("Browser navigation only supports absolute HTTP/HTTPS URLs without embedded credentials.");
        return uri;
    }

    public static string ValidateCssSelector(string value)
    {
        var selector = (value ?? string.Empty).Trim();
        if (selector.Length is 0 or > 500 || selector.Any(char.IsControl))
            throw new InvalidOperationException("Enter a CSS selector of 1–500 printable characters.");
        return selector;
    }

    public async Task<string> NavigateAsync(string url, CancellationToken token = default)
    {
        var targetUrl = ValidateHttpUrl(url);
        await _gate.WaitAsync(token);
        try
        {
            await EnsureBrowserStartedAsync(token);
            var page = await GetPageTargetAsync(token);
            var response = await SendCommandAsync(page, "Page.navigate", new JsonObject { ["url"] = targetUrl.AbsoluteUri }, token);
            var errorText = response["result"]?["errorText"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(errorText))
                throw new InvalidOperationException("Browser navigation failed: " + errorText);

            // Do not wait indefinitely for sites that stream or keep long-running connections open.
            for (var i = 0; i < 12; i++)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(250, token);
                try
                {
                    var state = await EvaluateAsync(page, "document.readyState", token);
                    if (state is "interactive" or "complete") break;
                }
                catch (Exception ex) when (ex is HttpRequestException or WebSocketException or TimeoutException or InvalidOperationException)
                {
                    // A navigation may replace the target's websocket. Re-resolve the dedicated page.
                    page = await GetPageTargetAsync(token);
                }
            }

            var snapshot = await ReadPageSnapshotAsync(page, token);
            return $"Navigation was requested. Current page URL: {snapshot.Url}\nTitle: {snapshot.Title}\nReady state: {snapshot.ReadyState}\nThis does not prove that the site's intended operation succeeded.";
        }
        finally { _gate.Release(); }
    }

    public async Task<string> ReadCurrentPageAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var page = await GetPageTargetAsync(token);
            var snapshot = await ReadPageSnapshotAsync(page, token);
            var output = new JsonObject
            {
                ["title"] = snapshot.Title,
                ["url"] = snapshot.Url,
                ["readyState"] = snapshot.ReadyState,
                ["visibleText"] = snapshot.Text,
                ["links"] = JsonNode.Parse(snapshot.LinksJson),
                ["buttons"] = JsonNode.Parse(snapshot.ButtonsJson),
                ["fields"] = JsonNode.Parse(snapshot.FieldsJson),
                ["formCount"] = snapshot.FormCount
            };
            return output.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        finally { _gate.Release(); }
    }

    public async Task<BrowserElementPreview> PreviewElementAsync(string selector, string action, CancellationToken token = default)
    {
        selector = ValidateCssSelector(selector);
        if (action is not ("click" or "fill"))
            throw new InvalidOperationException("Supported browser element actions are click and fill.");

        await _gate.WaitAsync(token);
        try
        {
            var page = await GetPageTargetAsync(token);
            var expression = BuildPreviewExpression(selector, action);
            var json = await EvaluateAsync(page, expression, token);
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("The browser did not return element metadata.");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("found", out var found) || !found.GetBoolean())
                throw new InvalidOperationException(root.TryGetProperty("error", out var error) ? error.GetString() : "No element matched this selector.");

            var snapshot = await ReadPageSnapshotAsync(page, token);
            return new BrowserElementPreview(
                action,
                page.Id,
                snapshot.Url,
                selector,
                root.GetProperty("fingerprint").GetString() ?? string.Empty,
                root.GetProperty("tag").GetString() ?? string.Empty,
                root.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("label", out var label) ? label.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("href", out var href) ? href.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("disabled", out var disabled) && disabled.GetBoolean(),
                root.TryGetProperty("readOnly", out var readOnly) && readOnly.GetBoolean());
        }
        finally { _gate.Release(); }
    }

    public async Task<string> ClickElementAsync(BrowserElementPreview preview, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Action != "click") throw new InvalidOperationException("The preview was not created for a click action.");
        await _gate.WaitAsync(token);
        try
        {
            var page = await GetPageTargetAsync(token);
            if (!string.Equals(page.Id, preview.PageTargetId, StringComparison.Ordinal))
                throw new InvalidOperationException("The active browser tab changed while awaiting approval. Inspect the intended tab again; no click was dispatched.");
            var expression = BuildActionExpression(preview, "click", string.Empty);
            var result = await EvaluateAsync(page, expression, token);
            EnsureActionSucceeded(result, "click");
            await Task.Delay(250, token);
            var current = await ReadPageSnapshotAsync(await GetPageTargetAsync(token), token);
            return $"The click event was dispatched to the approved browser element.\nPage after action: {current.Url}\nTitle: {current.Title}\nThis confirms that a DOM click was issued, not that the site's requested outcome succeeded.";
        }
        finally { _gate.Release(); }
    }

    public async Task<string> FillElementAsync(BrowserElementPreview preview, string value, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Action != "fill") throw new InvalidOperationException("The preview was not created for a fill action.");
        if (value is null || value.Length > 10_000)
            throw new InvalidOperationException("Text entry is limited to 10,000 characters.");
        await _gate.WaitAsync(token);
        try
        {
            var page = await GetPageTargetAsync(token);
            if (!string.Equals(page.Id, preview.PageTargetId, StringComparison.Ordinal))
                throw new InvalidOperationException("The active browser tab changed while awaiting approval. Inspect the intended tab again; no text was entered.");
            var expression = BuildActionExpression(preview, "fill", value);
            var result = await EvaluateAsync(page, expression, token);
            EnsureActionSucceeded(result, "fill");
            return $"Text-entry events were dispatched to the approved field. Entered character count: {value.Length}. The field's current value was not read back or included in this result.";
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureBrowserStartedAsync(CancellationToken token)
    {
        if (_debugPort > 0)
        {
            try
            {
                using var currentEndpoint = await _http.GetAsync(DebugEndpoint("json/version"), token);
                if (currentEndpoint.IsSuccessStatusCode) return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            _debugPort = 0;
        }
        if (await TryAttachExistingSessionAsync(token)) return;

        var edgePath = FindEdgeExecutable();
        Directory.CreateDirectory(_profileDirectory);
        var activePortFile = Path.Combine(_profileDirectory, "DevToolsActivePort");
        try { if (File.Exists(activePortFile)) File.Delete(activePortFile); } catch { }

        var start = new ProcessStartInfo(edgePath)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(edgePath)!
        };
        start.ArgumentList.Add("--remote-debugging-port=0");
        start.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        start.ArgumentList.Add("--user-data-dir=" + _profileDirectory);
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--no-default-browser-check");
        start.ArgumentList.Add("--disable-extensions");
        start.ArgumentList.Add("--disable-sync");
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add("about:blank");

        _browserProcess = Process.Start(start) ?? throw new InvalidOperationException("Microsoft Edge could not be started.");
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < BrowserStartTimeout)
        {
            token.ThrowIfCancellationRequested();
            if (_browserProcess.HasExited)
                throw new InvalidOperationException("The dedicated Layal browser process exited before the debugging endpoint became available.");
            try
            {
                if (File.Exists(activePortFile))
                {
                    var lines = await File.ReadAllLinesAsync(activePortFile, token);
                    if (lines.Length > 0 && int.TryParse(lines[0], out var port) && port is > 0 and <= 65535)
                    {
                        _debugPort = port;
                        using var response = await _http.GetAsync(DebugEndpoint("json/version"), token);
                        if (response.IsSuccessStatusCode) return;
                        _debugPort = 0;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException) { }
            await Task.Delay(150, token);
        }

        throw new TimeoutException("Microsoft Edge did not expose its local automation endpoint within 15 seconds. Check whether Edge is installed and allowed to start.");
    }

    private async Task<bool> TryAttachExistingSessionAsync(CancellationToken token)
    {
        var activePortFile = Path.Combine(_profileDirectory, "DevToolsActivePort");
        if (!File.Exists(activePortFile)) return false;
        try
        {
            var lines = await File.ReadAllLinesAsync(activePortFile, token);
            if (lines.Length == 0 || !int.TryParse(lines[0], out var port) || port is <= 0 or > 65535)
                return false;

            using var response = await _http.GetAsync($"http://127.0.0.1:{port}/json/version", token);
            if (!response.IsSuccessStatusCode) return false;
            var json = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Browser", out var browserName) ||
                !(browserName.GetString() ?? string.Empty).StartsWith("Edg/", StringComparison.OrdinalIgnoreCase))
                return false;

            _debugPort = port;
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or JsonException or TaskCanceledException)
        {
            return false;
        }
    }

    private static string FindEdgeExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        var match = candidates.FirstOrDefault(File.Exists);
        return match ?? throw new FileNotFoundException("Microsoft Edge was not found in its standard Windows installation locations. Install Edge or configure a browser adapter before using browser tools.");
    }

    private async Task<CdpPageTarget> GetPageTargetAsync(CancellationToken token)
    {
        if (_debugPort <= 0)
            await TryAttachExistingSessionAsync(token);
        if (_debugPort <= 0)
            throw new InvalidOperationException("No Layal-controlled browser session is active. Use browser_navigate first; Layal will launch a dedicated Edge profile after you approve navigation.");
        using var response = await _http.GetAsync(DebugEndpoint("json/list"), token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(token);
        using var doc = JsonDocument.Parse(json);
        var pages = doc.RootElement.EnumerateArray()
            .Where(x => x.TryGetProperty("type", out var type) && type.GetString() == "page")
            .Where(x => x.TryGetProperty("webSocketDebuggerUrl", out var ws) && !string.IsNullOrWhiteSpace(ws.GetString()))
            .Select(x => new CdpPageTarget(
                x.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                x.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                x.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                x.GetProperty("webSocketDebuggerUrl").GetString() ?? string.Empty))
            .ToList();

        var page = pages.LastOrDefault(x => !x.Url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase));
        return page ?? throw new InvalidOperationException("The Layal-controlled browser has no page target. Ask Layal to navigate to an HTTP/HTTPS URL first.");
    }

    private async Task<BrowserPageSnapshot> ReadPageSnapshotAsync(CdpPageTarget page, CancellationToken token)
    {
        var expression = "(() => JSON.stringify({title:document.title||'',url:location.href,readyState:document.readyState," +
            "text:(document.body?.innerText||'').slice(0,20000)," +
            "links:Array.from(document.querySelectorAll('a[href]')).slice(0,50).map(a=>({text:(a.innerText||a.getAttribute('aria-label')||'').trim().replace(/\\s+/g,' ').slice(0,160),href:a.href}))," +
            "buttons:Array.from(document.querySelectorAll('button,[role=button],input[type=button],input[type=submit]')).slice(0,40).map(e=>({text:(e.innerText||e.value||e.getAttribute('aria-label')||'').trim().replace(/\\s+/g,' ').slice(0,120),id:e.id||'',type:e.tagName.toLowerCase()}))," +
            "fields:Array.from(document.querySelectorAll('input:not([type=password]):not([type=file]):not([type=hidden]),textarea,[contenteditable=true]')).slice(0,40).map(e=>({tag:e.tagName.toLowerCase(),type:e.type||'',name:e.name||'',id:e.id||'',placeholder:e.placeholder||'',label:e.getAttribute('aria-label')||'',disabled:e.disabled===true}))," +
            "formCount:document.forms.length}))()";
        var json = await EvaluateAsync(page, expression, token);
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("The browser returned no page metadata.");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new BrowserPageSnapshot(
            root.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("readyState", out var ready) ? ready.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("links", out var links) ? links.GetRawText() : "[]",
            root.TryGetProperty("buttons", out var buttons) ? buttons.GetRawText() : "[]",
            root.TryGetProperty("fields", out var fields) ? fields.GetRawText() : "[]",
            root.TryGetProperty("formCount", out var forms) ? forms.GetInt32() : 0);
    }

    private async Task<string?> EvaluateAsync(CdpPageTarget page, string expression, CancellationToken token)
    {
        var result = await SendCommandAsync(page, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true
        }, token);
        if (result["result"]?["exceptionDetails"] is JsonObject exception)
        {
            var description = exception["exception"]?["description"]?.GetValue<string>() ??
                exception["text"]?.GetValue<string>() ?? "JavaScript execution failed.";
            throw new InvalidOperationException("Browser page evaluation failed: " + description);
        }
        var value = result["result"]?["result"]?["value"];
        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : value?.ToJsonString();
    }

    private async Task<JsonObject> SendCommandAsync(CdpPageTarget page, string method, JsonObject parameters, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(CdpRequestTimeout);
        using var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri(page.WebSocketUrl), timeout.Token);
            var request = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method, @params = parameters });
            await socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            using var message = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("The browser closed the DevTools connection.");
                message.Write(buffer, 0, received.Count);
                if (message.Length > MaxCdpMessageBytes)
                    throw new InvalidOperationException("The browser DevTools response exceeded 2 MB.");
                if (received.EndOfMessage) break;
            }
            using var doc = JsonDocument.Parse(message.ToArray());
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var id) || id.GetInt32() != 1)
                throw new InvalidOperationException("The browser DevTools response did not match the request.");
            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException("Browser DevTools command failed: " + error.GetRawText());
            return JsonNode.Parse(root.GetRawText()) as JsonObject ?? new JsonObject();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"Browser DevTools request '{method}' timed out after {CdpRequestTimeout.TotalSeconds:0} seconds.");
        }
    }

    private string DebugEndpoint(string route) => $"http://127.0.0.1:{_debugPort}/{route}";

    private static string BuildPreviewExpression(string selector, string action)
    {
        var q = JsonSerializer.Serialize(selector);
        var requested = JsonSerializer.Serialize(action);
        return "(() => {const q=" + q + ",action=" + requested + ";" +
            "const matches=document.querySelectorAll(q);if(matches.length===0)return JSON.stringify({found:false,error:'No element matched the selector.'});" +
            "if(matches.length!==1)return JSON.stringify({found:false,error:'The selector matched multiple elements. Use a more specific selector; ambiguous actions are blocked.'});" +
            "const e=matches[0];const tag=e.tagName.toLowerCase(),type=(e.type||'').toLowerCase(),disabled=e.disabled===true,readOnly=e.readOnly===true;" +
            "const visible=!!(e.getClientRects().length&&getComputedStyle(e).visibility!=='hidden'&&getComputedStyle(e).display!=='none');" +
            "const text=(e.innerText||e.getAttribute('aria-label')||'').trim().replace(/\\s+/g,' ').slice(0,160);" +
            "const label=e.getAttribute('aria-label')||e.getAttribute('name')||e.getAttribute('placeholder')||'';" +
            "const fp=JSON.stringify({tag,id:e.id||'',name:e.getAttribute('name')||'',type, label, text, href:e.href||''});" +
            "if(!visible)return JSON.stringify({found:false,error:'The selected element is not visible.'});" +
            "if(disabled)return JSON.stringify({found:false,error:'The selected element is disabled.'});" +
            "if(action==='click'){const clickable=tag==='button'||tag==='a'||e.getAttribute('role')==='button'||(tag==='input'&&['button','submit','reset'].includes(type))||tag==='summary';if(!clickable)return JSON.stringify({found:false,error:'Selector must match a visible link, button, role=button, submit/button input, or summary.'});" +
            "if(tag==='a'&&e.href&&!/^https?:/i.test(e.href))return JSON.stringify({found:false,error:'Only HTTP/HTTPS links are allowed for browser clicks.'});}" +
            "if(action==='fill'){const allowed=(tag==='textarea')||(tag==='input'&&['text','email','search','tel','url','number'].includes(type))||e.isContentEditable;if(!allowed||readOnly)return JSON.stringify({found:false,error:'Selector must match an editable visible text/email/search/tel/url/number input, textarea, or contenteditable field. Password/file/hidden fields are not supported.'});}" +
            "return JSON.stringify({found:true,tag,type,id:e.id||'',name:e.getAttribute('name')||'',label,text,href:e.href||'',disabled,readOnly,fingerprint:fp});})()";
    }

    private static string BuildActionExpression(BrowserElementPreview preview, string action, string value)
    {
        var selector = JsonSerializer.Serialize(preview.Selector);
        var expectedUrl = JsonSerializer.Serialize(preview.PageUrl);
        var fingerprint = JsonSerializer.Serialize(preview.Fingerprint);
        var actionJson = JsonSerializer.Serialize(action);
        var valueJson = JsonSerializer.Serialize(value);
        return "(() => {const q=" + selector + ",expectedUrl=" + expectedUrl + ",expectedFp=" + fingerprint +
            ",action=" + actionJson + ",value=" + valueJson + ";" +
            "if(location.href!==expectedUrl)return JSON.stringify({ok:false,error:'The page URL changed while awaiting approval.'});" +
            "const matches=document.querySelectorAll(q);if(matches.length!==1)return JSON.stringify({ok:false,error:'The selector no longer matches exactly one element.'});" +
            "const e=matches[0];const tag=e.tagName.toLowerCase(),type=(e.type||'').toLowerCase();" +
            "const text=(e.innerText||e.getAttribute('aria-label')||'').trim().replace(/\\s+/g,' ').slice(0,160);" +
            "const label=e.getAttribute('aria-label')||e.getAttribute('name')||e.getAttribute('placeholder')||'';" +
            "const fp=JSON.stringify({tag,id:e.id||'',name:e.getAttribute('name')||'',type,label,text,href:e.href||''});" +
            "if(fp!==expectedFp)return JSON.stringify({ok:false,error:'The selected element changed while awaiting approval. Inspect the page again.'});" +
            "const visible=!!(e.getClientRects().length&&getComputedStyle(e).visibility!=='hidden'&&getComputedStyle(e).display!=='none');" +
            "if(!visible||e.disabled===true)return JSON.stringify({ok:false,error:'The selected element is hidden or disabled.'});" +
            "if(action==='click'){const clickable=tag==='button'||tag==='a'||e.getAttribute('role')==='button'||(tag==='input'&&['button','submit','reset'].includes(type))||tag==='summary';if(!clickable)return JSON.stringify({ok:false,error:'The element is no longer an allowed click target.'});e.click();return JSON.stringify({ok:true,action:'click',tag,text});}" +
            "const allowed=(tag==='textarea')||(tag==='input'&&['text','email','search','tel','url','number'].includes(type))||e.isContentEditable;" +
            "if(!allowed||e.readOnly===true)return JSON.stringify({ok:false,error:'The element is no longer an allowed editable field.'});" +
            "if(e.isContentEditable)e.innerText=value;else {const proto=tag==='textarea'?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;const setter=Object.getOwnPropertyDescriptor(proto,'value')?.set;if(!setter)return JSON.stringify({ok:false,error:'The field value setter is unavailable.'});setter.call(e,value);}" +
            "e.dispatchEvent(new Event('input',{bubbles:true,composed:true}));e.dispatchEvent(new Event('change',{bubbles:true}));" +
            "return JSON.stringify({ok:true,action:'fill',tag,characterCount:value.length});})()";
    }

    private static void EnsureActionSucceeded(string? json, string action)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("The browser returned no result after the action.");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException(root.TryGetProperty("error", out var error) ? error.GetString() : $"The browser {action} action was not completed.");
    }
}

public sealed record BrowserElementPreview(
    string Action,
    string PageTargetId,
    string PageUrl,
    string Selector,
    string Fingerprint,
    string Tag,
    string Type,
    string Id,
    string Name,
    string Label,
    string Text,
    string Href,
    bool Disabled,
    bool ReadOnly);

public sealed record BrowserPageSnapshot(
    string Title,
    string Url,
    string ReadyState,
    string Text,
    string LinksJson,
    string ButtonsJson,
    string FieldsJson,
    int FormCount);

internal sealed record CdpPageTarget(string Id, string Title, string Url, string WebSocketUrl);
