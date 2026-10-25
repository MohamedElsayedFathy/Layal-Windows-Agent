using System.Text.Json;
using Layal.Models;

namespace Layal.Services;

public sealed class MemoryService
{
    private readonly ProtectedJsonStore<List<MemoryNote>> _store;
    private readonly object _sync = new();

    public MemoryService(string path) => _store = new ProtectedJsonStore<List<MemoryNote>>(path);

    public IReadOnlyList<MemoryNote> GetAll()
    {
        lock (_sync) return _store.Load(new List<MemoryNote>()).OrderByDescending(n => n.UpdatedAt).ToList();
    }

    public MemoryNote Save(string title, string content, string? id = null)
    {
        title = (title ?? string.Empty).Trim();
        content = (content ?? string.Empty).Trim();
        if (title.Length is < 1 or > 120) throw new InvalidOperationException("Memory title must be between 1 and 120 characters.");
        if (content.Length is < 1 or > 12000) throw new InvalidOperationException("Memory content must be between 1 and 12,000 characters.");
        if (LooksLikeSecret(title + "\n" + content))
            throw new InvalidOperationException("This note resembles an API key, token, password, or private key. Store credentials in the secure credential setting, not in memory.");

        lock (_sync)
        {
            var notes = _store.Load(new List<MemoryNote>());
            var existing = id is null ? null : notes.FirstOrDefault(n => n.Id == id);
            if (existing is null)
            {
                existing = new MemoryNote { Title = title, Content = content };
                notes.Add(existing);
            }
            else
            {
                existing.Title = title;
                existing.Content = content;
                existing.UpdatedAt = DateTimeOffset.Now;
            }
            _store.Save(notes);
            return existing;
        }
    }

    public bool Delete(string id)
    {
        lock (_sync)
        {
            var notes = _store.Load(new List<MemoryNote>());
            var removed = notes.RemoveAll(n => n.Id == id) > 0;
            if (removed) _store.Save(notes);
            return removed;
        }
    }

    public IReadOnlyList<MemoryNote> Search(string query, int maxResults = 8)
    {
        query = (query ?? string.Empty).Trim();
        if (query.Length == 0) return Array.Empty<MemoryNote>();
        return GetAll()
            .Where(n => n.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || n.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(maxResults, 1, 20)).ToList();
    }

    public string ExportJson()
    {
        var notes = GetAll();
        return JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true });
    }

    // Provider-specific key shapes are matched case-sensitively and with their real structure so ordinary
    // words/names containing "akia" or "aiza" (for example "Nakia" or "Aizawa") are not rejected.
    private static readonly System.Text.RegularExpressions.Regex AwsAccessKey =
        new(@"\bAKIA[0-9A-Z]{16}\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex GoogleApiKey =
        new(@"\bAIza[0-9A-Za-z_\-]{30,}", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool LooksLikeSecret(string value)
    {
        string[] patterns =
        {
            "-----BEGIN PRIVATE KEY-----", "-----BEGIN RSA PRIVATE KEY-----",
            "sk-proj-", "sk-ant-", "ghp_", "github_pat_"
        };
        return patterns.Any(p => value.Contains(p, StringComparison.OrdinalIgnoreCase)) ||
               AwsAccessKey.IsMatch(value) || GoogleApiKey.IsMatch(value) ||
               (value.Contains("password", StringComparison.OrdinalIgnoreCase) && value.Contains('=')) ||
               (value.Contains("api_key", StringComparison.OrdinalIgnoreCase) && value.Contains('='));
    }
}
