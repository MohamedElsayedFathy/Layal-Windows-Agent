using System.Text.Json;
using Layal.Models;

namespace Layal.Services;

public sealed class MemoryService
{
    public const int MaxSavedNotes = 100;

    private readonly ProtectedJsonStore<List<MemoryNote>> _store;
    private readonly object _sync = new();

    public MemoryService(string path) => _store = new ProtectedJsonStore<List<MemoryNote>>(path);

    public IReadOnlyList<MemoryNote> GetAll(bool includeArchived = false)
    {
        lock (_sync)
        {
            var notes = LoadBoundedNotes();
            if (!includeArchived)
                notes = notes.Where(note => !note.IsArchived).ToList();
            return notes.OrderByDescending(note => note.UpdatedAt).ToList();
        }
    }

    public MemoryNote Save(string title, string content, string? id = null, string? category = null)
    {
        title = (title ?? string.Empty).Trim();
        content = (content ?? string.Empty).Trim();
        if (title.Length is < 1 or > 120) throw new InvalidOperationException("Memory title must be between 1 and 120 characters.");
        if (content.Length is < 1 or > 12000) throw new InvalidOperationException("Memory content must be between 1 and 12,000 characters.");
        if (LooksLikeSecret(title + "\n" + content))
            throw new InvalidOperationException("This note resembles an API key, token, password, or private key. Store credentials in the secure credential setting, not in memory.");

        lock (_sync)
        {
            var notes = LoadBoundedNotes();
            var existing = id is null ? null : notes.FirstOrDefault(n => n.Id == id);
            var normalizedCategory = category is null
                ? NormalizeCategory(existing?.Category ?? "General")
                : NormalizeCategory(category);
            if (existing is null)
            {
                existing = new MemoryNote { Title = title, Content = content, Category = normalizedCategory };
                notes.Add(existing);
            }
            else
            {
                existing.Title = title;
                existing.Content = content;
                existing.Category = normalizedCategory;
                existing.UpdatedAt = DateTimeOffset.Now;
            }

            // Keep the note being saved even when several notes share the same timestamp.
            _store.Save(KeepNewest(notes, existing.Id));
            return existing;
        }
    }

    /// <summary>Archives or restores a note without deleting its contents.</summary>
    /// <summary>
    /// Merges validated notes from a backup. Existing IDs are left unchanged; no existing note is
    /// deleted or overwritten, and imported content is checked for credential-like secrets.
    /// </summary>
    public int ImportMany(IEnumerable<MemoryNote> imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var candidates = imported.Take(MaxSavedNotes + 1).ToList();
        if (candidates.Count > MaxSavedNotes)
            throw new InvalidOperationException($"A memory backup may contain at most {MaxSavedNotes} notes.");

        var normalized = new List<MemoryNote>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in candidates)
        {
            if (note is null ||
                !Guid.TryParseExact(note.Id, "N", out _) ||
                string.IsNullOrWhiteSpace(note.Title) || note.Title.Length > 120 ||
                string.IsNullOrWhiteSpace(note.Content) || note.Content.Length > 12_000)
                throw new InvalidOperationException("The memory backup contains an invalid note.");

            var title = note.Title.Trim();
            var content = note.Content.Trim();
            if (LooksLikeSecret(title + "\\n" + content))
                throw new InvalidOperationException("The memory backup contains credential-like content. No notes were imported.");
            if (!seenIds.Add(note.Id))
                continue;

            normalized.Add(new MemoryNote
            {
                Id = note.Id,
                Title = title,
                Content = content,
                Category = NormalizeCategory(note.Category),
                IsArchived = note.IsArchived,
                CreatedAt = note.CreatedAt,
                UpdatedAt = note.UpdatedAt
            });
        }

        lock (_sync)
        {
            var notes = LoadBoundedNotes();
            var existingIds = notes.Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
            var toAdd = normalized.Where(note => !existingIds.Contains(note.Id)).ToList();
            if (notes.Count + toAdd.Count > MaxSavedNotes)
                throw new InvalidOperationException("There is not enough memory capacity to merge this backup. Existing notes were not changed.");

            if (toAdd.Count > 0)
            {
                notes.AddRange(toAdd);
                _store.Save(notes);
            }
            return toAdd.Count;
        }
    }

    public bool SetArchived(string id, bool isArchived)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_sync)
        {
            var notes = LoadBoundedNotes();
            var note = notes.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (note is null) return false;
            if (note.IsArchived == isArchived) return true;
            note.IsArchived = isArchived;
            note.UpdatedAt = DateTimeOffset.Now;
            _store.Save(notes);
            return true;
        }
    }

    public static string NormalizeCategory(string? category)
    {
        var value = (category ?? string.Empty).Trim();
        if (value.Length == 0) return "General";
        if (value.Length > 40 || value.Any(char.IsControl))
            throw new InvalidOperationException("Memory categories must be 1–40 printable characters.");
        return value;
    }

    public bool Delete(string id)
    {
        lock (_sync)
        {
            var notes = LoadBoundedNotes();
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
            .Where(n => n.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        n.Content.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        n.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(maxResults, 1, 20)).ToList();
    }

    public string ExportJson()
    {
        // Export includes archived notes so a backup preserves the complete user-managed memory set.
        var notes = GetAll(includeArchived: true);
        return JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true });
    }

    private List<MemoryNote> LoadBoundedNotes()
    {
        var notes = _store.Load(new List<MemoryNote>());
        var metadataChanged = false;
        foreach (var note in notes)
        {
            string normalized;
            try { normalized = NormalizeCategory(note.Category); }
            catch (InvalidOperationException) { normalized = "General"; }
            if (!string.Equals(note.Category, normalized, StringComparison.Ordinal))
            {
                note.Category = normalized;
                metadataChanged = true;
            }
        }

        if (notes.Count <= MaxSavedNotes)
        {
            if (metadataChanged)
                _store.Save(notes);
            return notes;
        }

        var bounded = KeepNewest(notes);
        // Apply retention and category migration to legacy/existing files on normal access.
        _store.Save(bounded);
        return bounded;
    }

    private static List<MemoryNote> KeepNewest(List<MemoryNote> notes, string? preserveId = null)
    {
        if (notes.Count <= MaxSavedNotes) return notes;

        var ordered = notes
            .OrderByDescending(n => n.UpdatedAt)
            .ThenByDescending(n => n.CreatedAt)
            .ToList();
        var kept = new List<MemoryNote>(MaxSavedNotes);
        var preserved = preserveId is null ? null : ordered.FirstOrDefault(n => n.Id == preserveId);
        if (preserved is not null) kept.Add(preserved);

        foreach (var note in ordered)
        {
            if (kept.Count >= MaxSavedNotes) break;
            if (preserved is not null && note.Id == preserved.Id) continue;
            kept.Add(note);
        }
        return kept;
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
