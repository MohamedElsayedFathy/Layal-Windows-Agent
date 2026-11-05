using System.Text.Json;
using Layal.Models;

namespace Layal.Services;

public sealed class MemoryService
{
    // Retention cap applies to active and archived notes together; there is no age-based expiry.
    public const int MaxSavedNotes = 100;
    public const int MaxRelationshipsPerNote = 12;
    public const int MaxRelationshipLabelCharacters = 60;

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

    public MemoryNote Save(
        string title,
        string content,
        string? id = null,
        string? category = null,
        DateTimeOffset? expectedUpdatedAt = null)
    {
        title = NormalizeTitle(title);
        content = NormalizeContent(content);
        if (LooksLikeSecret(title + "\n" + content))
            throw new InvalidOperationException("This note resembles an API key, token, password, or private key. Store credentials in the secure credential setting, not in memory.");

        lock (_sync)
        {
            var notes = LoadBoundedNotes();
            var existing = id is null ? null : notes.FirstOrDefault(n => n.Id == id);
            if (id is not null && existing is null)
                throw new InvalidOperationException("This memory note no longer exists. Refresh the list before saving; no new note was created.");
            if (existing is not null && expectedUpdatedAt.HasValue && existing.UpdatedAt != expectedUpdatedAt.Value)
                throw new InvalidOperationException("This memory note changed after it was opened. Reload it before saving to avoid overwriting newer content.");
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
            var bounded = KeepNewest(notes, existing.Id);
            NormalizeRelationships(bounded);
            _store.Save(bounded);
            return existing;
        }
    }

    /// <summary>Archives or restores a note without deleting its contents.</summary>
    /// <summary>
    /// Merges validated notes from a backup. Existing IDs are left unchanged; no existing note is
    /// deleted or overwritten, and imported content is checked for credential-like secrets.
    /// </summary>
    public void ValidateImportMany(IEnumerable<MemoryNote> imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var candidates = imported.Take(MaxSavedNotes + 1).ToList();
        if (candidates.Count > MaxSavedNotes)
            throw new InvalidOperationException($"A memory backup may contain at most {MaxSavedNotes} notes.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in candidates)
        {
            if (note is null || !Guid.TryParseExact(note.Id, "N", out _))
                throw new InvalidOperationException("The memory backup contains an invalid note identifier.");
            var title = NormalizeTitle(note.Title);
            var content = NormalizeContent(note.Content);
            _ = NormalizeCategory(note.Category);
            ValidateRelationships(note);
            if (LooksLikeSecret(title + "\n" + content))
                throw new InvalidOperationException("The memory backup contains credential-like content. No notes were imported.");
            if (!ids.Add(note.Id))
                throw new InvalidOperationException("The memory backup contains a duplicate note identifier. No notes were imported.");
        }

        lock (_sync)
        {
            var notes = LoadBoundedNotes();
            var existingIds = notes.Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
            var additions = ids.Count(id => !existingIds.Contains(id));
            if (notes.Count + additions > MaxSavedNotes)
                throw new InvalidOperationException("There is not enough memory capacity to merge this backup. Existing notes were not changed.");
        }
    }

    public int ImportMany(IEnumerable<MemoryNote> imported) =>
        ImportManyWithIds(imported).Count;

    /// <summary>
    /// Merges validated memory notes and returns exactly the IDs newly added by this call.
    /// The workspace snapshot coordinator uses these IDs for compensating rollback if a
    /// subsequent protected-store or file operation fails.
    /// </summary>
    public IReadOnlyList<string> ImportManyWithIds(IEnumerable<MemoryNote> imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var candidates = imported.Take(MaxSavedNotes + 1).ToList();
        if (candidates.Count > MaxSavedNotes)
            throw new InvalidOperationException($"A memory backup may contain at most {MaxSavedNotes} notes.");

        var normalized = new List<MemoryNote>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in candidates)
        {
            if (note is null || !Guid.TryParseExact(note.Id, "N", out _))
                throw new InvalidOperationException("The memory backup contains an invalid note identifier.");

            var title = NormalizeTitle(note.Title);
            var content = NormalizeContent(note.Content);
            _ = NormalizeCategory(note.Category);
            ValidateRelationships(note);
            if (LooksLikeSecret(title + "\n" + content))
                throw new InvalidOperationException("The memory backup contains credential-like content. No notes were imported.");
            if (!seenIds.Add(note.Id))
                throw new InvalidOperationException("The memory backup contains a duplicate note identifier. No notes were imported.");

            normalized.Add(new MemoryNote
            {
                Id = note.Id,
                Title = title,
                Content = content,
                Category = NormalizeCategory(note.Category),
                IsArchived = note.IsArchived,
                CreatedAt = note.CreatedAt,
                UpdatedAt = note.UpdatedAt,
                Relationships = (note.Relationships ?? new List<MemoryNoteRelationship>())
                    .Select(link => new MemoryNoteRelationship { TargetNoteId = link.TargetNoteId, Label = link.Label })
                    .ToList()
            });
        }

        lock (_sync)
        {
            var notes = LoadBoundedNotes();
            var existingIds = notes.Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
            var toAdd = normalized.Where(note => !existingIds.Contains(note.Id)).ToList();
            if (notes.Count + toAdd.Count > MaxSavedNotes)
                throw new InvalidOperationException("There is not enough memory capacity to merge this backup. Existing notes were not changed.");

            if (toAdd.Count == 0)
                return Array.Empty<string>();

            var addedIds = toAdd.Select(note => note.Id).ToArray();
            notes.AddRange(toAdd);
            NormalizeRelationships(notes);
            _store.Save(notes);
            return addedIds;
        }
    }

