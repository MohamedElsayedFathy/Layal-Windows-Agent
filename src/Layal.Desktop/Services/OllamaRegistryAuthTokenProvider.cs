using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
        CancellationToken cancellationToken,
        string? userProfileDirectory = null)
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
        var authorization = CreateOllamaSignatureAuthorization(tokenUri, userProfileDirectory);

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
        // Match Go's url.Values.Encode() used by Ollama's registry challenge:
        // keys are sorted ordinally and values are URL-encoded using form-query rules.
        var values = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var existingQuery = realm.Query.TrimStart('?');
        if (!string.IsNullOrEmpty(existingQuery))
        {
            foreach (var pair in existingQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                var key = DecodeQueryComponent(separator < 0 ? pair : pair[..separator]);
                var value = DecodeQueryComponent(separator < 0 ? string.Empty : pair[(separator + 1)..]);
                AddQueryValue(values, key, value);
            }
        }

        AddQueryValue(values, "service", service);
        foreach (var item in scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            AddQueryValue(values, "scope", item);

        var nonceBytes = RandomNumberGenerator.GetBytes(16);
        var nonce = Convert.ToBase64String(nonceBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        AddQueryValue(
            values,
            "ts",
            timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddQueryValue(values, "nonce", nonce);

        var queryParts = new List<string>();
        foreach (var entry in values)
        {
            foreach (var value in entry.Value)
                queryParts.Add(EncodeQueryComponent(entry.Key) + "=" + EncodeQueryComponent(value));
        }

        var builder = new UriBuilder(realm)
        {
            Query = string.Join("&", queryParts)
        };
        return builder.Uri;
    }

    private static void AddQueryValue(
        SortedDictionary<string, List<string>> values,
        string key,
        string value)
    {
        if (!values.TryGetValue(key, out var list))
        {
            list = new List<string>();
            values.Add(key, list);
        }

        list.Add(value);
    }

    private static string DecodeQueryComponent(string value) =>
        Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));

    private static string EncodeQueryComponent(string value) =>
        Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);

    private static string CreateOllamaSignatureAuthorization(
        Uri tokenUri,
        string? userProfileDirectory)
    {
        var profileDirectory = string.IsNullOrWhiteSpace(userProfileDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(userProfileDirectory);
        var keyPath = Path.Combine(
            profileDirectory,
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
        // Ollama's Go client sends ssh.Signature.Blob, not the raw 64-byte
        // Ed25519 signature. The SSH blob is two length-prefixed strings:
        // "ssh-ed25519" followed by the raw signature bytes.
        var rawSignature = keyFile.Key.Sign(signedPayload);
        if (rawSignature.Length != 64)
            throw new InvalidDataException("The local Ollama Ed25519 key produced an invalid signature length.");

        using var signatureStream = new MemoryStream();
        WriteSshString(signatureStream, Encoding.ASCII.GetBytes("ssh-ed25519"));
        WriteSshString(signatureStream, rawSignature);
        var signatureBlob = signatureStream.ToArray();

        // Only the public-key blob and SSH-formatted signature are transmitted.
        // Neither private key bytes nor PEM data leave this process.
        return Convert.ToBase64String(signer.Data) + ":" + Convert.ToBase64String(signatureBlob);
    }

    private static void WriteSshString(Stream destination, byte[] value)
    {
        var length = (uint)value.Length;
        destination.WriteByte((byte)(length >> 24));
        destination.WriteByte((byte)(length >> 16));
        destination.WriteByte((byte)(length >> 8));
        destination.WriteByte((byte)length);
        destination.Write(value, 0, value.Length);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
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
