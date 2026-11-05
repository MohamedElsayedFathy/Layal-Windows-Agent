using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace Layal.Services;

/// <summary>
/// Controls a dedicated Microsoft Edge profile through its loopback-only Chromium DevTools Protocol.
/// It never attaches to the user's normal browser profile. Every navigation, page disclosure, click,
/// text-entry, and local file-selection operation must be gated by the calling agent's local approval callback.
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
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private Process? _browserProcess;
    private bool _attachedToExistingSession;
    private int _debugPort;
    private int _shutdownRequested;

    public BrowserAutomationService(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _profileDirectory = Path.Combine(Path.GetFullPath(dataRoot), "BrowserProfile");
    }

    /// <summary>
    /// Rejects new browser work, cancels active/queued operations, then closes an owned Edge process
    /// only after the operation gate is available. This avoids racing process disposal with CDP I/O.
    /// </summary>
    public async Task ShutdownAsync()
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        try { _shutdownCancellation.Cancel(); }
        catch { /* Cancellation must not prevent process cleanup. */ }

        await _gate.WaitAsync();
        try
        {
            var process = _browserProcess;
            var attachedPort = process is null && _attachedToExistingSession ? _debugPort : 0;
            _browserProcess = null;
            _attachedToExistingSession = false;
            _debugPort = 0;
            if (process is not null)
            {
                try
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch { /* Shutdown must not prevent the rest of the app from exiting. */ }
                }
                finally
                {
                    process.Dispose();
                }
            }
            else if (attachedPort > 0)
            {
                // This is an Edge session recovered after an abnormal exit, not a process
                // we launched in this instance. Close it through the validated loopback CDP
                // endpoint instead of leaving it running after Layal exits.
                try
                {
                    if (!await TryCloseAttachedBrowserSessionAsync(attachedPort))
                        Trace.TraceWarning("Layal could not issue Browser.close to the previously attached Edge session on port {0}.", attachedPort);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Layal failed to close the previously attached Edge session on port {0}: {1}", attachedPort, ex.Message);
                }
            }

            // All operations have left the gate and new operations are permanently rejected.
            _http.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private CancellationTokenSource CreateOperationToken(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _shutdownRequested) != 0)
            throw new ObjectDisposedException(nameof(BrowserAutomationService), "The browser service is shutting down.");

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCancellation.Token);
        if (Volatile.Read(ref _shutdownRequested) != 0)
        {
            linked.Dispose();
            throw new ObjectDisposedException(nameof(BrowserAutomationService), "The browser service is shutting down.");
        }
        return linked;
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

    /// <summary>Fact-check source URLs are HTTPS-only, port 443, and never IP literals or local names.</summary>
    public static Uri ValidateFactCheckSourceUrl(string value)
    {
        var uri = ValidateHttpUrl(value);
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 || string.IsNullOrWhiteSpace(host) || uri.IsLoopback ||
            IPAddress.TryParse(host, out _) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".example", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Length > 2048 || uri.Query.Length > 2048)
            throw new InvalidOperationException("Fact-check sources must be public HTTPS URLs on the standard port, without local/IP hosts or reserved test domains.");
        return uri;
    }

    /// <summary>
    /// Conservative registrable-domain approximation for common public suffixes. It is used only
    /// to avoid treating subdomains of the same publisher as independent sources.
    /// </summary>
    public static string GetRegistrableDomain(string host)
    {
        var normalized = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0 || IPAddress.TryParse(normalized, out _)) return normalized;
        var labels = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length <= 2) return normalized;
        var twoLabelSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ac.uk", "co.uk", "gov.uk", "org.uk", "sch.uk",
            "com.au", "net.au", "org.au", "edu.au", "gov.au",
            "co.nz", "org.nz", "govt.nz", "ac.nz",
            "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp",
            "com.br", "net.br", "org.br", "gov.br",
            "com.cn", "net.cn", "org.cn", "gov.cn",
            "com.sg", "com.my", "com.tr", "com.mx", "com.ar",
            "co.in", "firm.in", "net.in", "org.in", "gen.in", "ind.in",
            "com.eg", "gov.eg", "edu.eg", "org.eg", "net.eg"
        };
        var suffix = labels[^2] + "." + labels[^1];
        return string.Join(".", labels[^(twoLabelSuffixes.Contains(suffix) ? 3 : 2)..]);
    }

    public static bool AreSameRegistrableDomain(string firstHost, string secondHost)
    {
        var first = GetRegistrableDomain(firstHost);
        var second = GetRegistrableDomain(secondHost);
        return first.Length > 0 && second.Length > 0 &&
            string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ValidatePublicHostResolutionAsync(string host, CancellationToken token)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host, token); }
        catch (SocketException ex)
        {
            throw new InvalidOperationException("The approved source host could not be resolved to a public address.", ex);
        }
        if (addresses.Length == 0 || addresses.Any(address => !SafeBrowserDownloadService.IsPublicAddress(address)))
            throw new InvalidOperationException("The approved source host resolves to a private, local, reserved, or otherwise non-public IP address. No navigation was performed.");
    }

    public static string ValidateCssSelector(string value)
    {
        var selector = (value ?? string.Empty).Trim();
        if (selector.Length is 0 or > 500 || selector.Any(char.IsControl))
            throw new InvalidOperationException("Enter a CSS selector of 1–500 printable characters.");
        return selector;
    }

    /// <summary>
    /// Rejects clicks that can directly submit/reset an HTML form or target an element inside or
    /// associated with a form. Allowed page click handlers can still have side effects.
    /// </summary>
    public static void ValidateBrowserClickTarget(string tag, string type, string role, bool formAssociated)
    {
        var normalizedTag = (tag ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedRole = (role ?? string.Empty).Trim().ToLowerInvariant();

        if ((normalizedTag is "button" or "input") && (normalizedType is "submit" or "reset"))
            throw new InvalidOperationException("Submit and reset controls are not supported by browser_click.");

        if (formAssociated)
            throw new InvalidOperationException("Controls inside or associated with an HTML form cannot be clicked by Layal. Fill fields if needed, then submit or reset the form manually in the browser.");

        var clickable = normalizedTag is "a" or "button" or "summary" ||
            normalizedRole == "button" ||
            (normalizedTag == "input" && normalizedType == "button");
        if (!clickable)
            throw new InvalidOperationException("Only a visible HTTP/HTTPS link, standalone button, role=button, input type=button, or summary can be clicked.");
    }

    /// <summary>Allows choosing a file only through one visible, enabled HTML file input.</summary>
    public static void ValidateBrowserUploadTarget(string tag, string type)
    {
        if (!string.Equals((tag ?? string.Empty).Trim(), "input", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals((type ?? string.Empty).Trim(), "file", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("browser_upload_file requires one visible input[type=file] target. It never clicks or submits the form.");
    }

    /// <summary>Refuses ambiguous sessions instead of guessing which tab the user intended.</summary>
    public static void ValidateSinglePageSession(int pageCount)
    {
        if (pageCount == 0)
            throw new InvalidOperationException("The dedicated Layal browser has no usable page tab. Navigate to an HTTP/HTTPS URL first.");
        if (pageCount != 1)
            throw new InvalidOperationException("Layal browser control is paused because the dedicated Edge window has multiple page tabs or popups. Close extra tabs/windows and retry; Layal will not guess which page you intended.");
    }

    /// <summary>
    /// Enforces the target page's accept attribute before a local file can be selected.
    /// A blank accept attribute means the page does not declare a type restriction.
    /// </summary>
    public static void ValidateBrowserUploadAccept(string accept, string extension)
    {
        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (ext.Length < 2 || ext[0] != '.')
            throw new InvalidOperationException("The selected upload must have a recognized file extension.");

        var mime = ext switch
        {
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => throw new InvalidOperationException("This file type is not supported for browser upload.")
        };
        var tokens = (accept ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())
            .ToArray();
        if (tokens.Length == 0 || tokens.Contains("*/*", StringComparer.Ordinal))
            return;

        var family = mime[..mime.IndexOf('/')];
        if (tokens.Any(token => token == ext || token == mime || token == family + "/*"))
            return;

        throw new InvalidOperationException(
            $"The selected file type '{ext}' does not match the website file input's accept restriction '{accept}'. No file was selected.");
    }

    /// <summary>
    /// Builds an HTTPS Bing search URL. Search text is normalized and bounded before it can be sent.
    /// </summary>
    public static Uri BuildBingSearchUri(string query)
    {
        var rawQuery = query ?? string.Empty;
        if (rawQuery.Any(char.IsControl))
            throw new InvalidOperationException("The search query cannot contain control characters.");
        var normalized = string.Join(" ", rawQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length is < 3 or > 500)
            throw new InvalidOperationException("The search query must contain 3–500 printable characters.");

        return new Uri("https://www.bing.com/search?q=" + Uri.EscapeDataString(normalized), UriKind.Absolute);
    }

    public static bool IsBingDomain(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            (uri.Host.Equals("bing.com", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".bing.com", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrEmpty(uri.UserInfo);
    }

    /// <summary>
    /// Classifies common Bing challenge, consent, no-results, and unavailable states without
    /// treating arbitrary redirected sites as a successful search.
    /// </summary>
    public static string ClassifyBingSearchPage(string finalUrl, string title, string visibleText)
    {
        if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri) || !IsBingDomain(uri))
            return "unexpected_redirect";

        var content = (title ?? string.Empty) + "\n" + (visibleText ?? string.Empty);
        var blockedPhrases = new[]
        {
            "access denied", "your request has been blocked", "temporarily blocked",
            "unusual traffic", "automated queries", "verify you are not a robot",
            "verify you're not a robot", "verify that you are human", "verify you're human",
            "solve the following challenge", "complete the security check", "captcha", "too many requests"
        };
        if (blockedPhrases.Any(phrase => content.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            return "blocked_or_challenge";

        var consentPhrases = new[]
        {
            "consent required", "before you continue", "choose your privacy settings", "privacy choices"
        };
        if (consentPhrases.Any(phrase => content.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            return "consent_required";

        var noResultsPhrases = new[]
        {
            "there are no results for", "no results found",
            "your search did not match any documents", "we didn't find results for"
        };
        if (noResultsPhrases.Any(phrase => content.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            return "no_results";

        if (string.IsNullOrWhiteSpace(visibleText))
            return "empty_page";

        return "results_available";
    }

    /// <summary>
    /// Runs one bounded Bing search in the dedicated Edge profile, then verifies that the
    /// same browser tab still contains a Bing page before and after the visible-text snapshot.
    /// Unexpected external redirects are never read or shared.
    /// </summary>
    public async Task<BrowserSearchSnapshot> SearchBingAsync(string query, CancellationToken token = default)
    {
        var normalizedQuery = string.Join(" ", (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var targetUrl = BuildBingSearchUri(normalizedQuery);
        using var operationCancellation = CreateOperationToken(token);
        token = operationCancellation.Token;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            await EnsureBrowserStartedAsync(token);
            var page = await GetPageTargetAsync(token);
            var response = await SendCommandAsync(page, "Page.navigate", new JsonObject
            {
                ["url"] = targetUrl.AbsoluteUri
            }, token);
            var errorText = response["result"]?["errorText"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(errorText))
                throw new InvalidOperationException("Bing search navigation failed: " + errorText);

            for (var attempt = 0; attempt < 12; attempt++)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(250, token);
                try
                {
                    var readyState = await EvaluateAsync(page, "document.readyState", token);
                    if (readyState is "interactive" or "complete")
                        break;
                }
                catch (Exception ex) when (ex is HttpRequestException or WebSocketException or TimeoutException or InvalidOperationException)
                {
                    var refreshedPage = await GetPageTargetAsync(token);
                    if (!string.Equals(refreshedPage.Id, page.Id, StringComparison.Ordinal))
                        throw new InvalidOperationException("The active browser tab changed while Bing was loading. The search snapshot was discarded.");
                    page = refreshedPage;
                }
            }

            token.ThrowIfCancellationRequested();
            var initialUrl = await EvaluateAsync(page, "location.href", token) ?? string.Empty;
            var initialTitle = await EvaluateAsync(page, "document.title", token) ?? string.Empty;
            var finalUri = ValidateHttpUrl(initialUrl);
            if (!IsBingDomain(finalUri))
            {
                // Do not read text from a non-Bing destination reached through a redirect.
                return new BrowserSearchSnapshot(
                    normalizedQuery, targetUrl.AbsoluteUri, finalUri.AbsoluteUri, initialTitle,
                    "unexpected_redirect", "The search redirected outside Bing; page content was not read or shared.",
                    string.Empty, "[]");
            }

            var snapshot = await ReadPageSnapshotAsync(page, token);
            token.ThrowIfCancellationRequested();
            var currentUrl = await EvaluateAsync(page, "location.href", token) ?? string.Empty;
            var currentTitle = await EvaluateAsync(page, "document.title", token) ?? string.Empty;
            var activePage = await GetPageTargetAsync(token);
            if (!string.Equals(activePage.Id, page.Id, StringComparison.Ordinal) ||
                !string.Equals(snapshot.Url, initialUrl, StringComparison.Ordinal) ||
                !string.Equals(snapshot.Title, initialTitle, StringComparison.Ordinal) ||
                !string.Equals(currentUrl, initialUrl, StringComparison.Ordinal) ||
                !string.Equals(currentTitle, initialTitle, StringComparison.Ordinal))
                throw new InvalidOperationException("The Bing tab or page changed while search results were being read. The snapshot was discarded and not shared.");

            var status = ClassifyBingSearchPage(snapshot.Url, snapshot.Title, snapshot.Text);
            var notice = status switch
            {
                "blocked_or_challenge" => "Bing presented an access block or anti-automation challenge. No search results should be inferred from this page.",
                "consent_required" => "Bing requires a privacy/consent choice in its dedicated browser. Complete it manually and run the search again.",
                "no_results" => "Bing returned a page indicating that no matching results were found.",
                "empty_page" => "Bing loaded a page with no readable visible text.",
                _ => "Bing results were read from the same verified Bing tab. Result links and page text remain untrusted leads."
            };

            return new BrowserSearchSnapshot(
                normalizedQuery, targetUrl.AbsoluteUri, snapshot.Url, snapshot.Title,
                status, notice, snapshot.Text, snapshot.LinksJson);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> NavigateAsync(string url, CancellationToken token = default)
    {
        var targetUrl = ValidateHttpUrl(url);
        using var operationCancellation = CreateOperationToken(token);
        token = operationCancellation.Token;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Reads only the active tab identity (target id, URL, title and ready state) for a consent
    /// preview. It does not extract visible page text or DOM element content.
    /// </summary>
    /// <summary>
    /// Opens one user-approved direct source and captures its page in a single browser-gated
    /// operation. The final page must remain HTTPS on the same registrable domain, resolve only
    /// to public IP addresses, and remain the same tab/URL/title throughout the snapshot.
    /// </summary>
    public async Task<string> NavigateAndReadEvidencePageAsync(string url, string expectedHost, CancellationToken token = default)
    {
        var targetUrl = ValidateFactCheckSourceUrl(url);
        var expectedDomain = GetRegistrableDomain(expectedHost);
        if (!string.Equals(GetRegistrableDomain(targetUrl.IdnHost), expectedDomain, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The approved source URL does not match its expected publisher domain.");

        await ValidatePublicHostResolutionAsync(targetUrl.IdnHost, token);
        using var operationCancellation = CreateOperationToken(token);
        token = operationCancellation.Token;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            await EnsureBrowserStartedAsync(token);
            var page = await GetPageTargetAsync(token);
            var response = await SendCommandAsync(page, "Page.navigate", new JsonObject { ["url"] = targetUrl.AbsoluteUri }, token);
            var errorText = response["result"]?["errorText"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(errorText))
                throw new InvalidOperationException("Fact-check source navigation failed: " + errorText);

            var loaded = false;
            for (var attempt = 0; attempt < 12; attempt++)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(250, token);
                try
                {
                    var readyState = await EvaluateAsync(page, "document.readyState", token);
                    if (readyState is "interactive" or "complete") { loaded = true; break; }
                }
                catch (Exception ex) when (ex is HttpRequestException or WebSocketException or TimeoutException or InvalidOperationException)
                {
                    var refreshed = await GetPageTargetAsync(token);
                    if (!string.Equals(refreshed.Id, page.Id, StringComparison.Ordinal))
                        throw new InvalidOperationException("The dedicated browser tab changed while the approved source was loading. No page content was shared.");
                    page = refreshed;
                }
            }

            token.ThrowIfCancellationRequested();
            if (!loaded)
                throw new TimeoutException("The approved source page did not reach an interactive or complete document state.");

            var initialUrlText = await EvaluateAsync(page, "location.href", token) ?? string.Empty;
            var initialTitle = await EvaluateAsync(page, "document.title", token) ?? string.Empty;
            var finalUrl = ValidateFactCheckSourceUrl(initialUrlText);
            if (!AreSameRegistrableDomain(targetUrl.IdnHost, finalUrl.IdnHost))
                throw new InvalidOperationException("The approved source redirected to a different publisher domain. The redirected page was not read or shared.");
            await ValidatePublicHostResolutionAsync(finalUrl.IdnHost, token);

            var snapshot = await ReadPageSnapshotAsync(page, token);
            token.ThrowIfCancellationRequested();
            var currentUrl = await EvaluateAsync(page, "location.href", token) ?? string.Empty;
            var currentTitle = await EvaluateAsync(page, "document.title", token) ?? string.Empty;
            var activePage = await GetPageTargetAsync(token);
            if (!string.Equals(activePage.Id, page.Id, StringComparison.Ordinal) ||
                !string.Equals(snapshot.Url, initialUrlText, StringComparison.Ordinal) ||
                !string.Equals(snapshot.Title, initialTitle, StringComparison.Ordinal) ||
                !string.Equals(currentUrl, initialUrlText, StringComparison.Ordinal) ||
                !string.Equals(currentTitle, initialTitle, StringComparison.Ordinal))
                throw new InvalidOperationException("The source tab or page changed while its evidence was being read. The snapshot was discarded.");

            var output = new JsonObject
            {
                ["title"] = snapshot.Title,
                ["url"] = finalUrl.AbsoluteUri,
                ["readyState"] = snapshot.ReadyState,
                ["visibleText"] = snapshot.Text,
                ["links"] = JsonNode.Parse(snapshot.LinksJson) ?? new JsonArray(),
                ["buttons"] = JsonNode.Parse(snapshot.ButtonsJson) ?? new JsonArray(),
                ["fields"] = JsonNode.Parse(snapshot.FieldsJson) ?? new JsonArray(),
                ["formCount"] = snapshot.FormCount,
                ["author"] = snapshot.Author,
                ["publishedDate"] = snapshot.PublishedDate,
                ["modifiedDate"] = snapshot.ModifiedDate,
                ["visibleTimeDate"] = snapshot.VisibleTimeDate,
                ["visibleTimes"] = JsonNode.Parse(snapshot.VisibleTimesJson) ?? new JsonArray(),
                ["siteName"] = snapshot.SiteName,
                ["description"] = snapshot.Description,
                ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
            };
            return output.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        finally { _gate.Release(); }
    }

    public async Task<BrowserPageIdentity> GetCurrentPageIdentityAsync(CancellationToken token = default)
    {
        using var operationCancellation = CreateOperationToken(token);
        token = operationCancellation.Token;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var page = await GetPageTargetAsync(token);
            var json = await EvaluateAsync(page,
                "JSON.stringify({url:location.href,title:document.title||'',readyState:document.readyState})",
                token) ?? string.Empty;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var urlText = root.GetProperty("url").GetString() ?? string.Empty;
            var title = root.GetProperty("title").GetString() ?? string.Empty;
            var readyState = root.GetProperty("readyState").GetString() ?? string.Empty;
            var url = ValidateHttpUrl(urlText).AbsoluteUri;
            return new BrowserPageIdentity(page.Id, url, title, readyState);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ReadCurrentPageAsync(CancellationToken token = default)
    {
        using var operationCancellation = CreateOperationToken(token);
        token = operationCancellation.Token;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var page = await GetPageTargetAsync(token);
            var snapshot = await ReadPageSnapshotAsync(page, token);
            var output = new JsonObject
            {
                ["title"] = snapshot.Title,
                ["url"] = snapshot.Url,
                ["readyState"] = snapshot.ReadyState,
                ["visibleText"] = snapshot.Text,
                ["links"] = JsonNode.Parse(snapshot.LinksJson) ?? new JsonArray(),
                ["buttons"] = JsonNode.Parse(snapshot.ButtonsJson) ?? new JsonArray(),
                ["fields"] = JsonNode.Parse(snapshot.FieldsJson) ?? new JsonArray(),
                ["formCount"] = snapshot.FormCount,
                ["author"] = snapshot.Author,
                ["publishedDate"] = snapshot.PublishedDate,
                ["modifiedDate"] = snapshot.ModifiedDate,
                ["visibleTimeDate"] = snapshot.VisibleTimeDate,
                ["visibleTimes"] = JsonNode.Parse(snapshot.VisibleTimesJson) ?? new JsonArray(),
                ["siteName"] = snapshot.SiteName,
                ["description"] = snapshot.Description,
                ["capturedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
            };
            return output.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        finally { _gate.Release(); }
    }

    public async Task<BrowserElementPreview> PreviewElementAsync(string selector, string action, CancellationToken token = default)
    {
        selector = ValidateCssSelector(selector);
        if (action is not ("click" or "fill" or "upload"))
            throw new InvalidOperationException("Supported browser element actions are click, fill, and upload.");

        using var operationCancellation = CreateOperationToken(token);
        token = operationCancellation.Token;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var page = await GetPageTargetAsync(token);
            var expression = BuildPreviewExpression(selector, action);
            var json = await EvaluateAsync(page, expression, token);
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("The browser did not return element metadata.");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("found", out var found) || !found.GetBoolean())
                throw new InvalidOperationException(root.TryGetProperty("error", out var error) ? error.GetString() : "No element matched this selector.");

            var elementTag = root.GetProperty("tag").GetString() ?? string.Empty;
            var elementType = root.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty;
            var elementRole = root.TryGetProperty("role", out var role) ? role.GetString() ?? string.Empty : string.Empty;
            var formAssociated = root.TryGetProperty("formAssociated", out var formOwner) && formOwner.GetBoolean();
            if (action == "click")
                ValidateBrowserClickTarget(elementTag, elementType, elementRole, formAssociated);
            else if (action == "upload")
                ValidateBrowserUploadTarget(elementTag, elementType);

            // URL and element metadata come from the same synchronous DOM evaluation so a
            // navigation cannot pair an old element with a new page URL in the consent preview.
            var previewUrl = ValidateHttpUrl(root.GetProperty("url").GetString() ?? string.Empty).AbsoluteUri;
            return new BrowserElementPreview(
                action,
                page.Id,
                previewUrl,
                selector,
                root.GetProperty("fingerprint").GetString() ?? string.Empty,
                elementTag,
                elementType,
                root.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("label", out var label) ? label.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("href", out var href) ? href.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("disabled", out var disabled) && disabled.GetBoolean(),
                root.TryGetProperty("readOnly", out var readOnly) && readOnly.GetBoolean(),
                root.TryGetProperty("accept", out var accept) ? accept.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("multiple", out var multiple) && multiple.GetBoolean());
        }
        finally { _gate.Release(); }
    }

    public async Task<string> ClickElementAsync(BrowserElementPreview preview, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Action != "click") throw new InvalidOperationException("The preview was not created for a click action.");
        using var operationCancellation = CreateOperationToken(token);
        token = operationCancellation.Token;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var page = await GetPageTargetAsync(token);
            if (!string.Equals(page.Id, preview.PageTargetId, StringComparison.Ordinal))
                throw new InvalidOperationException("The active browser tab changed while awaiting approval. Inspect the intended tab again; no click was dispatched.");
            var expression = BuildActionExpression(preview, "click", string.Empty);
            var result = await EvaluateAsync(page, expression, token);
            EnsureActionSucceeded(result, "click");
            await Task.Delay(250, token);
            // Read the original tab directly: an approved link may intentionally open a new tab.
            // The next Layal action will pause if multiple page tabs exist, rather than guessing.
            // Return only the post-click URL/title; do not read page body or DOM metadata as a
            // side effect of a click. An approved link may open a new page target.
            var after = await EvaluateAsync(page,
                "JSON.stringify({url:location.href,title:document.title||''})",
                token) ?? string.Empty;
            using var afterDocument = JsonDocument.Parse(after);
            var currentUrl = afterDocument.RootElement.GetProperty("url").GetString() ?? string.Empty;
            var currentTitle = afterDocument.RootElement.GetProperty("title").GetString() ?? string.Empty;
            try { currentUrl = ValidateHttpUrl(currentUrl).AbsoluteUri; }
            catch (InvalidOperationException) { currentUrl = "(non-HTTP page)"; }

            var extraTabNotice = string.Empty;
            try { _ = await GetPageTargetAsync(token); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("multiple page tabs", StringComparison.OrdinalIgnoreCase))
            {
                extraTabNotice = "\nAdditional tab(s) or popup(s) opened. Layal will pause browser actions until only one page tab remains.";
            }
            return $"The click event was dispatched to the approved browser element.\nPage after action: {currentUrl}\nTitle: {currentTitle}{extraTabNotice}\nThis confirms that a DOM click was issued, not that the site's requested outcome succeeded.";
        }
        finally { _gate.Release(); }
    }

    public async Task<string> FillElementAsync(BrowserElementPreview preview, string value, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Action != "fill") throw new InvalidOperationException("The preview was not created for a fill action.");
        if (value is null || value.Length > 10_000)
            throw new InvalidOperationException("Text entry is limited to 10,000 characters.");
        using var operationCancellation = CreateOperationToken(token);
        token = operationCancellation.Token;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Selects one previously approved local file in one previously approved file input.
    /// The page may react to the file-selection event and upload immediately; Layal never clicks
    /// submit or otherwise submits the surrounding form.
    /// </summary>
    public async Task<string> UploadFileAsync(BrowserElementPreview preview, string absoluteFilePath, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Action != "upload")
            throw new InvalidOperationException("The preview was not created for a browser upload.");
        if (string.IsNullOrWhiteSpace(absoluteFilePath) || !Path.IsPathFullyQualified(absoluteFilePath))
            throw new InvalidOperationException("Browser upload requires a validated absolute workspace file path.");
        if (!File.Exists(absoluteFilePath))
            throw new FileNotFoundException("The approved upload file no longer exists.", absoluteFilePath);
        if ((File.GetAttributes(absoluteFilePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Browser uploads are blocked for symbolic links and other reparse points.");

        using var operationCancellation = CreateOperationToken(token);
        token = operationCancellation.Token;
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var page = await GetPageTargetAsync(token);
            if (!string.Equals(page.Id, preview.PageTargetId, StringComparison.Ordinal))
                throw new InvalidOperationException("The active browser tab changed while awaiting upload approval. Inspect and approve the intended file input again.");
            _ = ValidateHttpUrl(preview.PageUrl);
            ValidateBrowserUploadAccept(preview.Accept, Path.GetExtension(absoluteFilePath));

            var nodeId = await ResolveApprovedUploadNodeIdAsync(page, preview, token);
            token.ThrowIfCancellationRequested();

            // Refuse a new popup/tab that appeared during the consent or node-resolution window.
            var latest = await GetPageTargetAsync(token);
            if (!string.Equals(latest.Id, preview.PageTargetId, StringComparison.Ordinal))
                throw new InvalidOperationException("The active browser tab changed before file selection. No upload was started.");

            await SendCommandAsync(page, "DOM.setFileInputFiles", new JsonObject
            {
                ["nodeId"] = nodeId,
                ["files"] = new JsonArray(JsonValue.Create(absoluteFilePath))
            }, token);

            var fileName = Path.GetFileName(absoluteFilePath);
            return $"Selected {fileName} in the approved browser file input. The website may upload it immediately in response to the file-selection event. Layal did not click a submit button or submit the surrounding form; verify the site's status before continuing.";
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
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
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

        _attachedToExistingSession = false;
        _browserProcess = Process.Start(start) ?? throw new InvalidOperationException("Microsoft Edge could not be started.");
        try
        {
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
        catch
        {
            _debugPort = 0;
            try { if (!_browserProcess.HasExited) _browserProcess.Kill(entireProcessTree: true); } catch { }
            _browserProcess.Dispose();
            _browserProcess = null;
            try { if (File.Exists(activePortFile)) File.Delete(activePortFile); } catch { }
            throw;
        }
    }

    private async Task<bool> TryAttachExistingSessionAsync(CancellationToken token)
    {
        _attachedToExistingSession = false;
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
            _attachedToExistingSession = true;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or JsonException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> TryCloseAttachedBrowserSessionAsync(int port)
    {
        if (port is <= 0 or > 65535) return false;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var response = await _http.GetAsync($"http://127.0.0.1:{port}/json/version", timeout.Token);
        if (!response.IsSuccessStatusCode) return false;

        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("webSocketDebuggerUrl", out var endpointValue))
            return false;

        var endpointText = endpointValue.GetString();
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
            !string.Equals(endpoint.Scheme, "ws", StringComparison.OrdinalIgnoreCase) ||
            endpoint.Port != port ||
            !IPAddress.TryParse(endpoint.Host, out var address) ||
            !IPAddress.IsLoopback(address))
            return false;

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(endpoint, timeout.Token);
        var closeCommand = JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method = "Browser.close" });
        await socket.SendAsync(closeCommand.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        return true;
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
        if (_browserProcess is { HasExited: true })
        {
            _browserProcess.Dispose();
            _browserProcess = null;
            _attachedToExistingSession = false;
            _debugPort = 0;
        }
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

        var usablePages = pages
            .Where(x => !x.Url.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase) &&
                        IsTrustedBrowserWebSocketUrl(x.WebSocketUrl))
            .ToArray();
        ValidateSinglePageSession(usablePages.Length);
        return usablePages[0];
    }

    private async Task<BrowserPageSnapshot> ReadPageSnapshotAsync(CdpPageTarget page, CancellationToken token)
    {
        var expression = """
            (() => JSON.stringify({
              title: document.title || '',
              url: location.href,
              readyState: document.readyState,
              metadata: (() => {
                const allMeta = Array.from(document.querySelectorAll('meta'));
                const meta = (...names) => {
                  for (const name of names) {
                    const found = allMeta.find(m =>
                      (m.name || '').toLowerCase() === name.toLowerCase() ||
                      (m.getAttribute('property') || '').toLowerCase() === name.toLowerCase() ||
                      (m.getAttribute('itemprop') || '').toLowerCase() === name.toLowerCase());
                    if (found?.content) return String(found.content).slice(0, 500);
                  }
                  return '';
                };
                const jsonLd = (() => {
                  const result = {};
                  let visited = 0;
                  const authorName = value => {
                    if (typeof value === 'string') return value;
                    if (Array.isArray(value)) return value.map(authorName).filter(Boolean).join(', ');
                    if (value && typeof value === 'object') return String(value.name || value.alternateName || '');
                    return '';
                  };
                  const visit = (value, depth) => {
                    if (!value || depth > 10 || visited++ > 1500) return;
                    if (Array.isArray(value)) {
                      for (const item of value.slice(0, 100)) visit(item, depth + 1);
                      return;
                    }
                    if (typeof value !== 'object') return;
                    if (!result.publishedDate && typeof value.datePublished === 'string') result.publishedDate = value.datePublished;
                    if (!result.modifiedDate && typeof value.dateModified === 'string') result.modifiedDate = value.dateModified;
                    if (!result.author && value.author) result.author = authorName(value.author);
                    for (const key of ['@graph', 'mainEntity', 'mainEntityOfPage', 'hasPart', 'itemListElement']) {
                      if (value[key]) visit(value[key], depth + 1);
                    }
                  };
                  for (const script of Array.from(document.querySelectorAll('script[type="application/ld+json"]')).slice(0, 10)) {
                    const raw = script.textContent || '';
                    if (!raw || raw.length > 100000) continue;
                    try { visit(JSON.parse(raw), 0); } catch { /* Invalid JSON-LD is untrusted and ignored. */ }
                  }
                  return result;
                })();
                const times = Array.from(document.querySelectorAll('time[datetime]')).slice(0, 8).map(element => ({
                  datetime: String(element.getAttribute('datetime') || '').slice(0, 80),
                  context: String(element.getAttribute('aria-label') || element.title || element.parentElement?.innerText || element.innerText || '')
                    .replace(/\s+/g, ' ').trim().slice(0, 180)
                })).filter(item => item.datetime.length > 0);
                return {
                  author: (meta('author', 'article:author', 'dc.creator', 'dcterms.creator') || jsonLd.author || '').slice(0, 180),
                  publishedDate: (meta('article:published_time', 'datepublished', 'pubdate', 'parsely-pub-date', 'dcterms.date', 'dc.date') || jsonLd.publishedDate || '').slice(0, 80),
                  modifiedDate: (meta('article:modified_time', 'datemodified', 'last-modified', 'dcterms.modified') || jsonLd.modifiedDate || '').slice(0, 80),
                  visibleTimeDate: (times[0]?.datetime || '').slice(0, 80),
                  visibleTimes: times,
                  siteName: meta('og:site_name').slice(0, 180),
                  description: meta('description', 'og:description', 'twitter:description').slice(0, 500)
                };
              })(),
              text: (document.body?.innerText || '').slice(0, 20000),
              links: Array.from(document.querySelectorAll('a[href]')).slice(0, 50).map(a => ({
                text: (a.innerText || a.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ').slice(0, 160),
                href: a.href
              })),
              buttons: Array.from(document.querySelectorAll('button,[role=button],input[type=button],input[type=submit]')).slice(0, 40).map(e => ({
                text: (e.innerText || e.value || e.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ').slice(0, 120),
                id: e.id || '',
                type: e.tagName.toLowerCase()
              })),
              fields: Array.from(document.querySelectorAll('input:not([type=password]):not([type=file]):not([type=hidden]),textarea,[contenteditable=true]')).slice(0, 40).map(e => ({
                tag: e.tagName.toLowerCase(),
                type: e.type || '',
                name: e.name || '',
                id: e.id || '',
                placeholder: e.placeholder || '',
                label: e.getAttribute('aria-label') || '',
                disabled: e.disabled === true
              })),
              formCount: document.forms.length
            }))()
            """;
        var json = await EvaluateAsync(page, expression, token);
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("The browser returned no page metadata.");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var pageUrl = root.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty;
        _ = ValidateHttpUrl(pageUrl);
        var metadata = root.TryGetProperty("metadata", out var metadataElement) ? metadataElement : default;
        string Meta(string name) => metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? string.Empty : string.Empty;
        var visibleTimesJson = metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty("visibleTimes", out var times) && times.ValueKind == JsonValueKind.Array
                ? times.GetRawText() : "[]";
        return new BrowserPageSnapshot(
            root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() ?? string.Empty : string.Empty,
            pageUrl,
            root.TryGetProperty("readyState", out var ready) && ready.ValueKind == JsonValueKind.String ? ready.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array ? links.GetRawText() : "[]",
            root.TryGetProperty("buttons", out var buttons) && buttons.ValueKind == JsonValueKind.Array ? buttons.GetRawText() : "[]",
            root.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array ? fields.GetRawText() : "[]",
            root.TryGetProperty("formCount", out var forms) && forms.TryGetInt32(out var formCount) ? formCount : 0,
            Meta("author"), Meta("publishedDate"), Meta("modifiedDate"), Meta("visibleTimeDate"),
            Meta("siteName"), Meta("description"), visibleTimesJson);
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

    private bool IsTrustedBrowserWebSocketUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != _debugPort ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var loopbackHost = uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
        return loopbackHost && uri.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal);
    }

    private string DebugEndpoint(string route) => $"http://127.0.0.1:{_debugPort}/{route}";

    private static string BuildPreviewExpression(string selector, string action)
    {
        var q = JsonSerializer.Serialize(selector);
        var requested = JsonSerializer.Serialize(action);
        return "(() => {const q=" + q + ",action=" + requested + ";" +
            "const matches=document.querySelectorAll(q);if(matches.length===0)return JSON.stringify({found:false,error:'No element matched the selector.'});" +
            "if(matches.length!==1)return JSON.stringify({found:false,error:'The selector matched multiple elements. Use a more specific selector; ambiguous actions are blocked.'});" +
            "const e=matches[0];const tag=e.tagName.toLowerCase(),type=(tag==='button'?(e.getAttribute('type')||''):(e.type||'')).toLowerCase(),disabled=e.disabled===true||e.getAttribute('aria-disabled')==='true',readOnly=e.readOnly===true;" +
            "const style=getComputedStyle(e),rect=e.getBoundingClientRect();" +
            "const visible=!!(e.getClientRects().length&&style.visibility!=='hidden'&&style.display!=='none'&&Number(style.opacity)>0.01&&style.pointerEvents!=='none'&&!e.closest('[aria-hidden=true]')&&rect.width>0&&rect.height>0&&rect.bottom>0&&rect.right>0&&rect.top<innerHeight&&rect.left<innerWidth);" +
            "const text=(e.innerText||e.getAttribute('aria-label')||'').trim().replace(/\\s+/g,' ').slice(0,160);" +
            "const label=e.getAttribute('aria-label')||e.getAttribute('name')||e.getAttribute('placeholder')||'',role=e.getAttribute('role')||'',formAssociated=!!(e.closest('form')||e.form),accept=tag==='input'?(e.getAttribute('accept')||''):'',multiple=tag==='input'&&e.multiple===true;" +
            "const fp=JSON.stringify({tag,id:e.id||'',name:e.getAttribute('name')||'',type,label,text,href:e.href||'',role,formAssociated,accept,multiple});" +
            "if(!visible)return JSON.stringify({found:false,error:'The selected element is not visible, is outside the viewport, or is hidden from assistive technology.'});" +
            "if(disabled)return JSON.stringify({found:false,error:'The selected element is disabled.'});" +
            "if(action==='click'){const clickable=tag==='button'||tag==='a'||e.getAttribute('role')==='button'||(tag==='input'&&['button','submit','reset'].includes(type))||tag==='summary';if(!clickable)return JSON.stringify({found:false,error:'Selector must match a visible link, button, role=button, submit/button input, or summary.'});" +
            "if(tag==='a'&&(!e.href||!/^https?:/i.test(e.href)))return JSON.stringify({found:false,error:'Only anchors with HTTP/HTTPS destinations are allowed for browser clicks.'});}" +
            "if(action==='fill'){const allowed=(tag==='textarea')||(tag==='input'&&['text','email','search','tel','url','number'].includes(type))||e.isContentEditable;if(!allowed||readOnly)return JSON.stringify({found:false,error:'Selector must match an editable visible text/email/search/tel/url/number input, textarea, or contenteditable field. Password/file/hidden fields are not supported.'});}" +
            "if(action==='upload'&&(tag!=='input'||type!=='file'))return JSON.stringify({found:false,error:'Uploads require a visible input[type=file].'});" +
            "return JSON.stringify({found:true,url:location.href,tag,type,id:e.id||'',name:e.getAttribute('name')||'',label,text,href:e.href||'',role,formAssociated,disabled,readOnly,accept,multiple,fingerprint:fp});})()";
    }

    private async Task<int> ResolveApprovedUploadNodeIdAsync(
        CdpPageTarget page,
        BrowserElementPreview preview,
        CancellationToken token)
    {
        // Hold a live DOM object from the same synchronous check that revalidates the consent
        // fingerprint. If the page replaces this node afterward, CDP sees a detached node rather
        // than a new element that happens to reuse the CSS selector.
        var selector = JsonSerializer.Serialize(preview.Selector);
        var expectedUrl = JsonSerializer.Serialize(preview.PageUrl);
        var expectedFingerprint = JsonSerializer.Serialize(preview.Fingerprint);
        var expression =
            "(() => {const q=" + selector + ",expectedUrl=" + expectedUrl + ",expectedFp=" + expectedFingerprint + ";" +
            "if(location.href!==expectedUrl)throw new Error('The page URL changed while awaiting upload approval.');" +
            "const matches=document.querySelectorAll(q);if(matches.length!==1)throw new Error('The approved file input is no longer unique.');" +
            "const e=matches[0],tag=e.tagName.toLowerCase(),type=(e.type||'').toLowerCase();" +
            "const text=(e.innerText||e.getAttribute('aria-label')||'').trim().split(/\\s+/).join(' ').slice(0,160);" +
            "const label=e.getAttribute('aria-label')||e.getAttribute('name')||e.getAttribute('placeholder')||'',role=e.getAttribute('role')||'',formAssociated=!!(e.closest('form')||e.form),accept=e.getAttribute('accept')||'',multiple=e.multiple===true;" +
            "const fp=JSON.stringify({tag,id:e.id||'',name:e.getAttribute('name')||'',type,label,text,href:e.href||'',role,formAssociated,accept,multiple});" +
            "if(fp!==expectedFp||tag!=='input'||type!=='file')throw new Error('The approved file input changed while awaiting upload approval.');" +
            "const style=getComputedStyle(e),rect=e.getBoundingClientRect();" +
            "if(e.disabled===true||e.getAttribute('aria-disabled')==='true'||(e.files&&e.files.length>0)||!e.getClientRects().length||style.visibility==='hidden'||style.display==='none'||Number(style.opacity)<=0.01||style.pointerEvents==='none'||e.closest('[aria-hidden=true]')||rect.width<=0||rect.height<=0||rect.bottom<=0||rect.right<=0||rect.top>=innerHeight||rect.left>=innerWidth)throw new Error('The approved file input is no longer visible, enabled, and empty.');" +
            "return e;})()";

        _ = await SendCommandAsync(page, "DOM.getDocument", new JsonObject
        {
            ["depth"] = 1,
            ["pierce"] = true
        }, token);

        var evaluation = await SendCommandAsync(page, "Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = false,
            ["awaitPromise"] = false
        }, token);
        if (evaluation["result"]?["exceptionDetails"] is JsonObject exception)
        {
            var message = exception["exception"]?["description"]?.GetValue<string>() ??
                exception["text"]?.GetValue<string>() ?? "The approved file input changed.";
            throw new InvalidOperationException("No file was selected: " + message);
        }

        var objectId = evaluation["result"]?["result"]?["objectId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(objectId))
            throw new InvalidOperationException("The browser did not return a live reference to the approved file input.");

        var request = await SendCommandAsync(page, "DOM.requestNode", new JsonObject
        {
            ["objectId"] = objectId
        }, token);
        var nodeId = request["result"]?["nodeId"]?.GetValue<int>() ?? 0;
        if (nodeId <= 0)
            throw new InvalidOperationException("The approved file input was detached before local file selection.");
        return nodeId;
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
            "const e=matches[0];const tag=e.tagName.toLowerCase(),type=(tag==='button'?(e.getAttribute('type')||''):(e.type||'')).toLowerCase();" +
            "const text=(e.innerText||e.getAttribute('aria-label')||'').trim().replace(/\\s+/g,' ').slice(0,160);" +
            "const label=e.getAttribute('aria-label')||e.getAttribute('name')||e.getAttribute('placeholder')||'',role=e.getAttribute('role')||'',formAssociated=!!(e.closest('form')||e.form),accept=tag==='input'?(e.getAttribute('accept')||''):'',multiple=tag==='input'&&e.multiple===true;" +
            "if(action==='click'&&((['button','input'].includes(tag)&&['submit','reset'].includes(type))||formAssociated))return JSON.stringify({ok:false,error:'Form-associated controls and submit/reset actions are blocked by browser_click.'});" +
            "const fp=JSON.stringify({tag,id:e.id||'',name:e.getAttribute('name')||'',type,label,text,href:e.href||'',role,formAssociated,accept,multiple});" +
            "if(fp!==expectedFp)return JSON.stringify({ok:false,error:'The selected element changed while awaiting approval. Inspect the page again.'});" +
            "const style=getComputedStyle(e),rect=e.getBoundingClientRect();" +
            "const visible=!!(e.getClientRects().length&&style.visibility!=='hidden'&&style.display!=='none'&&Number(style.opacity)>0.01&&style.pointerEvents!=='none'&&!e.closest('[aria-hidden=true]')&&rect.width>0&&rect.height>0&&rect.bottom>0&&rect.right>0&&rect.top<innerHeight&&rect.left<innerWidth);" +
            "if(!visible||e.disabled===true||e.getAttribute('aria-disabled')==='true')return JSON.stringify({ok:false,error:'The selected element is hidden, outside the viewport, or disabled.'});" +
            "if(action==='click'){const clickable=tag==='button'||tag==='a'||e.getAttribute('role')==='button'||(tag==='input'&&['button','submit','reset'].includes(type))||tag==='summary';if(!clickable)return JSON.stringify({ok:false,error:'The element is no longer an allowed click target.'});e.click();return JSON.stringify({ok:true,action:'click',tag,text});}" +
            "if(action==='upload'){if(tag!=='input'||type!=='file'||(e.files&&e.files.length>0))return JSON.stringify({ok:false,error:'The approved target is no longer an empty file input.'});return JSON.stringify({ok:true,action:'upload',accept,multiple});}" +
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
    bool ReadOnly,
    string Accept = "",
    bool Multiple = false);

public sealed record BrowserPageSnapshot(
    string Title,
    string Url,
    string ReadyState,
    string Text,
    string LinksJson,
    string ButtonsJson,
    string FieldsJson,
    int FormCount,
    string Author = "",
    string PublishedDate = "",
    string ModifiedDate = "",
    string VisibleTimeDate = "",
    string SiteName = "",
    string Description = "",
    string VisibleTimesJson = "[]");

internal sealed record CdpPageTarget(string Id, string Title, string Url, string WebSocketUrl);

public sealed record BrowserSearchSnapshot(
    string Query,
    string SearchUrl,
    string FinalUrl,
    string Title,
    string Status,
    string Notice,
    string VisibleText,
    string LinksJson);

public sealed record BrowserPageIdentity(
    string PageTargetId,
    string Url,
    string Title,
    string ReadyState);