    /// <summary>Compensates a multi-store import by removing only records added by ImportManyWithIds.</summary>
    public int RollbackImportedMany(IEnumerable<string> importedIds)
    {
        ArgumentNullException.ThrowIfNull(importedIds);
        var ids = importedIds
            .Where(id => Guid.TryParseExact(id, "N", out _))
            .ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
            return 0;

        lock (_sync)
        {
            var notes = LoadBoundedNotes();
            var removed = notes.RemoveAll(note => ids.Contains(note.Id));
            if (removed > 0)
            {
                NormalizeRelationships(notes);
                _store.Save(notes);
            }
            return removed;
        }
    }

    /// <summary>Creates or updates a symmetric, user-labelled relationship between two saved notes.</summary>
    public bool LinkNotes(string sourceNoteId, string targetNoteId, string label)
    {
        if (!Guid.TryParseExact(sourceNoteId, "N", out _) || !Guid.TryParseExact(targetNoteId, "N", out _) ||
            string.Equals(sourceNoteId, targetNoteId, StringComparison.Ordinal)) return false;
        var normalizedLabel = NormalizeRelationshipLabel(label);
        lock (_sync)
        {
            var notes = LoadBoundedNotes();
            var source = notes.FirstOrDefault(note => string.Equals(note.Id, sourceNoteId, StringComparison.Ordinal));
            var target = notes.FirstOrDefault(note => string.Equals(note.Id, targetNoteId, StringComparison.Ordinal));
            if (source is null || target is null) return false;
            source.Relationships ??= new List<MemoryNoteRelationship>();
            target.Relationships ??= new List<MemoryNoteRelationship>();
            var sourceHasLink = source.Relationships.Any(link => string.Equals(link.TargetNoteId, targetNoteId, StringComparison.Ordinal));
            var targetHasLink = target.Relationships.Any(link => string.Equals(link.TargetNoteId, sourceNoteId, StringComparison.Ordinal));
            if ((!sourceHasLink && source.Relationships.Count >= MaxRelationshipsPerNote) ||
                (!targetHasLink && target.Relationships.Count >= MaxRelationshipsPerNote))
                throw new InvalidOperationException($"A note can have at most {MaxRelationshipsPerNote} explicit relationships.");
            source.Relationships.RemoveAll(link => string.Equals(link.TargetNoteId, targetNoteId, StringComparison.Ordinal));
            target.Relationships.RemoveAll(link => string.Equals(link.TargetNoteId, sourceNoteId, StringComparison.Ordinal));
            source.Relationships.Add(new MemoryNoteRelationship { TargetNoteId = targetNoteId, Label = normalizedLabel });
            target.Relationships.Add(new MemoryNoteRelationship { TargetNoteId = sourceNoteId, Label = normalizedLabel });
            source.UpdatedAt = target.UpdatedAt = DateTimeOffset.Now;
            NormalizeRelationships(notes);
            _store.Save(notes);
            return true;
        }
    }

