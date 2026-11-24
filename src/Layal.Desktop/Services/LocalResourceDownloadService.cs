using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

public sealed record LocalResourceDownloadProgress(
    string ResourceId,
    long DownloadedBytes,
    long TotalBytes,
    double BytesPerSecond,
    double? RemainingSeconds);

/// <summary>
/// Downloads only catalog entries with a pinned size and SHA-256. Partial files are resumed
/// only when the server supplied a strong ETag or Last-Modified validator.
/// </summary>
public sealed class LocalResourceDownloadService : IDisposable
{
    private static readonly HashSet<string> DefaultApprovedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "codeload.github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com",
        "huggingface.co",
        "cdn-lfs.huggingface.co",
        "cas-bridge.xethub.hf.co",
        "download.pytorch.org",
        "pypi.org",
        "files.pythonhosted.org",
        "www.python.org",
        "ollama.com",
        "registry.ollama.ai"
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DestinationLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static SocketsHttpHandler CreateDownloadHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.None
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly HashSet<string> _approvedHosts;

    private sealed record ResumeMetadata(
        string SourceUri,
        long ExpectedSizeBytes,
        string ExpectedSha256,
        string? StrongETag,
        DateTimeOffset? LastModified);

    public LocalResourceDownloadService(
        HttpClient? httpClient = null,
        IEnumerable<string>? approvedHosts = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(CreateDownloadHttpHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        _approvedHosts = new HashSet<string>(
            approvedHosts ?? DefaultApprovedHosts,
            StringComparer.OrdinalIgnoreCase);
        if (_approvedHosts.Count == 0)
            throw new ArgumentException("At least one approved download host is required.", nameof(approvedHosts));
    }

    public async Task DownloadAsync(
        LocalResourceDefinition resource,
        string destinationPath,
        IProgress<LocalResourceDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<LocalResourceDownloadProgress, CancellationToken, ValueTask>? progressCallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var normalizedDestination = Path.GetFullPath(destinationPath);
        var destinationLock = DestinationLocks.GetOrAdd(
            normalizedDestination, _ => new SemaphoreSlim(1, 1));
        await destinationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadAttemptAsync(
                        resource, normalizedDestination, progress, cancellationToken, progressCallback)
                        .ConfigureAwait(false);
                    return;
                }
                catch (HttpRequestException exception) when (
                    attempt < 3 && IsTransientHttpFailure(exception))
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            destinationLock.Release();
        }
    }

    private async Task DownloadAttemptAsync(
        LocalResourceDefinition resource,
        string destinationPath,
        IProgress<LocalResourceDownloadProgress>? progress,
        CancellationToken cancellationToken,
        Func<LocalResourceDownloadProgress, CancellationToken, ValueTask>? progressCallback)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (resource.ExpectedSizeBytes is not > 0 || !resource.HasPinnedSha256)
            throw new InvalidOperationException(
                "A direct resource download requires a catalog-pinned byte size and SHA-256: " + resource.Id);

        if (!Uri.TryCreate(resource.Source, UriKind.Absolute, out var source))
            throw new InvalidOperationException("The resource catalog source is not an absolute URI: " + resource.Id);
        ValidateApprovedHttpsUri(source);

        var expectedSize = resource.ExpectedSizeBytes.Value;
        var expectedHash = resource.ExpectedSha256!.ToLowerInvariant();
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The download destination has no parent directory.");

        EnsureNoReparsePoints(directory);
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(directory);

        var partialPath = destination + ".partial";
        var metadataPath = partialPath + ".json";
        EnsureFileIsNotReparsePoint(destination);
        EnsureFileIsNotReparsePoint(partialPath);
        EnsureFileIsNotReparsePoint(metadataPath);

        if (await MatchesExpectedFileAsync(destination, expectedSize, expectedHash, cancellationToken)
            .ConfigureAwait(false))
        {
            await ReportProgressAsync(
                resource,
                new LocalResourceDownloadProgress(resource.Id, expectedSize, expectedSize, 0, 0),
                progress, progressCallback, cancellationToken).ConfigureAwait(false);
            return;
        }

        var resume = await TryLoadResumeMetadataAsync(
            metadataPath, partialPath, source, expectedSize, expectedHash, cancellationToken)
            .ConfigureAwait(false);

        var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (offset > expectedSize)
        {
            DeletePartialArtifacts(partialPath, metadataPath);
            offset = 0;
            resume = null;
        }

        if (offset == expectedSize)
        {
            if (await MatchesExpectedFileAsync(partialPath, expectedSize, expectedHash, cancellationToken)
                .ConfigureAwait(false))
            {
                File.Move(partialPath, destination, overwrite: true);
                TryDeleteFile(metadataPath);
                await ReportProgressAsync(
                    resource,
                    new LocalResourceDownloadProgress(resource.Id, expectedSize, expectedSize, 0, 0),
                    progress, progressCallback, cancellationToken).ConfigureAwait(false);
                return;
            }

            DeletePartialArtifacts(partialPath, metadataPath);
            offset = 0;
            resume = null;
        }

        if (offset > 0 && (resume is null ||
            (string.IsNullOrWhiteSpace(resume.StrongETag) && resume.LastModified is null)))
        {
            DeletePartialArtifacts(partialPath, metadataPath);
            offset = 0;
            resume = null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (!string.IsNullOrWhiteSpace(resume!.StrongETag) &&
                EntityTagHeaderValue.TryParse(resume.StrongETag, out var entityTag))
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
            }
            else if (resume.LastModified.HasValue)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(resume.LastModified.Value);
            }
        }

        using var response = await SendWithValidatedRedirectsAsync(
            request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
            offset == expectedSize &&
            await MatchesExpectedFileAsync(partialPath, expectedSize, expectedHash, cancellationToken)
                .ConfigureAwait(false))
        {
            File.Move(partialPath, destination, overwrite: true);
            TryDeleteFile(metadataPath);
            return;
        }

        var append = response.StatusCode == HttpStatusCode.PartialContent;
        if (append)
        {
            if (offset <= 0 ||
                response.Content.Headers.ContentRange?.From != offset ||
                response.Content.Headers.ContentRange?.Length != expectedSize)
            {
                DeletePartialArtifacts(partialPath, metadataPath);
                throw new InvalidDataException("The download server returned an invalid byte range.");
            }

            if (resume?.StrongETag is { Length: > 0 } expectedETag)
            {
                var actualETag = response.Headers.ETag;
                if (actualETag is null || actualETag.IsWeak ||
                    !string.Equals(expectedETag, actualETag.ToString(), StringComparison.Ordinal))
                {
                    DeletePartialArtifacts(partialPath, metadataPath);
                    throw new InvalidDataException(
                        "The server did not confirm the strong ETag for the resumed resource.");
                }
            }
            else if (resume?.LastModified is { } expectedLastModified)
            {
                if (response.Content.Headers.LastModified is not { } actualLastModified ||
                    actualLastModified != expectedLastModified)
                {
                    DeletePartialArtifacts(partialPath, metadataPath);
                    throw new InvalidDataException(
                        "The server did not confirm the Last-Modified validator for the resumed resource.");
                }
            }
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            // The server ignored Range or the validator changed. Start a clean full download.
            offset = 0;
        }
        else
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidDataException("The download server returned an unsupported success response.");
        }

        var expectedResponseLength = expectedSize - offset;
        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength != expectedResponseLength)
        {
            DeletePartialArtifacts(partialPath, metadataPath);
            throw new InvalidDataException("The download response length does not match the pinned resource size.");
        }

        var strongETag = response.Headers.ETag is { IsWeak: false } tag
            ? tag.ToString()
            : null;
        var lastModified = response.Content.Headers.LastModified ?? (append ? resume?.LastModified : null);
        var effectiveETag = strongETag ?? (append ? resume?.StrongETag : null);
        var metadata = new ResumeMetadata(
            source.AbsoluteUri,
            expectedSize,
            expectedHash,
            effectiveETag,
            lastModified);
        await WriteResumeMetadataAsync(metadataPath, metadata, cancellationToken).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        long downloaded = offset;
        var buffer = new byte[128 * 1024];
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var output = new FileStream(
                partialPath,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;

                    downloaded += count;
                    if (downloaded > expectedSize)
                        throw new InvalidDataException("The download exceeded the pinned resource size.");

                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    var elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
                    var speed = Math.Max(0, (downloaded - offset) / elapsedSeconds);
                    var remaining = speed > 0
                        ? Math.Max(0, (expectedSize - downloaded) / speed)
                        : (double?)null;
                    await ReportProgressAsync(
                        resource,
                        new LocalResourceDownloadProgress(resource.Id, downloaded, expectedSize, speed, remaining),
                        progress, progressCallback, cancellationToken).ConfigureAwait(false);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (downloaded != expectedSize)
                throw new InvalidDataException("The downloaded resource ended before its pinned byte size.");

            if (!await MatchesExpectedFileAsync(partialPath, expectedSize, expectedHash, cancellationToken)
                .ConfigureAwait(false))
            {
                DeletePartialArtifacts(partialPath, metadataPath);
                throw new InvalidDataException("SHA-256 verification failed; the downloaded resource was discarded.");
            }

            File.Move(partialPath, destination, overwrite: true);
            TryDeleteFile(metadataPath);
            await ReportProgressAsync(
                resource,
                new LocalResourceDownloadProgress(
                    resource.Id, expectedSize, expectedSize,
                    Math.Max(0, (expectedSize - offset) / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001)), 0),
                progress, progressCallback, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    public static Task<bool> VerifyFileAsync(
        LocalResourceDefinition resource,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.ExpectedSizeBytes is not > 0 || !resource.HasPinnedSha256)
            throw new InvalidOperationException(
                "A local resource verification requires a catalog-pinned byte size and SHA-256: " + resource.Id);
        return MatchesExpectedFileAsync(
            path, resource.ExpectedSizeBytes.Value, resource.ExpectedSha256!, cancellationToken);
    }

    private static async Task ReportProgressAsync(
        LocalResourceDefinition resource,
        LocalResourceDownloadProgress value,
        IProgress<LocalResourceDownloadProgress>? progress,
        Func<LocalResourceDownloadProgress, CancellationToken, ValueTask>? progressCallback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(value);
        if (progressCallback is not null)
            await progressCallback(value, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTransientHttpFailure(HttpRequestException exception)
    {
        if (exception.StatusCode is null)
            return true;

        var statusCode = (int)exception.StatusCode.Value;
        return exception.StatusCode == HttpStatusCode.RequestTimeout ||
               exception.StatusCode == HttpStatusCode.TooManyRequests ||
               statusCode is >= 500 and <= 599;
    }

    private async Task<ResumeMetadata?> TryLoadResumeMetadataAsync(
        string metadataPath,
        string partialPath,
        Uri source,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(partialPath) || !File.Exists(metadataPath))
        {
            if (File.Exists(partialPath) || File.Exists(metadataPath))
                DeletePartialArtifacts(partialPath, metadataPath);
            return null;
        }

        try
        {
            await using var input = new FileStream(
                metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var metadata = await JsonSerializer.DeserializeAsync<ResumeMetadata>(
                input, JsonOptions, cancellationToken).ConfigureAwait(false);

            if (metadata is null ||
                !string.Equals(metadata.SourceUri, source.AbsoluteUri, StringComparison.Ordinal) ||
                metadata.ExpectedSizeBytes != expectedSize ||
                !string.Equals(metadata.ExpectedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrWhiteSpace(metadata.StrongETag) && metadata.LastModified is null))
            {
                DeletePartialArtifacts(partialPath, metadataPath);
                return null;
            }

            return metadata;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DeletePartialArtifacts(partialPath, metadataPath);
            return null;
        }
    }

    private static async Task WriteResumeMetadataAsync(
        string path,
        ResumeMetadata metadata,
        CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, metadata, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static async Task<bool> MatchesExpectedFileAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        EnsureFileIsNotReparsePoint(path);
        var info = new FileInfo(path);
        if (info.Length != expectedSize) return false;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var expected = Convert.FromHexString(expectedSha256);
        return CryptographicOperations.FixedTimeEquals(hash, expected);
    }

    private const int MaximumValidatedRedirects = 5;

    /// <summary>
    /// Follows HTTPS redirects manually so every target is checked against the approved-host
    /// allowlist before any request is sent to that host. The download pipeline uses GET only.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
        HttpRequestMessage initialRequest,
        CancellationToken cancellationToken)
    {
        if (initialRequest.Method != HttpMethod.Get)
            throw new InvalidOperationException("Validated resource redirects currently support GET requests only.");

        var currentRequest = initialRequest;
        var ownsCurrentRequest = false;

        for (var redirectCount = 0; ; redirectCount++)
        {
            var currentUri = currentRequest.RequestUri
                ?? throw new InvalidOperationException("The resource download request has no URI.");
            ValidateApprovedHttpsUri(currentUri);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    currentRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (ownsCurrentRequest)
                    currentRequest.Dispose();
                throw;
            }

            if (!IsRedirectStatusCode(response.StatusCode))
            {
                try
                {
                    // Also verify the effective URI returned by the handler. The production
                    // handler disables automatic redirects; this is defense in depth for injected clients.
                    ValidateApprovedHttpsUri(response.RequestMessage?.RequestUri ?? currentUri);
                }
                catch
                {
                    response.Dispose();
                    if (ownsCurrentRequest)
                        currentRequest.Dispose();
                    throw;
                }

                if (ownsCurrentRequest)
                {
                    // The final URI has already been validated; the response body remains
                    // readable after disposing the request because resource downloads are GET-only.
                    response.RequestMessage = null;
                    currentRequest.Dispose();
                }
                return response;
            }

            if (redirectCount >= MaximumValidatedRedirects)
            {
                response.Dispose();
                if (ownsCurrentRequest)
                    currentRequest.Dispose();
                throw new InvalidDataException(
                    "The resource download exceeded the maximum number of validated redirects.");
            }

            if (response.Headers.Location is not { } location)
            {
                response.Dispose();
                if (ownsCurrentRequest)
                    currentRequest.Dispose();
                throw new InvalidDataException(
                    "The resource download server returned a redirect without a Location header.");
            }

            Uri nextUri;
            try
            {
                nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                // Validate before constructing/sending the next HTTP request. This blocks
                // unapproved hosts, HTTP downgrade redirects, user-info URLs, and loopback targets.
                ValidateApprovedHttpsUri(nextUri);
            }
            catch
            {
                response.Dispose();
                if (ownsCurrentRequest)
                    currentRequest.Dispose();
                throw;
            }

            var nextRequest = new HttpRequestMessage(HttpMethod.Get, nextUri);
            foreach (var header in currentRequest.Headers)
                nextRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

            response.Dispose();
            if (ownsCurrentRequest)
                currentRequest.Dispose();

            currentRequest = nextRequest;
            ownsCurrentRequest = true;
        }
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private void ValidateApprovedHttpsUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.IsLoopback ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !_approvedHosts.Contains(uri.IdnHost))
            throw new InvalidOperationException("The resource download URL is not an approved HTTPS source.");
    }

    private static void EnsureFileIsNotReparsePoint(string path)
    {
        if (File.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to use a reparse-point download artifact.");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The download path has no filesystem root.");
        var current = root;

        foreach (var segment in fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Refusing to download through a reparse-point directory.");
        }
    }

    private static void DeletePartialArtifacts(string partialPath, string metadataPath)
    {
        TryDeleteFile(partialPath);
        TryDeleteFile(metadataPath);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }
}
