using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Layal.Services;

/// <summary>
/// Implements Ollama's registry challenge flow using the private key generated
/// and owned by the local Ollama runtime. The key is read locally and is never
/// sent; only Ollama's signed challenge response is sent to the same HTTPS host.
/// </summary>
internal static class OllamaRegistryAuthTokenProvider
{
    private const int MaximumTokenResponseBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static async Task<string> GetBearerTokenAsync(
        HttpClient httpClient,
        Uri registryUri,
        string challengeHeader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(registryUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeHeader);

        var realm = ReadChallengeValue(challengeHeader, "realm");
        var service = ReadChallengeValue(challengeHeader, "service");
        var scope = ReadChallengeValue(challengeHeader, "scope");

        if (!Uri.TryCreate(realm, UriKind.Absolute, out var realmUri) ||
            !string.Equals(realmUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(realmUri.UserInfo) ||
            !string.IsNullOrEmpty(realmUri.Fragment) ||
            !string.Equals(realmUri.Authority, registryUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Ollama Registry supplied a token realm outside the exact HTTPS registry authority.");
        }

        var tokenUri = BuildTokenUri(realmUri, service, scope);
        var authorization = CreateOllamaSignatureAuthorization(tokenUri);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, tokenUri);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                $"Ollama Registry token request returned HTTP {(int)response.StatusCode}.");

        var body = await ReadBoundedAsync(
            response.Content,
            MaximumTokenResponseBytes,
            timeout.Token).ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("token", out var tokenElement) ||
            tokenElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Ollama Registry returned a malformed token response.");
        }

        var token = tokenElement.GetString();
        if (string.IsNullOrWhiteSpace(token) || token.Length > 32 * 1024)
            throw new InvalidDataException("Ollama Registry returned an empty or oversized bearer token.");

        return token;
    }

    private static string ReadChallengeValue(string challengeHeader, string name)
    {
        if (!challengeHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ollama Registry returned an unsupported authentication challenge.");

        var expression = "(?:^|,)\\s*" + Regex.Escape(name) + "\\s*=\\s*\"(?<value>[^\"]*)\"";
        var match = Regex.Match(
            challengeHeader["Bearer ".Length..],
            expression,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups["value"].Value))
            throw new InvalidDataException("Ollama Registry authentication challenge is missing " + name + ".");

        return match.Groups["value"].Value;
    }

    private static Uri BuildTokenUri(Uri realm, string service, string scope)
    {
        var builder = new UriBuilder(realm);
        var query = new System.Collections.Generic.List<string>();
        var existingQuery = builder.Query.TrimStart('?');
        if (!string.IsNullOrEmpty(existingQuery))
            query.Add(existingQuery);

        query.Add("service=" + Uri.EscapeDataString(service));
        foreach (var item in scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            query.Add("scope=" + Uri.EscapeDataString(item));

        var nonceBytes = RandomNumberGenerator.GetBytes(16);
        var nonce = Convert.ToBase64String(nonceBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        query.Add("ts=" + timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        query.Add("nonce=" + Uri.EscapeDataString(nonce));
        builder.Query = string.Join("&", query);

        return builder.Uri;
    }

    private static string CreateOllamaSignatureAuthorization(Uri tokenUri)
    {
        var keyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ollama",
            "id_ed25519");

        if (!File.Exists(keyPath) ||
            (File.GetAttributes(keyPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The local Ollama signing key is not available as a regular file. Restart Ollama to initialize its keypair, then retry model setup.");
        }

        var keyInfo = new FileInfo(keyPath);
        if (keyInfo.Length <= 0 || keyInfo.Length > 16 * 1024)
            throw new InvalidDataException("The local Ollama signing key has an invalid file size.");

        using var keyStream = new FileStream(
            keyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        using var keyFile = new PrivateKeyFile(keyStream);
        var signer = keyFile.HostKeyAlgorithms.SingleOrDefault(
            item => string.Equals(item.Name, "ssh-ed25519", StringComparison.Ordinal));

        if (signer is null)
            throw new InvalidDataException("The local Ollama key is not an Ed25519 signing key.");

        var emptyBodyHashHex = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()))
            .ToLowerInvariant();
        var encodedEmptyBodyHash = Convert.ToBase64String(Encoding.UTF8.GetBytes(emptyBodyHashHex));
        var signedPayload = Encoding.UTF8.GetBytes(
            "GET," + tokenUri.AbsoluteUri + "," + encodedEmptyBodyHash);
        var signedBlob = signer.Sign(signedPayload);

        // Ollama's registry protocol uses the base64 SSH public-key blob followed
        // by the base64 SSH signature blob. Neither private key bytes nor PEM data
        // leave this process.
        return Convert.ToBase64String(signer.Data) + ":" + Convert.ToBase64String(signedBlob);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var contentLength &&
            contentLength > maximumBytes)
            throw new InvalidDataException("Ollama Registry token response exceeds the allowed size.");

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Ollama Registry token response exceeds the allowed size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return output.ToArray();
    }
}
