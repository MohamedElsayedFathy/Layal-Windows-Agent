using System.IO.Compression;
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

    public SkillPackageService(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
        _indexPath = Path.Combine(root, "skills-index.json");
    }

    public IReadOnlyList<SkillRecord> GetInstalled()
    {
        try
        {
            if (!File.Exists(_indexPath)) return Array.Empty<SkillRecord>();
            return JsonSerializer.Deserialize<List<SkillRecord>>(File.ReadAllText(_indexPath), JsonOptions) ?? new();
        }
        catch { return Array.Empty<SkillRecord>(); }
    }

    public SkillRecord ImportZip(string zipPath, bool overwrite)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("Skill package was not found.", zipPath);
        var info = new FileInfo(zipPath);
        if (info.Length > 50 * 1024 * 1024) throw new InvalidOperationException("Skill package exceeds the 50 MB import limit.");

        string name;
        string description;
        string skillMarkdownEntry;
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            var candidates = archive.Entries
                .Where(e => e.FullName.Replace('\\', '/').Split('/').Last().Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Count(c => c == '/' || c == '\\'))
                .ThenBy(e => e.FullName.Length)
                .ToList();
            if (candidates.Count == 0) throw new InvalidOperationException("The ZIP does not contain a SKILL.md file.");
            if (candidates[0].Length > 1_000_000) throw new InvalidOperationException("SKILL.md is unexpectedly large.");
            skillMarkdownEntry = candidates[0].FullName;
            using var reader = new StreamReader(candidates[0].Open());
            var text = reader.ReadToEnd();
            (name, description) = ParseFrontMatter(text);
        }

        var records = GetInstalled().ToList();
        var existing = records.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !overwrite)
            throw new SkillAlreadyExistsException(name);

        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name + ".zip");
        var temp = target + ".tmp";
        File.Copy(zipPath, temp, true);
        File.Move(temp, target, overwrite: true);

        var record = new SkillRecord
        {
            Name = name,
            Description = description,
            PackagePath = target,
            OriginalFileName = Path.GetFileName(zipPath),
            SkillMarkdownEntry = skillMarkdownEntry,
            ImportedAt = DateTimeOffset.Now,
            ValidationStatus = "Metadata validated; package stored without extraction; scripts not executed"
        };
        records.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        records.Add(record);
        SaveIndex(records);
        return record;
    }

    public async Task<SkillRecord> ImportFromGitHubAsync(string sourceUrl, bool overwrite, CancellationToken cancellationToken = default)
    {
        var source = ParseGitHubUrl(sourceUrl);
        var defaultBranch = source.Branch ?? await TryGetDefaultBranchAsync(source.Owner, source.Repository, cancellationToken);
        var branches = source.Branch is null
            ? new[] { defaultBranch, "main", "master" }.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b!).Distinct(StringComparer.Ordinal).ToArray()
            : new[] { source.Branch! };
        Exception? lastError = null;
        foreach (var branch in branches.Distinct(StringComparer.Ordinal))
        {
            var rawTemp = Path.Combine(Path.GetTempPath(), "layal-github-" + Guid.NewGuid().ToString("N") + ".zip");
            var filteredTemp = Path.Combine(Path.GetTempPath(), "layal-skill-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                await DownloadGitHubArchiveAsync(source.Owner, source.Repository, branch, rawTemp, cancellationToken);
                FilterSkillArchive(rawTemp, filteredTemp, source.SkillDirectory);
                return ImportZip(filteredTemp, overwrite);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                if (source.Branch is not null) throw;
            }
            finally
            {
                try { if (File.Exists(rawTemp)) File.Delete(rawTemp); } catch { }
                try { if (File.Exists(filteredTemp)) File.Delete(filteredTemp); } catch { }
            }
        }
        throw new InvalidOperationException("Could not download the public GitHub repository on main or master branch.", lastError);
    }

    public string ReadSkillMarkdown(SkillRecord record, int maxCharacters = 12000)
    {
        if (!File.Exists(record.PackagePath)) throw new FileNotFoundException("Imported skill package is missing.", record.PackagePath);
        using var archive = ZipFile.OpenRead(record.PackagePath);
        var candidates = archive.Entries
            .Where(e => e.FullName.Replace('\\', '/').Split('/').Last().Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c == '/' || c == '\\'))
            .ThenBy(e => e.FullName.Length)
            .ToList();
        var selected = candidates.FirstOrDefault(e => string.Equals(e.FullName, record.SkillMarkdownEntry, StringComparison.Ordinal)) ?? candidates.FirstOrDefault();
        if (selected is null) throw new InvalidOperationException("No SKILL.md was found in the stored skill package.");
        using var reader = new StreamReader(selected.Open());
        var content = reader.ReadToEnd();
        return content.Length <= maxCharacters ? content : content[..maxCharacters] + "\n[skill text truncated by Layal]";
    }

    private static async Task<string?> TryGetDefaultBranchAsync(string owner, string repository, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LayalSkillImporter", "0.3"));
            using var response = await client.GetAsync($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("default_branch", out var branch) && branch.ValueKind == JsonValueKind.String)
                return branch.GetString();
        }
        catch { /* Fall back to common branch names if repository metadata cannot be read. */ }
        return null;
    }

    private static async Task DownloadGitHubArchiveAsync(string owner, string repository, string branch, string destination, CancellationToken cancellationToken)
    {
        if (branch.Contains('/') || branch.Contains('\\'))
            throw new InvalidOperationException("For branches containing '/', use a GitHub directory URL whose branch is the first segment after tree/ or blob/.");
        var url = $"https://codeload.github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/zip/refs/heads/{Uri.EscapeDataString(branch)}";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LayalSkillImporter", "0.3"));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub archive download failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
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
            if (total > 50L * 1024 * 1024) throw new InvalidOperationException("GitHub skill archive exceeded the 50 MB download limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void FilterSkillArchive(string sourceZip, string destinationZip, string? desiredDirectory)
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
        foreach (var entry in selectedEntries)
        {
            var full = Normalize(entry.FullName);
            var relative = full[prefix.Length..];
            if (string.IsNullOrWhiteSpace(relative)) continue;
            if (relative.Split('/').Any(part => part is "." or "..")) throw new InvalidOperationException("The skill archive contains an unsafe relative path.");
            var newEntry = target.CreateEntry(relative, CompressionLevel.Optimal);
            if (entry.FullName.EndsWith('/')) continue;
            using var input = entry.Open();
            using var destination = newEntry.Open();
            input.CopyTo(destination);
        }
    }

    private static GitHubSkillSource ParseGitHubUrl(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enter an HTTPS GitHub URL in repository, tree directory, or blob SKILL.md format.");
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

                    description = value.StartsWith('|', StringComparison.Ordinal)
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
        if (!NamePattern.IsMatch(name)) throw new InvalidOperationException("Skill name must use lowercase letters, numbers, and hyphens.");
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

    private void SaveIndex(List<SkillRecord> records)
    {
        var temp = _indexPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(records, JsonOptions));
        if (File.Exists(_indexPath)) File.Replace(temp, _indexPath, null);
        else File.Move(temp, _indexPath);
    }
}

public sealed class SkillAlreadyExistsException(string name) : Exception($"A skill named '{name}' already exists.")
{
    public string SkillName { get; } = name;
}
