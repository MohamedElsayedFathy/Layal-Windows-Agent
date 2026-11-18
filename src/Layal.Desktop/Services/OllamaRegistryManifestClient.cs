using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

/// <summary>
/// Resolves the configured Qwen tag directly from the official Ollama Registry.
/// The returned digest is calculated from the exact HTTPS manifest response bytes.
/// </summary>
public sealed class OllamaRegistryManifestClient
{
    public const string RegistryHost = "registry.ollama.ai";
    public static readonly Uri DefaultManifestUri =
        new("https://registry.ollama.ai/v2/library/qwen3.5/manifests/2b");

    private const int MaximumManifestBytes = 1024 * 1024;
    private const string DockerManifestMediaType = "application/vnd.docker.distribution.manifest.v2+json";
    private const string OllamaManifestListMediaType = "application/vnd.ollama.manifest.list.v2+json";

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(12),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _httpClient;
    private readonly Func<Uri, string, CancellationToken, Task<string>> _getBearerTokenAsync;

    public OllamaRegistryManifestClient(
        HttpClient? httpClient = null,
        Func<Uri, string, CancellationToken, Task<string>>? getBearerTokenAsync = null)
    {
        _httpClient = httpClient ?? SharedClient;
        _getBearerTokenAsync = getBearerTokenAsync ??
            ((registryUri, challenge, cancellationToken) =>
                OllamaRegistryAuthTokenProvider.GetBearerTokenAsync(
                    _httpClient, registryUri, challenge, cancellationToken));
    }

    public async Task<OllamaRegistryManifestSnapshot> FetchDefaultModelManifestAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        using var response = await SendManifestRequestAsync(timeout.Token).ConfigureAwait(false);

        // Redirects are intentionally not followed: the trust boundary is the exact
        // HTTPS Ollama Registry host and a tag must not silently move to another host.
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                $"Ollama Registry manifest request returned HTTP {(int)response.StatusCode}; redirects and non-200 responses are rejected.");

        if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
            throw new InvalidDataException("The Ollama Registry manifest exceeds the allowed size.");

        var body = await ReadBoundedAsync(response.Content, MaximumManifestBytes, cancellationToken)
            .ConfigureAwait(false);
        var mediaType = ValidateManifestBody(body);

        var computedDigest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var registryDigest = TryReadRegistryDigest(response);
        if (registryDigest is not null &&
            !OllamaModelDigestPolicy.Matches(registryDigest, computedDigest))
        {
            throw new InvalidDataException(
                "Ollama Registry Docker-Content-Digest does not match the returned manifest bytes.");
        }

        return new OllamaRegistryManifestSnapshot(
            "qwen3.5:2b",
            DefaultManifestUri.AbsoluteUri,
            computedDigest,
            mediaType,
            body.LongLength,
            DateTimeOffset.UtcNow);
    }

    private async Task<HttpResponseMessage> SendManifestRequestAsync(
        CancellationToken cancellationToken)
    {
        using var request = CreateManifestRequest();
        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        var challenge = response.Headers.WwwAuthenticate
            .FirstOrDefault(item => string.Equals(item.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            ?.ToString();
        response.Dispose();

        if (string.IsNullOrWhiteSpace(challenge))
            throw new InvalidDataException("Ollama Registry returned 401 without a Bearer challenge.");

        var bearerToken = await _getBearerTokenAsync(
            DefaultManifestUri,
            challenge,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new InvalidDataException("Ollama Registry authentication returned an empty token.");

        using var authenticatedRequest = CreateManifestRequest();
        authenticatedRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        return await _httpClient.SendAsync(
            authenticatedRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateManifestRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, DefaultManifestUri);
        request.Headers.Accept.ParseAdd(OllamaManifestListMediaType);
        request.Headers.Accept.ParseAdd(DockerManifestMediaType);
        request.Headers.Accept.ParseAdd("application/vnd.oci.image.index.v1+json");
        request.Headers.Accept.ParseAdd("application/vnd.oci.image.manifest.v1+json");
        request.Headers.AcceptEncoding.ParseAdd("identity");
        return request;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("The Ollama Registry manifest exceeds the allowed size.");

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (output.Length == 0)
            throw new InvalidDataException("Ollama Registry returned an empty manifest.");

        return output.ToArray();
    }

    private static string ValidateManifestBody(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var version) ||
            version != 2 ||
            !root.TryGetProperty("mediaType", out var mediaTypeElement) ||
            mediaTypeElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Ollama Registry returned an unsupported manifest document.");
        }

        var mediaType = mediaTypeElement.GetString() ?? string.Empty;
        if (string.Equals(mediaType, OllamaManifestListMediaType, StringComparison.Ordinal))
        {
            if (!root.TryGetProperty("manifests", out var manifests) ||
                manifests.ValueKind != JsonValueKind.Array ||
                manifests.GetArrayLength() == 0)
                throw new InvalidDataException("Ollama Registry returned an empty manifest list.");

            return mediaType;
        }

        if (string.Equals(mediaType, DockerManifestMediaType, StringComparison.Ordinal) ||
            string.Equals(mediaType, "application/vnd.oci.image.manifest.v1+json", StringComparison.Ordinal))
        {
            if (!root.TryGetProperty("config", out var config) ||
                config.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("layers", out var layers) ||
                layers.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Ollama Registry returned an incomplete model manifest.");

            return mediaType;
        }

        if (string.Equals(mediaType, "application/vnd.oci.image.index.v1+json", StringComparison.Ordinal))
        {
            if (!root.TryGetProperty("manifests", out var manifests) ||
                manifests.ValueKind != JsonValueKind.Array ||
                manifests.GetArrayLength() == 0)
                throw new InvalidDataException("Ollama Registry returned an empty OCI manifest index.");

            return mediaType;
        }

        throw new InvalidDataException("Ollama Registry returned an unrecognized manifest media type.");
    }

    private static string? TryReadRegistryDigest(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Docker-Content-Digest", out var values))
            return null;

        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
            return null;

        var value = enumerator.Current.Trim();
        if (enumerator.MoveNext())
            throw new InvalidDataException("Ollama Registry returned multiple Docker-Content-Digest values.");

        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ollama Registry returned a non-SHA-256 manifest digest.");

        var digest = value[prefix.Length..];
        if (!OllamaModelDigestPolicy.IsValidSha256Digest(digest))
            throw new InvalidDataException("Ollama Registry returned an invalid manifest digest.");

        return digest.ToLowerInvariant();
    }
}

public sealed record OllamaRegistryManifestSnapshot(
    string ModelId,
    string ManifestUri,
    string Digest,
    string MediaType,
    long ManifestSizeBytes,
    DateTimeOffset RetrievedAtUtc);
