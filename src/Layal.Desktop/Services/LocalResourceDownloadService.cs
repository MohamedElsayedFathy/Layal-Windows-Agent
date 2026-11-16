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
        "ollama.com",
        "registry.ollama.ai"
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DestinationLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
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
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = Timeout.InfiniteTimeSpan };

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
        CancellationToken cancellationToken = default)
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
                    await DownloadAttemptAsync(resource, normalizedDestination, progress, cancellationToken)
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
        CancellationToken cancellationToken)
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
            progress?.Report(new LocalResourceDownloadProgress(
                resource.Id, expectedSize, expectedSize, 0, 0));
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
                progress?.Report(new LocalResourceDownloadProgress(
                    resource.Id, expectedSize, expectedSize, 0, 0));
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

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        var finalUri = response.RequestMessage?.RequestUri ?? source;
        ValidateApprovedHttpsUri(finalUri);

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
                if (response.Headers.ETag is not { IsWeak: false } actualETag ||
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
                    progress?.Report(new LocalResourceDownloadProgress(
                        resource.Id, downloaded, expectedSize, speed, remaining));
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
            progress?.Report(new LocalResourceDownloadProgress(
                resource.Id, expectedSize, expectedSize,
                Math.Max(0, (expectedSize - offset) / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001)), 0));
        }
        finally
        {
            Array.Clear(buffer);
        }
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

    private void ValidateApprovedHttpsUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
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
