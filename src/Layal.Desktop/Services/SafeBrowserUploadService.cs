using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Security.Cryptography;

namespace Layal.Services;

/// <summary>
/// Validates one user-approved browser upload. Only regular files within LayalWorkspace
/// and an explicit passive-document/image allowlist are eligible; content is never executed.
/// </summary>
public sealed class SafeBrowserUploadService
{
    public const long MaximumUploadBytes = 20L * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".md", ".csv", ".json", ".docx", ".xlsx", ".png", ".jpg", ".jpeg", ".webp"
    };

    private readonly string _workspaceRoot;
    private readonly string _rootPrefix;

    public SafeBrowserUploadService(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        _rootPrefix = _workspaceRoot + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Resolves and fingerprints a workspace-relative file before the approval prompt.
    /// Hashing is local only; no file bytes are sent by this service.
    /// </summary>
    public SafeBrowserUploadFile PrepareUpload(string relativePath)
    {
        var value = (relativePath ?? string.Empty).Trim().Replace('\\', '/');
        if (value.Length is < 1 or > 400 ||
            Path.IsPathRooted(value) ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            value.Contains(':') ||
            value.Any(char.IsControl))
            throw new InvalidOperationException("Upload paths must be bounded, printable, workspace-relative paths.");

        var segments = value.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new InvalidOperationException("Upload paths cannot contain empty, '.' or '..' path segments.");

        EnsureWorkspacePathIsRegular();
        var current = _workspaceRoot;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.GetFullPath(Path.Combine(current, segments[index]));
            if (!current.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected upload resolves outside LayalWorkspace.");

            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { throw new FileNotFoundException("The selected upload file does not exist in LayalWorkspace.", value); }
            catch (DirectoryNotFoundException) { throw new FileNotFoundException("The selected upload file or one of its folders no longer exists in LayalWorkspace.", value); }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Browser uploads are blocked for symbolic links and other reparse points.");
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (index < segments.Length - 1 && !isDirectory)
                throw new InvalidOperationException("An upload path contains a non-directory parent component.");
            if (index == segments.Length - 1 && isDirectory)
                throw new InvalidOperationException("Browser uploads accept one regular file, not a folder.");
        }

        if (!File.Exists(current))
            throw new FileNotFoundException("The selected upload file no longer exists.", value);

        var extension = Path.GetExtension(current);
        if (!AllowedExtensions.Contains(extension))
            throw new InvalidOperationException("Browser uploads allow only PDF, TXT, MD, CSV, JSON, DOCX, XLSX, PNG, JPG, JPEG, and WEBP files.");

        var info = new FileInfo(current);
        if (info.Length is <= 0 or > MaximumUploadBytes)
            throw new InvalidOperationException($"Browser uploads require a non-empty file no larger than {MaximumUploadBytes / (1024 * 1024)} MiB.");

        string digest;
        long hashedLength;
        DateTime lastWriteUtc;
        using (var stream = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            hashedLength = stream.Length;
            lastWriteUtc = File.GetLastWriteTimeUtc(current);
            ValidateContent(stream, extension);
            stream.Position = 0;
            digest = Convert.ToHexString(SHA256.HashData(stream));
        }

        if (hashedLength != info.Length || lastWriteUtc != File.GetLastWriteTimeUtc(current))
            throw new IOException("The selected file changed while it was being inspected. Select it again before uploading.");

        return new SafeBrowserUploadFile(
            value.Replace('\\', '/'),
            current,
            Path.GetFileName(current),
            extension,
            hashedLength,
            lastWriteUtc,
            digest);
    }

    /// <summary>Re-hashes the file after user approval and rejects any path or content change.</summary>
    public SafeBrowserUploadFile RevalidateUnchanged(SafeBrowserUploadFile approved)
    {
        ArgumentNullException.ThrowIfNull(approved);
        var current = PrepareUpload(approved.RelativePath);
        if (!string.Equals(current.FullPath, approved.FullPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.Sha256, approved.Sha256, StringComparison.Ordinal) ||
            current.LengthBytes != approved.LengthBytes ||
            current.LastWriteTimeUtc != approved.LastWriteTimeUtc)
            throw new IOException("The selected file changed while awaiting approval. No browser upload was started; inspect and approve the current file again.");
        return current;
    }

    /// <summary>
    /// Checks a file's actual format, not only its extension. This keeps executable or active
    /// content disguised as a document out of the upload allowlist.
    /// </summary>
    private static void ValidateContent(FileStream stream, string extension)
    {
        Span<byte> header = stackalloc byte[16];
        var read = stream.Read(header);
        stream.Position = 0;

        var valid = extension.ToLowerInvariant() switch
        {
            ".pdf" => read >= 5 && Encoding.ASCII.GetString(header[..5]) == "%PDF-",
            ".png" => read >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".webp" => read >= 12 && Encoding.ASCII.GetString(header.Slice(0, 4)) == "RIFF" &&
                       Encoding.ASCII.GetString(header.Slice(8, 4)) == "WEBP",
            ".docx" => ValidateOfficeArchive(stream, "word/document.xml"),
            ".xlsx" => ValidateOfficeArchive(stream, "xl/workbook.xml"),
            ".json" => ValidateJsonText(stream),
            ".txt" or ".md" or ".csv" => ValidateUtf8Text(stream),
            _ => false
        };

        if (!valid)
            throw new InvalidDataException("The selected file's contents do not match its allowed document/image type. No upload was started.");
    }

    private static bool ValidateOfficeArchive(FileStream stream, string requiredDocumentEntry)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count is 0 or > 2048 ||
                !archive.Entries.Any(entry => string.Equals(entry.FullName, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase)) ||
                !archive.Entries.Any(entry => string.Equals(entry.FullName, requiredDocumentEntry, StringComparison.OrdinalIgnoreCase)) ||
                archive.Entries.Any(entry => entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                return false;

            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length < 0 || entry.Length > 32L * 1024 * 1024)
                    return false;
                expandedBytes += entry.Length;
                if (expandedBytes > 80L * 1024 * 1024)
                    return false;
                if (entry.Length > 1_048_576 && entry.Length / Math.Max(1L, entry.CompressedLength) > 500)
                    return false;
            }
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static bool ValidateJsonText(FileStream stream)
    {
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            var json = reader.ReadToEnd();
            stream.Position = 0;
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            return true;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or JsonException)
        {
            stream.Position = 0;
            return false;
        }
    }

    private static bool ValidateUtf8Text(FileStream stream)
    {
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var buffer = new char[4096];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (Array.IndexOf(buffer, '\\0', 0, read) >= 0)
                {
                    stream.Position = 0;
                    return false;
                }
            }
            stream.Position = 0;
            return true;
        }
        catch (DecoderFallbackException)
        {
            stream.Position = 0;
            return false;
        }
    }

    private void EnsureWorkspacePathIsRegular()
    {
        if (!Directory.Exists(_workspaceRoot))
            throw new DirectoryNotFoundException("LayalWorkspace does not exist.");

        var root = Path.GetPathRoot(_workspaceRoot)
            ?? throw new InvalidOperationException("LayalWorkspace has no filesystem root.");
        var current = root;
        foreach (var segment in _workspaceRoot[root.Length..].Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
                throw new DirectoryNotFoundException("A LayalWorkspace parent directory no longer exists.");
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) == 0)
                throw new InvalidOperationException("LayalWorkspace contains a symbolic link, reparse point, or non-directory parent.");
        }
    }
}

public sealed record SafeBrowserUploadFile(
    string RelativePath,
    string FullPath,
    string FileName,
    string Extension,
    long LengthBytes,
    DateTime LastWriteTimeUtc,
    string Sha256);