    /// <summary>Removes the relationship from both notes without deleting either note.</summary>
    public bool UnlinkNotes(string sourceNoteId, string targetNoteId)
    {
        if (string.IsNullOrWhiteSpace(sourceNoteId) || string.IsNullOrWhiteSpace(targetNoteId) ||
            string.Equals(sourceNoteId, targetNoteId, StringComparison.Ordinal)) return false;
        lock (_sync)
        {
            var notes = LoadBoundedNotes();
            var source = notes.FirstOrDefault(note => string.Equals(note.Id, sourceNoteId, StringComparison.Ordinal));
            var target = notes.FirstOrDefault(note => string.Equals(note.Id, targetNoteId, StringComparison.Ordinal));
            if (source is null || target is null) return false;
            var removed = source.Relationships.RemoveAll(link => string.Equals(link.TargetNoteId, targetNoteId, StringComparison.Ordinal)) > 0;
            removed |= target.Relationships.RemoveAll(link => string.Equals(link.TargetNoteId, sourceNoteId, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                source.UpdatedAt = target.UpdatedAt = DateTimeOffset.Now;
                _store.Save(notes);
            }
            return removed;
        }
    }

    public static string NormalizeRelationshipLabel(string? label)
    {
        var value = (label ?? string.Empty).Trim();
        if (value.Length is < 1 or > MaxRelationshipLabelCharacters || value.Any(char.IsControl))
            throw new InvalidOperationException($"Relationship labels must contain 1–{MaxRelationshipLabelCharacters} printable characters.");
        return value;
    }

    private static void ValidateRelationships(MemoryNote note)
    {
        var links = note.Relationships ?? new List<MemoryNoteRelationship>();
        if (links.Count > MaxRelationshipsPerNote)
            throw new InvalidOperationException($"A memory note may contain at most {MaxRelationshipsPerNote} explicit relationships.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (link is null || !Guid.TryParseExact(link.TargetNoteId, "N", out _) ||
                string.Equals(link.TargetNoteId, note.Id, StringComparison.Ordinal) || !seen.Add(link.TargetNoteId))
                throw new InvalidOperationException("A memory backup contains an invalid or duplicate relationship.");
            _ = NormalizeRelationshipLabel(link.Label);
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

    public static string NormalizeTitle(string? title)
    {
        var value = (title ?? string.Empty).Trim();
        if (value.Length is < 1 or > 120 || value.Any(char.IsControl))
            throw new InvalidOperationException("Memory titles must contain 1–120 printable characters.");
        return value;
    }

    public static string NormalizeContent(string? content)
    {
        var value = (content ?? string.Empty).Trim();
        if (value.Length is < 1 or > 12_000 ||
            value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new InvalidOperationException("Memory content must contain 1–12,000 characters and no unsupported control characters.");
        return value;
    }

    public static string NormalizeCategory(string? category)
    {
        var value = (category ?? string.Empty).Trim();
        if (value.Length == 0) return "General";
        if (value.Length > 40 || value.Any(char.IsControl))
            throw new InvalidOperationException("Memory categories must be 1–40 printable characters.");
        return value;
    }

    public bool Delete(string id, DateTimeOffset? expectedUpdatedAt = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (_sync)
        {
            var notes = LoadBoundedNotes();
            var existing = notes.FirstOrDefault(note => string.Equals(note.Id, id, StringComparison.Ordinal));
            if (existing is null || (expectedUpdatedAt.HasValue && existing.UpdatedAt != expectedUpdatedAt.Value))
                return false;
            notes.Remove(existing);
            NormalizeRelationships(notes);
            _store.Save(notes);
            return true;
        }
    }

    public IReadOnlyList<MemoryNote> Search(string query, int maxResults = 8, bool includeArchived = false)
    {
        query = (query ?? string.Empty).Trim();
        if (query.Length == 0) return Array.Empty<MemoryNote>();
        return GetAll(includeArchived)
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

        metadataChanged |= NormalizeRelationships(notes);
        if (notes.Count <= MaxSavedNotes)
        {
            if (metadataChanged) _store.Save(notes);
            return notes;
        }
        var bounded = KeepNewest(notes);
        NormalizeRelationships(bounded);
        // Apply retention, category migration, and relationship integrity to legacy/existing files.
        _store.Save(bounded);
        return bounded;
    }

    private static bool NormalizeRelationships(List<MemoryNote> notes)
    {
        var byId = notes.GroupBy(note => note.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var pairs = new Dictionary<(string First, string Second), string>();
        foreach (var note in notes)
        foreach (var link in note.Relationships ?? new List<MemoryNoteRelationship>())
        {
            if (link is null || string.IsNullOrWhiteSpace(link.TargetNoteId) ||
                string.Equals(note.Id, link.TargetNoteId, StringComparison.Ordinal) || !byId.ContainsKey(link.TargetNoteId))
                continue;
            string label;
            try { label = NormalizeRelationshipLabel(link.Label); }
            catch (InvalidOperationException) { continue; }
            var key = string.CompareOrdinal(note.Id, link.TargetNoteId) < 0
                ? (note.Id, link.TargetNoteId) : (link.TargetNoteId, note.Id);
            if (!pairs.ContainsKey(key)) pairs.Add(key, label);
        }

        var rebuilt = notes.ToDictionary(note => note.Id, _ => new List<MemoryNoteRelationship>(), StringComparer.Ordinal);
        foreach (var pair in pairs.OrderBy(item => item.Key.First, StringComparer.Ordinal).ThenBy(item => item.Key.Second, StringComparer.Ordinal))
        {
            var firstLinks = rebuilt[pair.Key.First];
            var secondLinks = rebuilt[pair.Key.Second];
            if (firstLinks.Count >= MaxRelationshipsPerNote || secondLinks.Count >= MaxRelationshipsPerNote) continue;
            firstLinks.Add(new MemoryNoteRelationship { TargetNoteId = pair.Key.Second, Label = pair.Value });
            secondLinks.Add(new MemoryNoteRelationship { TargetNoteId = pair.Key.First, Label = pair.Value });
        }

        var changed = false;
        foreach (var note in notes)
        {
            var normalized = rebuilt[note.Id];
            if (note.Relationships is null || note.Relationships.Count != normalized.Count ||
                note.Relationships.Where((link, index) => index >= normalized.Count || link is null ||
                    !string.Equals(link.TargetNoteId, normalized[index].TargetNoteId, StringComparison.Ordinal) ||
                    !string.Equals(link.Label, normalized[index].Label, StringComparison.Ordinal)).Any()) changed = true;
            note.Relationships = normalized;
        }
        return changed;
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
