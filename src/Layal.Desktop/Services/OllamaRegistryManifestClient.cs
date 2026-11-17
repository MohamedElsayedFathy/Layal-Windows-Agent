using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

/// <summary>
/// Reads the public Qwen model manifest from the official Ollama registry.
/// It deliberately sends no Ollama account credentials and never follows redirects.
/// </summary>
public sealed class OllamaRegistryManifestClient
{
    private const string ManifestListMediaType = "application/vnd.ollama.manifest.list.v2+json";
    private const string ManifestMediaType = "application/vnd.docker.distribution.manifest.v2+json";
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private static readonly Uri ManifestUri =
        new("https://registry.ollama.ai/v2/library/qwen3.5/manifests/2b");
    private static readonly HttpClient SharedClient = CreateHttpClient();

    private readonly HttpClient _httpClient;

    public OllamaRegistryManifestClient()
        : this(SharedClient)
    {
    }

    public OllamaRegistryManifestClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<OllamaRegistryManifestSnapshot> GetDefaultModelManifestAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUri);
        request.Headers.TryAddWithoutValidation(
            "Accept",
            ManifestListMediaType + ", " + ManifestMediaType);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.UserAgent.ParseAdd("Layal-Windows-Agent/0.5.8");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "لم أستطع قراءة manifest نموذج Qwen من سجل Ollama الرسمي دون تسجيل دخول. " +
                "أوقفت التثبيت بدل الاعتماد على أول بصمة محلية غير موثقة.");
        }

        if (response.StatusCode == HttpStatusCode.MovedPermanently ||
            response.StatusCode == HttpStatusCode.Redirect ||
            response.StatusCode == HttpStatusCode.TemporaryRedirect ||
            response.StatusCode == HttpStatusCode.PermanentRedirect)
        {
            throw new InvalidDataException(
                "سجل Ollama أعاد تحويلًا غير متوقع عند طلب manifest النموذج؛ تم رفض التحويل.");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException(
                $"Official Ollama registry manifest request failed with HTTP {(int)response.StatusCode}.");
        }

        var body = await ReadBoundedAsync(
            await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
            MaximumManifestBytes,
            timeout.Token).ConfigureAwait(false);

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var dockerDigest = response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            ? values.SingleOrDefault()
            : null;

        var snapshot = OllamaRegistryManifestPolicy.Parse(
            body,
            dockerDigest,
            DateTimeOffset.UtcNow);

        if (!string.IsNullOrWhiteSpace(contentType) &&
            !string.Equals(contentType, snapshot.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The Ollama registry Content-Type does not match the manifest mediaType.");
        }

        return snapshot;
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("The official model manifest exceeded the allowed size.");
            output.Write(buffer, 0, read);
        }

        if (output.Length == 0)
            throw new InvalidDataException("The official model registry returned an empty manifest.");
        return output.ToArray();
    }
}
