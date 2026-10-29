
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Layal.Models;

namespace Layal.Services;

/// <summary>
/// User-initiated Gmail reader using Google's installed-app OAuth flow, PKCE, loopback callback,
/// gmail.readonly scope, and DPAPI-protected refresh-token persistence. It never sends/deletes mail.
/// </summary>
public sealed class GmailReadOnlyService
{
    public const int MaximumListedMessages = 25;
    public const int MaximumBodyCharacters = 15_000;
    private const string GmailReadOnlyScope = "https://www.googleapis.com/auth/gmail.readonly";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly ProtectedJsonStore<GmailOAuthCredential> _credentialStore;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _cachedAccessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public GmailReadOnlyService(string credentialPath, HttpClient? httpClient = null)
    {
        _credentialStore = new ProtectedJsonStore<GmailOAuthCredential>(credentialPath);
        _http = httpClient ?? SharedHttpClient;
    }

    public bool IsConnected
    {
        get
        {
            var credential = _credentialStore.Load(new GmailOAuthCredential());
            return !string.IsNullOrWhiteSpace(credential.ClientId) &&
                !string.IsNullOrWhiteSpace(credential.RefreshToken);
        }
    }

    public static string ValidateOAuthClientId(string clientId)
    {
        var value = (clientId ?? string.Empty).Trim();
        if (value.Length is < 20 or > 300 ||
            !value.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) ||
            value.Any(char.IsControl) || value.Contains(' ') || value.Contains('\n') || value.Contains('\r'))
        {
            throw new InvalidOperationException(
                "Enter a Google OAuth 2.0 Client ID for a Desktop app (ending in .apps.googleusercontent.com). Do not enter a client secret or an API key.");
        }
        return value;
    }

    public static string CreatePkceVerifier()
    {
        // 32 random bytes => a verifier within RFC 7636's 43–128 character range after base64url encoding.
        return Base64Url(RandomNumberGenerator.GetBytes(32));
    }

    public static string CreatePkceChallenge(string verifier)
    {
        if (string.IsNullOrWhiteSpace(verifier) || verifier.Length is < 43 or > 128 ||
            verifier.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~')))
        {
            throw new ArgumentException("The PKCE verifier is invalid.", nameof(verifier));
        }

        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    public async Task AuthorizeAsync(string clientId, CancellationToken cancellationToken = default)
    {
        clientId = ValidateOAuthClientId(clientId);
        cancellationToken.ThrowIfCancellationRequested();

        var previous = _credentialStore.Load(new GmailOAuthCredential());
        var verifier = CreatePkceVerifier();
        var challenge = CreatePkceChallenge(verifier);
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));

        // Reserve an ephemeral IPv4 loopback port, then bind an HTTP listener only to 127.0.0.1.
        // Google supports dynamic loopback ports for installed desktop applications.
        int port;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }

        var redirectUri = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = GmailReadOnlyScope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "true",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        var authorizationUrl = AuthorizationEndpoint + "?" +
            string.Join("&", query.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

        try
        {
            Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            listener.Stop();
            throw new InvalidOperationException(
                "Could not open the system browser. No Gmail token was saved.", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Google sign-in timed out after three minutes. Try connecting again.");
        }

        string? code = null;
        try
        {
            if (context.Request.RemoteEndPoint?.Address is not { } remote ||
                !IPAddress.IsLoopback(remote) ||
                context.Request.HttpMethod != "GET" ||
                context.Request.Url?.AbsolutePath != "/")
            {
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                await WriteCallbackResponseAsync(context, "This local callback was rejected.", cancellationToken);
                throw new InvalidOperationException("The Gmail OAuth callback did not originate from a valid loopback request.");
            }

            var returnedState = context.Request.QueryString["state"] ?? string.Empty;
            if (!FixedTimeEquals(state, returnedState))
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteCallbackResponseAsync(context, "The sign-in state check failed. You can close this tab.", cancellationToken);
                throw new InvalidOperationException("Google sign-in state validation failed. No Gmail token was saved.");
            }

            var providerError = context.Request.QueryString["error"];
            if (!string.IsNullOrWhiteSpace(providerError))
            {
                await WriteCallbackResponseAsync(context, "Google sign-in was not completed. You can close this tab.", cancellationToken);
                throw new InvalidOperationException("Google sign-in was declined or cancelled (" + Truncate(providerError, 80) + ").");
            }

            code = context.Request.QueryString["code"];
            if (string.IsNullOrWhiteSpace(code) || code.Length > 4096)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteCallbackResponseAsync(context, "No valid sign-in code was returned. You can close this tab.", cancellationToken);
                throw new InvalidOperationException("Google did not return a valid authorization code.");
            }

            await WriteCallbackResponseAsync(context,
                "Gmail connection confirmed for Layal. You may close this tab and return to Layal.", cancellationToken);
        }
        finally
        {
            listener.Stop();
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["code"] = code!,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri
            })
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                "Google could not exchange the authorization code. Check the Desktop OAuth client configuration and try again.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        var refreshToken = GetJsonString(root, "refresh_token");
        if (string.IsNullOrWhiteSpace(refreshToken))
            refreshToken = string.Equals(previous.ClientId, clientId, StringComparison.Ordinal)
                ? previous.RefreshToken
                : string.Empty;
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException(
                "Google did not return a refresh token. Revoke this app in Google Account security, then connect again and approve the read-only Gmail scope.");

        _credentialStore.Save(new GmailOAuthCredential
        {
            ClientId = clientId,
            RefreshToken = refreshToken,
            ConnectedAt = DateTimeOffset.Now
        });
        _cachedAccessToken = null;
        _accessTokenExpiresAt = DateTimeOffset.MinValue;
    }

    public void Disconnect()
    {
        _credentialStore.Delete();
        _cachedAccessToken = null;
        _accessTokenExpiresAt = DateTimeOffset.MinValue;
    }

    public async Task<IReadOnlyList<GmailMessageSummary>> ListMessagesAsync(
        string query = "newer_than:30d",
        int maxResults = 12,
        CancellationToken cancellationToken = default)
    {
        query = string.Join(" ", (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (query.Length > 250 || query.Any(char.IsControl))
            throw new InvalidOperationException("Gmail search text is limited to 250 printable characters.");
        maxResults = Math.Clamp(maxResults, 1, MaximumListedMessages);

        var url = "https://gmail.googleapis.com/gmail/v1/users/me/messages?maxResults=" +
            maxResults.ToString(CultureInfo.InvariantCulture) + "&q=" + Uri.EscapeDataString(query);
        using var response = await SendGmailRequestAsync(HttpMethod.Get, url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateGmailApiExceptionAsync(response, "Could not list Gmail messages");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("messages", out var messages) ||
            messages.ValueKind != JsonValueKind.Array)
            return Array.Empty<GmailMessageSummary>();

        var output = new List<GmailMessageSummary>();
        foreach (var message in messages.EnumerateArray().Take(maxResults))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = GetJsonString(message, "id");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
                continue;

            var detailUrl = "https://gmail.googleapis.com/gmail/v1/users/me/messages/" +
                Uri.EscapeDataString(id) + "?format=metadata&metadataHeaders=From&metadataHeaders=Subject&metadataHeaders=Date";
            using var detailResponse = await SendGmailRequestAsync(HttpMethod.Get, detailUrl, cancellationToken);
            if (!detailResponse.IsSuccessStatusCode)
                continue;

            using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync(cancellationToken));
            var root = detail.RootElement;
            var headers = GetHeaders(root);
            output.Add(new GmailMessageSummary(
                id,
                GetJsonString(root, "threadId"),
                HeaderValue(headers, "From"),
                HeaderValue(headers, "Subject"),
                Truncate(GetJsonString(root, "snippet"), 500),
                ParseInternalDate(GetJsonString(root, "internalDate"))));
        }

        return output;
    }

    public async Task<GmailMessageDetail> ReadMessageAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 200 ||
            messageId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidOperationException("The selected Gmail message ID is invalid.");

        var url = "https://gmail.googleapis.com/gmail/v1/users/me/messages/" +
            Uri.EscapeDataString(messageId) + "?format=full";
        using var response = await SendGmailRequestAsync(HttpMethod.Get, url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateGmailApiExceptionAsync(response, "Could not read the selected Gmail message");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        var headers = GetHeaders(root);
        var body = new StringBuilder();
        if (root.TryGetProperty("payload", out var payload))
            AppendPlainTextParts(payload, body);
        var text = Truncate(body.ToString().Trim(), MaximumBodyCharacters);
        if (text.Length == 0)
            text = "(No plain-text body was available. HTML-only content and attachments are not rendered.)";

        return new GmailMessageDetail(
            messageId,
            GetJsonString(root, "threadId"),
            HeaderValue(headers, "From"),
            HeaderValue(headers, "To"),
            HeaderValue(headers, "Subject"),
            Truncate(GetJsonString(root, "snippet"), 500),
            ParseInternalDate(GetJsonString(root, "internalDate")),
            text);
    }

    private async Task<HttpResponseMessage> SendGmailRequestAsync(
        HttpMethod method,
        string url,
        CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return await _http.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedAccessToken) &&
            _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return _cachedAccessToken;

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedAccessToken) &&
                _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                return _cachedAccessToken;

            var credential = _credentialStore.Load(new GmailOAuthCredential());
            if (string.IsNullOrWhiteSpace(credential.ClientId) ||
                string.IsNullOrWhiteSpace(credential.RefreshToken))
                throw new InvalidOperationException("Connect Gmail from Layal first. No Gmail credential is stored.");

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = credential.ClientId,
                    ["refresh_token"] = credential.RefreshToken,
                    ["grant_type"] = "refresh_token"
                })
            };
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    "Google rejected the stored Gmail authorization. Disconnect Gmail, then connect it again from Settings.");

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var accessToken = GetJsonString(json.RootElement, "access_token");
            var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expiry) &&
                expiry.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 60, 86_400) : 3600;
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException("Google returned an empty access token.");

            var rotatedRefreshToken = GetJsonString(json.RootElement, "refresh_token");
            if (!string.IsNullOrWhiteSpace(rotatedRefreshToken) &&
                !string.Equals(rotatedRefreshToken, credential.RefreshToken, StringComparison.Ordinal))
            {
                credential.RefreshToken = rotatedRefreshToken;
                _credentialStore.Save(credential);
            }

            _cachedAccessToken = accessToken;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return accessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static async Task<InvalidOperationException> CreateGmailApiExceptionAsync(
        HttpResponseMessage response,
        string operation)
    {
        var detail = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "The Gmail authorization expired or was revoked. Reconnect Gmail.",
            HttpStatusCode.Forbidden => "Google denied Gmail read access. Check account consent and Gmail API configuration.",
            HttpStatusCode.NotFound => "The selected Gmail message was not found.",
            (HttpStatusCode)429 => "Google rate-limited this request. Try again shortly.",
            _ => "Google returned HTTP " + (int)response.StatusCode + "."
        };
        return new InvalidOperationException(operation + ". " + detail);
    }

    private static Dictionary<string, string> GetHeaders(JsonElement root)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("payload", out var payload) &&
            payload.TryGetProperty("headers", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var name = GetJsonString(item, "name");
                if (name.Length > 0 && !headers.ContainsKey(name))
                    headers[name] = Truncate(GetJsonString(item, "value"), 500);
            }
        }
        return headers;
    }

    private static string HeaderValue(IReadOnlyDictionary<string, string> headers, string key) =>
        headers.TryGetValue(key, out var value) ? value : string.Empty;

    private static void AppendPlainTextParts(JsonElement part, StringBuilder builder)
    {
        if (builder.Length >= MaximumBodyCharacters)
            return;

        var filename = GetJsonString(part, "filename");
        var mimeType = GetJsonString(part, "mimeType");
        if (filename.Length == 0 && string.Equals(mimeType, "text/plain", StringComparison.OrdinalIgnoreCase) &&
            part.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
        {
            var data = GetJsonString(body, "data");
            if (data.Length > 0)
            {
                try
                {
                    var decoded = Encoding.UTF8.GetString(FromBase64Url(data));
                    if (builder.Length > 0) builder.AppendLine().AppendLine("---");
                    builder.Append(decoded.AsSpan(0, Math.Min(decoded.Length, MaximumBodyCharacters - builder.Length)));
                }
                catch (FormatException) { /* Ignore malformed MIME data instead of displaying corrupted text. */ }
            }
        }

        if (part.TryGetProperty("parts", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                if (builder.Length >= MaximumBodyCharacters) break;
                AppendPlainTextParts(child, builder);
            }
        }
    }

    private static DateTimeOffset? ParseInternalDate(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;

    private static string GetJsonString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        return Convert.FromBase64String(base64);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static async Task WriteCallbackResponseAsync(
        HttpListenerContext context,
        string message,
        CancellationToken token)
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Layal Gmail</title></head>" +
            "<body style=\"font-family:Segoe UI,sans-serif;max-width:40rem;margin:4rem auto;line-height:1.5\"><h2>" +
            WebUtility.HtmlEncode(message) + "</h2></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        try
        {
            await context.Response.OutputStream.WriteAsync(bytes, token);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static string Truncate(string value, int maximum) =>
        string.IsNullOrEmpty(value) || value.Length <= maximum ? value : value[..maximum] + "…";
}
