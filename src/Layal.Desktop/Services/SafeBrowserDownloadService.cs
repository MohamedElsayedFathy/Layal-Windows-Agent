using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Layal.Services;

/// <summary>
/// Downloads only a visible same-origin HTTPS link selected in Layal's dedicated browser.
/// It sends no browser cookies or credentials, pins connections to public DNS results,
/// blocks redirects outside the original host, caps file size, rejects active/executable formats,
/// and never opens the downloaded file automatically.
/// </summary>
public sealed class SafeBrowserDownloadService
{
    public const long MaximumDownloadBytes = 20L * 1024 * 1024;
    public const int MaximumRedirects = 3;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".md", ".csv", ".json", ".docx", ".xlsx", ".png", ".jpg", ".jpeg", ".webp"
    };
    private readonly string _workspaceRoot;
    private readonly string _downloadRoot;

    public SafeBrowserDownloadService(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _downloadRoot = Path.Combine(_workspaceRoot, "Downloads");
    }

    public static Uri ValidateDownloadTarget(string pageUrl, string downloadUrl)
    {
        var page = BrowserAutomationService.ValidateHttpUrl(pageUrl);
        var download = BrowserAutomationService.ValidateHttpUrl(downloadUrl);

        if (!string.Equals(page.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(download.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Browser downloads require HTTPS on both the current page and the file link.");
        }

        if (!string.Equals(page.IdnHost, download.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            page.Port != download.Port)
        {
            throw new InvalidOperationException("For safety, browser downloads are limited to the exact HTTPS origin of the current page. Cross-origin/CDN downloads are not supported.");
        }

        var extension = Path.GetExtension(download.AbsolutePath);
        if (!AllowedExtensions.Contains(extension))
            throw new InvalidOperationException("This download type is not supported. Allowed types: PDF, TXT, MD, CSV, JSON, DOCX, XLSX, PNG, JPG, and WEBP.");

        if (download.AbsolutePath.Length > 1024 || download.Query.Length > 2048)
            throw new InvalidOperationException("The download URL is too long.");

        return download;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            return IsPublicAddress(address.MapToIPv4());

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            var c = bytes[2];
            if (a == 0 || a == 10 || a == 127 || a >= 224)
                return false;
            if (a == 100 && b is >= 64 and <= 127)
                return false; // Shared address space (RFC 6598).
            if (a == 169 && b == 254)
                return false;
            if (a == 172 && b is >= 16 and <= 31)
                return false;
            if (a == 192 && (b == 168 || (b == 0 && c == 0) || (b == 0 && c == 2)))
                return false;
            if (a == 198 && (b == 18 || b == 19 || (b == 51 && c == 100)))
                return false;
            if (a == 203 && b == 0 && c == 113)
                return false;
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
            address.IsIPv6Any || address.IsIPv6Loopback ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast || address.IsIPv6Teredo)
            return false;

        // Only global-unicast 2000::/3 is accepted. Unique-local, documentation,
        // protocol-assignment, link-local and other special-use ranges are excluded.
        if ((bytes[0] & 0xE0) != 0x20)
            return false;
        if (bytes[0] == 0x20 && bytes[1] == 0x01 &&
            (bytes[2] == 0x0D || bytes[2] == 0x00))
            return false; // Documentation and Teredo/protocol assignment blocks.
        return true;
    }

    public static string SanitizeFileName(string? proposedName, string fallbackExtension)
    {
        var name = (proposedName ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].Trim();
        var safe = new string(name.Where(ch =>
            char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' or ' ' or '(' or ')')
            .Take(120)
            .ToArray()).Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
            safe = "download" + fallbackExtension;

        var extension = Path.GetExtension(safe);
        if (!AllowedExtensions.Contains(extension))
            safe = Path.GetFileNameWithoutExtension(safe) + fallbackExtension;

        var baseName = Path.GetFileNameWithoutExtension(safe);
        if (string.Equals(baseName, "CON", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(baseName, "PRN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(baseName, "AUX", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(baseName, "NUL", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(baseName, @"^(COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            safe = "download" + Path.GetExtension(safe);
        }

        return safe;
    }

    public async Task<BrowserDownloadResult> DownloadAsync(
        string pageUrl,
        string downloadUrl,
        CancellationToken cancellationToken = default)
    {
        var page = BrowserAutomationService.ValidateHttpUrl(pageUrl);
        var initial = ValidateDownloadTarget(page.AbsoluteUri, downloadUrl);
        EnsureSafeDirectory();

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            MaxConnectionsPerServer = 1,
            ConnectCallback = ConnectToPublicAddressAsync
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(35) };

        var current = initial;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDownloadTarget(page.AbsoluteUri, current.AbsoluteUri);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd("Layal-Safe-Downloader/1.0");
            request.Headers.Accept.ParseAdd("application/pdf, text/plain, text/markdown, text/csv, application/json, application/vnd.openxmlformats-officedocument.*, image/png, image/jpeg, image/webp, application/octet-stream;q=0.5");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                if (redirect >= MaximumRedirects ||
                    response.Headers.Location is null)
                    throw new InvalidOperationException("The download exceeded the permitted redirect count or returned an invalid redirect.");

                var next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                current = ValidateDownloadTarget(page.AbsoluteUri, next.AbsoluteUri);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The file server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
                throw new InvalidDataException($"The file exceeds the {MaximumDownloadBytes / (1024 * 1024)} MB download limit.");

            var fallbackExtension = Path.GetExtension(current.AbsolutePath).ToLowerInvariant();
            var proposedName = response.Content.Headers.ContentDisposition?.FileNameStar ??
                response.Content.Headers.ContentDisposition?.FileName ??
                Uri.UnescapeDataString(Path.GetFileName(current.AbsolutePath));
            var fileName = SanitizeFileName(proposedName, fallbackExtension);
            var actualExtension = Path.GetExtension(fileName);
            if (!string.Equals(actualExtension, fallbackExtension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The server-provided filename extension does not match the approved link extension.");

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("x-msdownload", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("x-executable", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The server returned HTML, script, or executable content instead of an approved document.");

            var resultPath = await SaveBoundedFileAsync(
                response.Content,
                fileName,
                fallbackExtension,
                cancellationToken);
            var length = new FileInfo(resultPath).Length;
            return new BrowserDownloadResult(
                resultPath,
                Path.GetRelativePath(_workspaceRoot, resultPath),
                length,
                mediaType,
                current.AbsoluteUri);
        }

        throw new InvalidOperationException("The download could not be completed safely.");
    }

    private async Task<string> SaveBoundedFileAsync(
        HttpContent content,
        string fileName,
        string extension,
        CancellationToken token)
    {
        EnsureSafeDirectory();
        var temporaryPath = Path.Combine(_downloadRoot, "." + Guid.NewGuid().ToString("N") + ".part");
        string? finalPath = null;
        try
        {
            long total = 0;
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var input = await content.ReadAsStreamAsync(token))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, token);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > MaximumDownloadBytes)
                        throw new InvalidDataException($"The download exceeded the {MaximumDownloadBytes / (1024 * 1024)} MB limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                await output.FlushAsync(token);
            }

            if (total == 0)
                throw new InvalidDataException("The server returned an empty file.");

            await ValidateFileContentAsync(temporaryPath, extension, token);
            finalPath = ChooseUniquePath(fileName);
            File.Move(temporaryPath, finalPath, overwrite: false);
            return finalPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            if (finalPath is not null)
                TryDelete(finalPath);
            throw;
        }
    }

    private static async Task ValidateFileContentAsync(string path, string extension, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var header = new byte[16];
        var count = await stream.ReadAsync(header, token);
        var valid = extension switch
        {
            ".pdf" => count >= 5 && Encoding.ASCII.GetString(header, 0, 5) == "%PDF-",
            ".png" => count >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => count >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".webp" => count >= 12 && Encoding.ASCII.GetString(header, 0, 4) == "RIFF" &&
                       Encoding.ASCII.GetString(header, 8, 4) == "WEBP",
            ".docx" or ".xlsx" => count >= 4 && header[0] == 0x50 && header[1] == 0x4B &&
                                  header[2] is 0x03 or 0x05 or 0x07,
            _ => await ValidateUtf8TextAsync(stream, header, count, token)
        };
        if (!valid)
            throw new InvalidDataException("The downloaded bytes do not match the approved file type. The file was discarded.");

        if (extension is ".docx" or ".xlsx")
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count is 0 or > 2048 ||
                archive.Entries.Any(entry => entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The downloaded Office package is empty, too complex, or contains a macro payload. The file was discarded.");
        }
    }

    private static async Task<bool> ValidateUtf8TextAsync(Stream stream, byte[] header, int count, CancellationToken token)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(header, 0, count);
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer, token);
            if (read > 0)
                _ = new UTF8Encoding(false, true).GetString(buffer, 0, read);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken token)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, token);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("The download host resolved to a private, local, reserved, or mixed public/private address set. The request was blocked.");

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (ex is OperationCanceledException)
                    throw;
                lastError = ex;
            }
        }

        throw new HttpRequestException("Could not connect to the validated public download host.", lastError);
    }

    private void EnsureSafeDirectory()
    {
        EnsureNoReparsePoints(_workspaceRoot);
        Directory.CreateDirectory(_downloadRoot);
        EnsureNoReparsePoints(_downloadRoot);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidOperationException("The download path has no filesystem root.");
        var current = root;
        var relative = fullPath[root.Length..];
        foreach (var segment in relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Downloads are blocked because the workspace path contains a symbolic link or reparse point.");
        }
    }

    private string ChooseUniquePath(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 0; index < 100; index++)
        {
            var suffix = index == 0 ? string.Empty : $" ({index})";
            var candidate = Path.Combine(_downloadRoot, baseName + suffix + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
        throw new IOException("Could not choose a unique download filename after 100 attempts.");
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
            HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Preserve the original exception. */ }
    }
}

public sealed record BrowserDownloadResult(
    string FullPath,
    string RelativePath,
    long Length,
    string ContentType,
    string FinalUrl);
