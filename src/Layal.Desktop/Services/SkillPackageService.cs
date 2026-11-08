using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Layal.Models;

namespace Layal.Services;

public sealed class SkillPackageService
{
    private readonly string _root;
    private readonly string _indexPath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly Regex NamePattern = new("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.Compiled);
    private const long MaximumArchiveBytes = 50L * 1024 * 1024;
    private const int MaximumPackageEntries = 200;
    private const long MaximumPackageEntryBytes = 5L * 1024 * 1024;
    private const long MaximumPackageExpandedBytes = 10L * 1024 * 1024;
    private static readonly HttpClient SharedGitHubHttpClient = CreateGitHubHttpClient();
    private readonly HttpClient _githubHttpClient;

    private static HttpClient CreateGitHubHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LayalSkillImporter/0.4");
        return client;
    }

    public SkillPackageService(string root) : this(root, SharedGitHubHttpClient) { }

    public SkillPackageService(string root, HttpClient githubHttpClient)
    {
        _githubHttpClient = githubHttpClient ?? throw new ArgumentNullException(nameof(githubHttpClient));
        if (_githubHttpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _githubHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LayalSkillImporter/0.4");
        _root = Path.GetFullPath(root);
        // Check existing ancestors before Directory.CreateDirectory so a linked parent cannot
        // cause the constructor itself to create the managed catalog outside its intended path.
        EnsureNoReparsePointsInExistingPath(_root);
        Directory.CreateDirectory(_root);
        EnsureNoReparsePointsInExistingPath(_root);
        _indexPath = Path.Combine(_root, "skills-index.json");
    }

    public IReadOnlyList<SkillRecord> GetInstalled()
    {
        try
        {
            return ReadIndexStrict();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            // Keep the UI usable, but never treat an unreadable index as a valid empty catalog.
            // Mutating operations call ReadIndexStrict directly and fail closed.
            PreserveUnreadableIndexBestEffort();
            return Array.Empty<SkillRecord>();
        }
    }

    private List<SkillRecord> ReadIndexStrict()
    {
        if (Directory.Exists(_indexPath))
            throw new IOException("The skills index path is a directory; refusing to modify the skill catalog.");
        EnsureNoReparsePointsInExistingPath(_indexPath);

        string json;
        try
        {
            json = File.ReadAllText(_indexPath);
        }
        catch (FileNotFoundException)
        {
            // A genuinely missing index is normal for a first run.
            return new List<SkillRecord>();
        }

        var records = JsonSerializer.Deserialize<List<SkillRecord>>(json, JsonOptions);
        if (records is null)
            throw new JsonException("The skills index must contain a JSON array.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (record is null)
                throw new JsonException("The skills index contains a missing or invalid skill name.");

            var skillName = record.Name;
            if (!IsValidSkillName(skillName))
                throw new JsonException("The skills index contains a missing or invalid skill name.");

            ValidateSkillRecordProvenance(record);

            if (!names.Add(skillName))
                throw new JsonException("The skills index contains duplicate skill names.");

            string expectedPackagePath;
            string actualPackagePath;
            try
            {
                expectedPackagePath = Path.GetFullPath(Path.Combine(_root, skillName, skillName + ".zip"));
                actualPackagePath = Path.GetFullPath(record.PackagePath ?? string.Empty);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new JsonException("The skills index contains an invalid package path.", ex);
            }

            if (!string.Equals(expectedPackagePath, actualPackagePath, StringComparison.OrdinalIgnoreCase))
                throw new JsonException("The skills index contains a package path outside its managed skill directory.");

            try
            {
                EnsureNoReparsePointsInExistingPath(actualPackagePath);
            }
            catch (IOException ex)
            {
                throw new JsonException("The skills index references a package below a symbolic link or reparse point.", ex);
            }
        }

        return records;
    }

    private void PreserveUnreadableIndexBestEffort()
    {
        try
        {
            if (!File.Exists(_indexPath)) return;

            // RefreshSkills may call GetInstalled repeatedly. Do not accumulate another copy
            // every time the same corrupt index is read; retain one backup per distinct payload.
            byte[] sourceHash;
            using (var source = File.OpenRead(_indexPath))
                sourceHash = SHA256.HashData(source);

            foreach (var existingBackup in Directory.EnumerateFiles(_root, "skills-index.json.corrupt-*.bak"))
            {
                using var backup = File.OpenRead(existingBackup);
                var backupHash = SHA256.HashData(backup);
                if (CryptographicOperations.FixedTimeEquals(sourceHash, backupHash)) return;
            }

            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            var backupPath = _indexPath + ".corrupt-" + timestamp + "-" + Guid.NewGuid().ToString("N") + ".bak";
            File.Copy(_indexPath, backupPath, overwrite: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
    }

    public SkillRecord ImportZip(string zipPath, bool overwrite) =>
        ImportZipInternal(zipPath, overwrite, sourceProvenance: null);

    private SkillRecord ImportZipInternal(string zipPath, bool overwrite, GitHubSourceProvenance? sourceProvenance)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Skill package was not found.", zipPath);

        // Snapshot the user-selected archive once while its source handle is open. Validation and
        // installation then consume exactly these staged bytes rather than reopening a mutable path.
        var originalFileName = Path.GetFileName(zipPath);
        var stagedArchive = StageImportArchive(zipPath);
        try
        {
            return ImportStagedArchive(stagedArchive, originalFileName, overwrite, sourceProvenance);
        }
        finally
        {
            try { if (File.Exists(stagedArchive)) File.Delete(stagedArchive); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string StageImportArchive(string sourcePath)
    {
        EnsureNoReparsePointsInExistingPath(_root);
        var stagedPath = Path.Combine(_root, ".layal-skill-import-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sourceLength = source.Length;
            if (sourceLength <= 0 || sourceLength > MaximumArchiveBytes)
                throw new InvalidOperationException("Skill package must be non-empty and no larger than the 50 MB import limit.");

            using (var destination = new FileStream(
                       stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 64 * 1024, options: FileOptions.WriteThrough))
            {
                var copied = CopyBounded(source, destination, MaximumArchiveBytes);
                if (copied != sourceLength)
                    throw new IOException("The skill package changed or ended unexpectedly while it was being staged.");
                destination.Flush(flushToDisk: true);
            }

            EnsureNoReparsePointsInExistingPath(stagedPath);
            return stagedPath;
        }
        catch
        {
            try { if (File.Exists(stagedPath)) File.Delete(stagedPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private SkillRecord ImportStagedArchive(
        string stagedArchive,
        string originalFileName,
        bool overwrite,
        GitHubSourceProvenance? sourceProvenance)
    {
        string name;
        string description;
        string skillMarkdownEntry;
        using (var archive = ZipFile.OpenRead(stagedArchive))
        {
            ValidateSkillArchive(archive);
            var candidates = archive.Entries
                .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal) &&
                    entry.FullName.Split('/').Last().Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0)
                throw new InvalidOperationException("The ZIP does not contain a SKILL.md file.");
            if (candidates.Count != 1)
                throw new InvalidOperationException("The ZIP contains multiple SKILL.md files; import one unambiguous skill package at a time.");
            if (candidates[0].Length > 1_000_000)
                throw new InvalidOperationException("SKILL.md is unexpectedly large.");

            skillMarkdownEntry = candidates[0].FullName;
            var text = ReadEntryTextBounded(candidates[0], 1_000_000);
            (name, description) = ParseFrontMatter(text);
        }

        // Read strictly before changing any package files. A broken index must not be
        // interpreted as an empty catalog, or an existing package could be overwritten.
        var records = ReadIndexStrict();
        var existing = records.FirstOrDefault(skill => skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !overwrite)
            throw new SkillAlreadyExistsException(name);

        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        EnsureNoReparsePointsInExistingPath(folder);

        var target = Path.Combine(folder, name + ".zip");
        if (Directory.Exists(target))
            throw new IOException("The skill package target is a directory; refusing to replace it.");
        EnsureNoReparsePointsInExistingPath(target);

        var temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        var previous = target + ".previous-" + Guid.NewGuid().ToString("N");
        var hadPreviousPackage = File.Exists(target);
        if (hadPreviousPackage && existing is null && !overwrite)
        {
            // A package can remain on disk after an interrupted older import even when the
            // index has no corresponding record. Never silently overwrite that orphan package.
            throw new SkillAlreadyExistsException(name);
        }

        var previousPackageMoved = false;
        var newPackageInstalled = false;

        var record = new SkillRecord
        {
            Name = name,
            Description = description,
            PackagePath = target,
            OriginalFileName = originalFileName,
            SkillMarkdownEntry = skillMarkdownEntry,
            ImportedAt = DateTimeOffset.Now,
            PackageSha256 = ComputeSha256Hex(stagedArchive),
            SourceType = sourceProvenance is null ? "local" : "github",
            SourceRepository = sourceProvenance?.Repository,
            SourceRef = sourceProvenance?.RefName,
            SourceCommitSha = sourceProvenance?.CommitSha,
            SourceArchiveSha256 = sourceProvenance?.ArchiveSha256,
            SourceSkillPath = sourceProvenance?.SkillPath,
            SourceResolvedAtUtc = sourceProvenance?.ResolvedAtUtc,
            ValidationStatus = sourceProvenance is null
                ? "Archive paths and expanded sizes validated; package SHA-256 recorded; scripts not executed"
                : $"Archive paths validated; GitHub revision {sourceProvenance.CommitSha} pinned; package SHA-256 recorded; scripts not executed"
        };
        records.RemoveAll(skill => skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        records.Add(record);

        try
        {
            EnsureNoReparsePointsInExistingPath(folder);
            EnsureNoReparsePointsInExistingPath(stagedArchive);
            if (File.Exists(target) != hadPreviousPackage)
                throw new IOException("The skill package changed during import; no package was replaced.");

            if (hadPreviousPackage)
            {
                File.Move(target, previous, overwrite: false);
                previousPackageMoved = true;
            }

            // Both files are beneath the same managed root, so this is a same-volume move.
            File.Move(stagedArchive, temp, overwrite: false);
            if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(temp)),
                    Path.GetPathRoot(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The staged skill package is not on the target volume.");
            File.Move(temp, target, overwrite: false);
            newPackageInstalled = true;
            EnsureNoReparsePointsInExistingPath(target);
            SaveIndex(records);
        }
        catch (Exception importError)
        {
            try
            {
                if (previousPackageMoved && File.Exists(previous))
                {
                    if (newPackageInstalled && File.Exists(target)) File.Delete(target);
                    File.Move(previous, target, overwrite: false);
                }
                else if (!hadPreviousPackage && newPackageInstalled && File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch (Exception rollbackError)
            {
                throw new IOException(
                    $"Skill import failed and automatic rollback was incomplete. Inspect the retained package backup '{previous}' before retrying.",
                    new AggregateException(importError, rollbackError));
            }

            throw;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // A stale backup must not turn a successful import into a reported failure.
        if (hadPreviousPackage)
        {
            try { if (File.Exists(previous)) File.Delete(previous); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return record;
    }

    private static void ValidateSkillArchive(ZipArchive archive)
    {
        if (archive.Entries.Count is < 1 or > MaximumPackageEntries)
            throw new InvalidOperationException("A skill package must contain between 1 and 200 entries.");

        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;
        foreach (var entry in archive.Entries)
        {
            var rawPath = entry.FullName;
            if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Length > 2048 ||
                rawPath.Contains('\\') || rawPath.StartsWith("/", StringComparison.Ordinal) ||
                Path.IsPathRooted(rawPath) || Regex.IsMatch(rawPath, "^[A-Za-z]:"))
                throw new InvalidOperationException("The skill archive contains an absolute, rooted, or non-canonical path.");

            var isDirectory = rawPath.EndsWith("/", StringComparison.Ordinal);
            var path = isDirectory ? rawPath[..^1] : rawPath;
            if (path.Length == 0)
                throw new InvalidOperationException("The skill archive contains an empty path.");
            var segments = path.Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
                throw new InvalidOperationException("The skill archive contains an unsafe relative path.");

            foreach (var segment in segments)
            {
                if (segment.Length > 255 || segment.EndsWith(" ", StringComparison.Ordinal) ||
                    segment.EndsWith(".", StringComparison.Ordinal) ||
                    segment.Any(character => character is '<' or '>' or '"' or '|' or '?' or '*' or ':' ||
                        char.IsControl(character)))
                    throw new InvalidOperationException("The skill archive contains a Windows-invalid path segment.");

                var deviceStem = segment.Split('.')[0].TrimEnd(' ', '.');
                if (IsReservedWindowsDeviceName(deviceStem))
                    throw new InvalidOperationException("The skill archive contains a reserved Windows device name.");
            }

            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000)
                throw new InvalidOperationException("Symbolic links are not allowed in imported skills.");
            if (unixMode is not 0 and not 0x8000 and not 0x4000)
                throw new InvalidOperationException("The skill archive contains an unsupported special filesystem entry.");
            if (isDirectory && entry.Length != 0)
                throw new InvalidOperationException("A skill archive directory entry cannot contain file data.");
            if (unixMode == 0x4000 && !isDirectory || unixMode == 0x8000 && isDirectory)
                throw new InvalidOperationException("The skill archive has inconsistent file/directory metadata.");

            if (!isDirectory && entry.Length > MaximumPackageEntryBytes)
                throw new InvalidOperationException("A skill package file exceeds the 5 MB per-file expanded-size limit.");
            declaredTotal += entry.Length;
            if (declaredTotal > MaximumPackageExpandedBytes)
                throw new InvalidOperationException("The skill package exceeds the 10 MB total expanded-size limit.");

            if (!paths.TryAdd(path, isDirectory))
                throw new InvalidOperationException("The skill archive contains duplicate or case-colliding paths.");
        }

        foreach (var pair in paths)
        {
            var segments = pair.Key.Split('/');
            for (var index = 1; index < segments.Length; index++)
            {
                var parent = string.Join("/", segments.Take(index));
                if (paths.TryGetValue(parent, out var parentIsDirectory) && !parentIsDirectory)
                    throw new InvalidOperationException("A skill archive file is also used as a parent directory.");
            }

            if (!pair.Value && paths.Keys.Any(candidate =>
                    candidate.StartsWith(pair.Key + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A skill archive file conflicts with a nested package path.");
        }

        long actualTotal = 0;
        foreach (var entry in archive.Entries.Where(item => !item.FullName.EndsWith("/", StringComparison.Ordinal)))
        {
            using var input = entry.Open();
            var remaining = MaximumPackageExpandedBytes - actualTotal;
            var actualLength = CopyBounded(input, Stream.Null, Math.Min(MaximumPackageEntryBytes, remaining));
            if (actualLength != entry.Length)
                throw new InvalidOperationException("A skill archive entry's expanded length does not match its ZIP metadata.");
            actualTotal += actualLength;
        }
    }

    private static bool IsNumberedWindowsDeviceName(string value, string prefix) =>
        value.Length == prefix.Length + 1 &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        value[^1] is >= '1' and <= '9';

    private static bool IsReservedWindowsDeviceName(string value) =>
        value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
        IsNumberedWindowsDeviceName(value, "COM") ||
        IsNumberedWindowsDeviceName(value, "LPT");

    private static bool IsValidSkillName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        NamePattern.IsMatch(value) &&
        !IsReservedWindowsDeviceName(value);

    private static bool IsSha256Hash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void ValidateSkillRecordProvenance(SkillRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.PackageSha256) && !IsSha256Hash(record.PackageSha256))
            throw new JsonException("The skills index contains an invalid package SHA-256 digest.");

        bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);
        if (string.Equals(record.SourceType, "local", StringComparison.Ordinal))
        {
            if (HasText(record.SourceRepository) || HasText(record.SourceRef) ||
                HasText(record.SourceCommitSha) || HasText(record.SourceArchiveSha256) ||
                HasText(record.SourceSkillPath) || record.SourceResolvedAtUtc is not null)
                throw new JsonException("The skills index contains GitHub provenance fields on a local skill record.");
            return;
        }

        if (!string.Equals(record.SourceType, "github", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(record.PackageSha256) ||
            string.IsNullOrWhiteSpace(record.SourceRepository) ||
            !Regex.IsMatch(record.SourceRepository, "^[A-Za-z0-9-]{1,100}/[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant) ||
            string.IsNullOrWhiteSpace(record.SourceRef) || record.SourceRef.Length > 255 ||
            record.SourceRef.Any(char.IsControl) ||
            record.SourceCommitSha is not { Length: 40 } ||
            !record.SourceCommitSha.All(Uri.IsHexDigit) ||
            !IsSha256Hash(record.SourceArchiveSha256) ||
            record.SourceSkillPath is null || record.SourceSkillPath.Length > 2048 ||
            record.SourceSkillPath.StartsWith("/", StringComparison.Ordinal) ||
            record.SourceSkillPath.Contains('\\') ||
            (!string.IsNullOrEmpty(record.SourceSkillPath) &&
             record.SourceSkillPath.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..")) ||
            record.SourceResolvedAtUtc is null ||
            record.SourceResolvedAtUtc.Value.Offset != TimeSpan.Zero)
            throw new JsonException("The skills index contains incomplete or invalid GitHub provenance metadata.");
    }

    private static string ComputeSha256Hex(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private string ResolveManagedPackagePath(SkillRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsValidSkillName(record.Name))
            throw new InvalidOperationException("The skill record has an invalid managed package name.");

        var expectedPath = Path.GetFullPath(Path.Combine(_root, record.Name, record.Name + ".zip"));
        var actualPath = Path.GetFullPath(record.PackagePath ?? string.Empty);
        if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The skill record package path is outside its managed skill directory.");

        EnsureNoReparsePointsInExistingPath(expectedPath);
        if (!File.Exists(expectedPath))
            throw new FileNotFoundException("Imported skill package is missing.", expectedPath);
        return expectedPath;
    }

    public async Task<SkillRecord> ImportFromGitHubAsync(string sourceUrl, bool overwrite, CancellationToken cancellationToken = default)
    {
        var source = ParseGitHubUrl(sourceUrl);
        var requestedRef = source.Branch ?? await GetGitHubDefaultBranchAsync(source.Owner, source.Repository, cancellationToken);
        var commitSha = await ResolveGitHubCommitShaAsync(source.Owner, source.Repository, requestedRef, cancellationToken);

        var rawTemp = Path.Combine(Path.GetTempPath(), "layal-github-" + Guid.NewGuid().ToString("N") + ".zip");
        var filteredTemp = Path.Combine(Path.GetTempPath(), "layal-skill-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            // Download by the resolved commit SHA, never by a branch name that can move.
            await DownloadGitHubArchiveAsync(source.Owner, source.Repository, commitSha, rawTemp, cancellationToken);
            var sourceArchiveSha256 = ComputeSha256Hex(rawTemp);
            var selectedSkillPath = FilterSkillArchive(rawTemp, filteredTemp, source.SkillDirectory);
            var provenance = new GitHubSourceProvenance(
                $"{source.Owner}/{source.Repository}",
                requestedRef,
                commitSha,
                sourceArchiveSha256,
                selectedSkillPath,
                DateTimeOffset.UtcNow);
            return ImportZipInternal(filteredTemp, overwrite, provenance);
        }
        finally
        {
            try { if (File.Exists(rawTemp)) File.Delete(rawTemp); } catch { }
            try { if (File.Exists(filteredTemp)) File.Delete(filteredTemp); } catch { }
        }
    }

    public string ReadSkillMarkdown(SkillRecord record, int maxCharacters = 12000)
    {
        var packagePath = ResolveManagedPackagePath(record);
        if (!string.IsNullOrWhiteSpace(record.PackageSha256))
        {
            if (!IsSha256Hash(record.PackageSha256))
                throw new InvalidOperationException("The installed skill package has an invalid SHA-256 catalog value.");
            var actualPackageSha256 = ComputeSha256Hex(packagePath);
            if (!string.Equals(actualPackageSha256, record.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The installed skill package no longer matches its recorded SHA-256 digest.");
        }
        using var archive = ZipFile.OpenRead(packagePath);
        ValidateSkillArchive(archive);
        var candidates = archive.Entries
            .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal) &&
                entry.FullName.Split('/').Last().Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var selected = candidates.SingleOrDefault(entry =>
            string.Equals(entry.FullName, record.SkillMarkdownEntry, StringComparison.Ordinal));
        if (selected is null)
            throw new InvalidOperationException("The stored skill package no longer contains its indexed SKILL.md entry.");

        var content = ReadEntryTextBounded(selected, 1_000_000);
        var (packageName, packageDescription) = ParseFrontMatter(content);
        if (!string.Equals(packageName, record.Name, StringComparison.Ordinal) ||
            !string.Equals(packageDescription, record.Description, StringComparison.Ordinal))
            throw new InvalidOperationException("The stored skill package metadata no longer matches the installed catalog entry.");

        return content.Length <= maxCharacters ? content : content[..maxCharacters] + "\n[skill text truncated by Layal]";
    }

    private async Task<string> GetGitHubDefaultBranchAsync(string owner, string repository, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}";
        using var response = await _githubHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub repository metadata request failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                null,
                response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("default_branch", out var branchElement) ||
            branchElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(branchElement.GetString()))
            throw new InvalidDataException("GitHub repository metadata did not contain a usable default branch.");

        return branchElement.GetString()!;
    }

    private async Task<string> ResolveGitHubCommitShaAsync(
        string owner,
        string repository,
        string requestedRef,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestedRef) || requestedRef.Length > 255 || requestedRef.Any(char.IsControl))
            throw new InvalidOperationException("The selected GitHub branch or ref is invalid.");

        var url =
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/commits/{Uri.EscapeDataString(requestedRef)}";
        using var response = await _githubHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub could not resolve the selected ref to a commit (HTTP {(int)response.StatusCode}: {response.ReasonPhrase}).",
                null,
                response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("sha", out var shaElement) ||
            shaElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("GitHub commit resolution did not return a commit SHA.");

        var commitSha = shaElement.GetString();
        if (commitSha is null || !Regex.IsMatch(commitSha, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("GitHub returned an invalid commit SHA.");

        return commitSha.ToLowerInvariant();
    }

    private async Task DownloadGitHubArchiveAsync(
        string owner,
        string repository,
        string commitSha,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(commitSha, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("GitHub archive download requires a resolved 40-character commit SHA.");

        var url = $"https://codeload.github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/zip/{commitSha}";
        using var response = await _githubHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GitHub archive download failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).",
                null,
                response.StatusCode);
        if (response.Content.Headers.ContentLength is > 50 * 1024 * 1024)
            throw new InvalidOperationException("GitHub skill archive exceeds the 50 MB download limit.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > 50L * 1024 * 1024)
                throw new InvalidOperationException("GitHub skill archive exceeded the 50 MB download limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static string FilterSkillArchive(string sourceZip, string destinationZip, string? desiredDirectory)
    {
        using var source = ZipFile.OpenRead(sourceZip);
        var entries = source.Entries.Where(e => !string.IsNullOrEmpty(e.FullName)).ToList();
        if (entries.Count > 10000) throw new InvalidOperationException("The repository archive has too many entries to inspect safely.");
        string? top = entries.Select(e => e.FullName.Replace('\\', '/').Split('/')[0]).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(top)) throw new InvalidOperationException("The repository archive is empty.");

        string Normalize(string path) => path.Replace('\\', '/').Trim('/');
        var normalizedDesired = desiredDirectory is null ? null : Normalize(desiredDirectory);
        var candidates = entries.Where(e =>
        {
            var normalized = Normalize(e.FullName);
            if (!normalized.StartsWith(top + "/", StringComparison.Ordinal)) return false;
            var relative = normalized[(top.Length + 1)..];
            if (!relative.Split('/').Last().Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) return false;
            if (normalizedDesired is null) return true;
            return relative.Equals(normalizedDesired + "/SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                   (normalizedDesired.Length == 0 && relative.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
        }).OrderBy(e => e.FullName.Count(c => c == '/')).ThenBy(e => e.FullName.Length).ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException(normalizedDesired is null
                ? "No SKILL.md was found in this repository. Paste the exact GitHub directory URL for the skill."
                : $"No SKILL.md was found at the requested directory '{normalizedDesired}'. Paste the exact directory URL containing SKILL.md.");

        var chosen = Normalize(candidates[0].FullName);
        var chosenRelative = chosen[(top.Length + 1)..];
        var skillDirectory = chosenRelative.Contains('/') ? chosenRelative[..chosenRelative.LastIndexOf('/')] : string.Empty;
        var prefix = skillDirectory.Length == 0 ? top + "/" : top + "/" + skillDirectory + "/";
        var selectedEntries = entries.Where(e =>
        {
            var full = Normalize(e.FullName);
            return full.StartsWith(prefix, StringComparison.Ordinal) && !full.Equals(prefix.TrimEnd('/'), StringComparison.Ordinal);
        }).ToList();
        if (selectedEntries.Count == 0 || selectedEntries.Count > 200)
            throw new InvalidOperationException("The selected skill must contain between 1 and 200 package entries.");

        long totalUncompressed = 0;
        foreach (var entry in selectedEntries)
        {
            var normalized = Normalize(entry.FullName);
            if (normalized.Split('/').Any(part => part is "." or "..") || Path.IsPathRooted(normalized))
                throw new InvalidOperationException("The skill archive contains an unsafe path.");
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000) throw new InvalidOperationException("Symbolic links are not allowed in imported skills.");
            if (entry.Length > 5L * 1024 * 1024) throw new InvalidOperationException("A selected skill file exceeds the 5 MB per-file limit.");
            totalUncompressed += entry.Length;
        }
        if (totalUncompressed > 10L * 1024 * 1024) throw new InvalidOperationException("The selected skill exceeds the 10 MB extracted-size limit.");

        using var outputFile = new FileStream(destinationZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var target = new ZipArchive(outputFile, ZipArchiveMode.Create, leaveOpen: false);
        long actualTotal = 0;
        foreach (var entry in selectedEntries)
        {
            var full = Normalize(entry.FullName);
            var relative = full[prefix.Length..];
            if (string.IsNullOrWhiteSpace(relative)) continue;
            if (relative.Split('/').Any(part => part is "." or "..")) throw new InvalidOperationException("The skill archive contains an unsafe relative path.");
            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var newEntry = target.CreateEntry(isDirectory ? relative + "/" : relative, CompressionLevel.Optimal);
            if (isDirectory) continue;
            using var input = entry.Open();
            using var destination = newEntry.Open();
            var remainingTotal = 10L * 1024 * 1024 - actualTotal;
            var entryLimit = Math.Min(5L * 1024 * 1024, remainingTotal);
            actualTotal += CopyBounded(input, destination, entryLimit);
            if (actualTotal > 10L * 1024 * 1024)
                throw new InvalidOperationException("The selected skill exceeds the 10 MB extracted-size limit.");
        }

        return skillDirectory;
    }

    /// <summary>Read a compressed entry with a bound on bytes actually expanded, not only its ZIP header.</summary>
    internal static string ReadEntryTextBounded(ZipArchiveEntry entry, long maxBytes)
    {
        using var source = entry.Open();
        using var buffer = new MemoryStream();
        CopyBounded(source, buffer, maxBytes);
        buffer.Position = 0;
        using var reader = new StreamReader(buffer);
        return reader.ReadToEnd();
    }

    /// <summary>Copy no more than maxBytes of actual source data to the destination.</summary>
    internal static long CopyBounded(Stream source, Stream destination, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            // Check before writing this chunk so a malicious entry cannot exceed the output bound.
            if (read > maxBytes - total)
                throw new InvalidOperationException("A skill archive entry expanded beyond its allowed size; the archive was rejected.");
            destination.Write(chunk, 0, read);
            total += read;
        }
        return total;
    }

    private static GitHubSkillSource ParseGitHubUrl(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Enter a clean HTTPS GitHub URL without credentials, custom ports, query strings, or fragments.");
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length < 2 || segments.Any(s => s is "." or "..")) throw new InvalidOperationException("The GitHub URL does not contain an owner and repository.");
        var owner = segments[0];
        var repository = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        if (owner.Length > 100 || repository.Length > 100 || !Regex.IsMatch(owner, "^[A-Za-z0-9-]+$") || !Regex.IsMatch(repository, "^[A-Za-z0-9._-]+$"))
            throw new InvalidOperationException("The owner/repository portion of the GitHub URL is invalid.");
        if (segments.Length == 2) return new GitHubSkillSource(owner, repository, null, null);
        var mode = segments[2];
        if (mode is not ("tree" or "blob") || segments.Length < 4)
            throw new InvalidOperationException("Supported GitHub URL formats are a repository URL, tree directory URL, or blob SKILL.md URL.");
        var branch = segments[3];
        if (string.IsNullOrWhiteSpace(branch) || branch.Contains('/')) throw new InvalidOperationException("Unable to determine the branch from this GitHub URL.");
        var pathSegments = segments.Skip(4).ToArray();
        if (mode == "blob")
        {
            if (pathSegments.Length == 0 || !pathSegments[^1].Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("For a GitHub blob URL, select the SKILL.md file itself.");
            pathSegments = pathSegments[..^1];
        }
        return new GitHubSkillSource(owner, repository, branch, string.Join('/', pathSegments));
    }

    private sealed record GitHubSkillSource(string Owner, string Repository, string? Branch, string? SkillDirectory);

    private sealed record GitHubSourceProvenance(
        string Repository,
        string RefName,
        string CommitSha,
        string ArchiveSha256,
        string SkillPath,
        DateTimeOffset ResolvedAtUtc);

    private static (string Name, string Description) ParseFrontMatter(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        if (lines.Length < 4 || lines[0].Trim() != "---")
            throw new InvalidOperationException("SKILL.md must begin with YAML front matter delimited by ---.");
        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end < 2) throw new InvalidOperationException("SKILL.md YAML front matter has no closing --- delimiter.");
        string? name = null;
        string? description = null;
        for (var i = 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                name = Unquote(line[5..].Trim());
            }
            else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["description:".Length..].Trim();
                if (value is ">" or ">-" or ">+" or "|" or "|-" or "|+")
                {
                    var blockLines = new List<string>();
                    while (i + 1 < end)
                    {
                        var candidate = lines[i + 1];
                        if (string.IsNullOrWhiteSpace(candidate))
                        {
                            blockLines.Add(string.Empty);
                            i++;
                            continue;
                        }

                        if (!char.IsWhiteSpace(candidate[0])) break;
                        blockLines.Add(candidate.Trim());
                        i++;
                    }

                    description = value.StartsWith("|", StringComparison.Ordinal)
                        ? string.Join(Environment.NewLine, blockLines).Trim()
                        : FoldDescriptionLines(blockLines);
                }
                else
                {
                    description = Unquote(value);
                }
            }
        }
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("SKILL.md front matter must contain a non-empty name field.");
        if (!IsValidSkillName(name))
            throw new InvalidOperationException("Skill name must use lowercase letters, numbers, and hyphens, and cannot be a reserved Windows device name.");
        if (string.IsNullOrWhiteSpace(description)) throw new InvalidOperationException("SKILL.md front matter must contain a non-empty description field.");
        return (name, description);
    }

    private static string FoldDescriptionLines(IReadOnlyList<string> lines)
    {
        var paragraphs = new List<string>();
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0)
                {
                    paragraphs.Add(string.Join(" ", current));
                    current.Clear();
                }
                continue;
            }

            current.Add(line.Trim());
        }

        if (current.Count > 0) paragraphs.Add(string.Join(" ", current));
        return string.Join(Environment.NewLine + Environment.NewLine, paragraphs).Trim();
    }

    private static string Unquote(string value) => value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
        ? value[1..^1] : value;

    private static void EnsureNoReparsePointsInExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new IOException("The skill package path has no filesystem root.");
        var current = root;
        var segments = fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The skill catalog path contains a symbolic link or reparse point; refusing to follow it.");
            if (index < segments.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                throw new IOException("A skill catalog path component is not a directory.");
        }
    }

    private void SaveIndex(List<SkillRecord> records)
    {
        EnsureNoReparsePointsInExistingPath(_root);
        EnsureNoReparsePointsInExistingPath(_indexPath);
        var temp = _indexPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(output))
            {
                writer.Write(JsonSerializer.Serialize(records, JsonOptions));
                writer.Flush();
                output.Flush(flushToDisk: true);
            }

            EnsureNoReparsePointsInExistingPath(_indexPath);
            if (File.Exists(_indexPath)) File.Replace(temp, _indexPath, null);
            else File.Move(temp, _indexPath);
        }
        finally
        {
            // Unique staging names avoid collisions between operations, and cleanup must
            // not mask the original save/replace failure.
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

public sealed class SkillAlreadyExistsException(string name) : Exception($"A skill named '{name}' already exists.")
{
    public string SkillName { get; } = name;
}
