using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class MemoryServiceTests
{
    [TestMethod]
    public void SecretLikeContentIsRejectedBeforePersistence()
    {
        using var temp = new TestTempDirectory();
        var service = new MemoryService(System.IO.Path.Combine(temp.Path, "memory.dpapi"));

        Assert.ThrowsExactly<InvalidOperationException>(() => service.Save("Provider", string.Concat("sk-", "proj-", new string('a', 40))));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.Save("Password", "password=hunter2"));
    }

    [TestMethod]
    public void OrdinaryNamesContainingKeyPrefixesAreNotTreatedAsSecrets()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Saving requires Windows DPAPI in this test suite.");
        using var temp = new TestTempDirectory();
        var service = new MemoryService(System.IO.Path.Combine(temp.Path, "memory.dpapi"));

        var note = service.Save("People", "Meeting with Nakia and Mr. Aizawa tomorrow");

        Assert.AreEqual("People", note.Title);
        Assert.ThrowsExactly<InvalidOperationException>(() => service.Save("Cloud", "AKIA" + "ABCDEFGHIJKLMNOP"));
    }

    [TestMethod]
    public void EmptySearchReturnsNoResults()
    {
        using var temp = new TestTempDirectory();
        var service = new MemoryService(System.IO.Path.Combine(temp.Path, "memory.dpapi"));

        Assert.AreEqual(0, service.Search("   ").Count);
    }

    [TestMethod]
    public void SaveSearchUpdateAndDeleteRoundTripThroughDpapi()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows DPAPI round-trip must run on Windows.");
        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "memory.dpapi");
        var service = new MemoryService(path);

        var note = service.Save("Layal architecture", "Use a modular Windows architecture.");
        Assert.IsTrue(File.Exists(path));
        Assert.IsTrue(service.Search("modular").Any(n => n.Id == note.Id));

        var updated = service.Save("Layal architecture", "Keep the agent and UI separate.", note.Id);
        Assert.AreEqual(note.Id, updated.Id);
        Assert.AreEqual("Keep the agent and UI separate.", service.Search("separate").Single().Content);

        var reloaded = new MemoryService(path);
        Assert.AreEqual(1, reloaded.GetAll().Count);
        Assert.IsTrue(reloaded.Delete(note.Id));
        Assert.AreEqual(0, new MemoryService(path).GetAll().Count);
        Assert.IsFalse(reloaded.Delete(note.Id));
    }


    [TestMethod]
    public void LegacyMemoryJsonDefaultsToGeneralAndActive()
    {
        const string legacyJson = """{"Id":"legacy-note","Title":"Legacy","Content":"Old note"}""";
        var note = System.Text.Json.JsonSerializer.Deserialize<MemoryNote>(legacyJson)
            ?? throw new AssertFailedException("Legacy memory JSON did not deserialize.");

        Assert.AreEqual("General", note.Category);
        Assert.IsFalse(note.IsArchived);
    }

    [TestMethod]
    public void CategoryIsTrimmedBoundedAndDefaultsToGeneral()
    {
        Assert.AreEqual("General", MemoryService.NormalizeCategory(null));
        Assert.AreEqual("General", MemoryService.NormalizeCategory("  "));
        Assert.AreEqual("Research", MemoryService.NormalizeCategory("  Research  "));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            MemoryService.NormalizeCategory(new string('x', 41)));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            MemoryService.NormalizeCategory("bad\ncategory"));
    }

    [TestMethod]
    public void SavePersistsCategoryAndSearchCanFindIt()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "memory.dpapi");
        var service = new MemoryService(path);
        var note = service.Save("Architecture review", "Review the service boundaries.", category: "Engineering");

        var reloaded = new MemoryService(path);
        var saved = reloaded.GetAll().Single(item => item.Id == note.Id);

        Assert.AreEqual("Engineering", saved.Category);
        Assert.AreEqual(note.Id, reloaded.Search("engineering").Single().Id);

        var updatedWithoutCategory = reloaded.Save("Architecture review", "Revised boundaries.", note.Id);
        Assert.AreEqual("Engineering", updatedWithoutCategory.Category);
    }

    [TestMethod]
    public void ArchivedNotesAreHiddenFromActiveSearchAndCanBeRestored()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "memory.dpapi");
        var service = new MemoryService(path);
        var note = service.Save("Release checklist", "Verify the installer.", category: "Project");

        Assert.IsTrue(service.SetArchived(note.Id, true));
        Assert.AreEqual(0, service.GetAll().Count);
        Assert.AreEqual(0, service.Search("checklist").Count);
        var archived = new MemoryService(path).GetAll(includeArchived: true).Single();
        Assert.IsTrue(archived.IsArchived);
        Assert.AreEqual("Project", archived.Category);

        Assert.IsTrue(service.SetArchived(note.Id, false));
        var restored = new MemoryService(path).GetAll().Single();
        Assert.IsFalse(restored.IsArchived);
        Assert.AreEqual(note.Id, restored.Id);
        Assert.AreEqual(note.Id, service.Search("Project").Single().Id);
    }

    [TestMethod]
    public void ExportPreservesArchivedNotesAndTheirCategories()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new MemoryService(System.IO.Path.Combine(temp.Path, "memory.dpapi"));
        var note = service.Save("Archived project", "Old release notes.", category: "Archive");
        Assert.IsTrue(service.SetArchived(note.Id, true));

        var exported = service.ExportJson();

        StringAssert.Contains(exported, "Archived project");
        StringAssert.Contains(exported, "Archive");
        StringAssert.Contains(exported, "\"IsArchived\": true");
    }

    [TestMethod]
    public void ExplicitRelationshipsAreSymmetricNamedAndPersistedThroughDpapi()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "memory.dpapi");
        var service = new MemoryService(path);
        var source = service.Save("Release plan", "Plan the next release.");
        var target = service.Save("Release validation", "Validate the installer.");
        Assert.IsTrue(service.LinkNotes(source.Id, target.Id, "validates"));
        var reloaded = new MemoryService(path).GetAll(includeArchived: true);
        var savedSource = reloaded.Single(note => note.Id == source.Id);
        var savedTarget = reloaded.Single(note => note.Id == target.Id);
        Assert.AreEqual("validates", savedSource.Relationships.Single().Label);
        Assert.AreEqual(source.Id, savedTarget.Relationships.Single().TargetNoteId);
        Assert.AreEqual("validates", savedTarget.Relationships.Single().Label);
        Assert.IsTrue(new MemoryService(path).UnlinkNotes(source.Id, target.Id));
        var afterUnlink = new MemoryService(path).GetAll(includeArchived: true);
        Assert.AreEqual(0, afterUnlink.Single(note => note.Id == source.Id).Relationships.Count);
        Assert.AreEqual(0, afterUnlink.Single(note => note.Id == target.Id).Relationships.Count);
    }

    [TestMethod]
    public void DeletingNoteRemovesRelationshipsFromRemainingNotes()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "memory.dpapi");
        var service = new MemoryService(path);
        var first = service.Save("First", "First note.");
        var second = service.Save("Second", "Second note.");
        Assert.IsTrue(service.LinkNotes(first.Id, second.Id, "depends on"));
        Assert.IsTrue(service.Delete(second.Id));
        var remaining = new MemoryService(path).GetAll(includeArchived: true).Single();
        Assert.AreEqual(first.Id, remaining.Id);
        Assert.AreEqual(0, remaining.Relationships.Count);
    }

    [TestMethod]
    public void RelationshipValidationRejectsInvalidLabelsAndSelfLinks()
    {
        using var temp = new TestTempDirectory();
        Assert.ThrowsExactly<InvalidOperationException>(() => MemoryService.NormalizeRelationshipLabel("bad\nlabel"));
        Assert.ThrowsExactly<InvalidOperationException>(() => MemoryService.NormalizeRelationshipLabel(new string('x', MemoryService.MaxRelationshipLabelCharacters + 1)));
        Assert.AreEqual("supports", MemoryService.NormalizeRelationshipLabel("  supports  "));
        var service = new MemoryService(System.IO.Path.Combine(temp.Path, "memory.dpapi"));
        var note = new MemoryNote();
        Assert.IsFalse(service.LinkNotes(note.Id, note.Id, "self"));
    }

    [TestMethod]
    public void ExplicitRelationshipLimitIsEnforcedForEachNote()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var service = new MemoryService(System.IO.Path.Combine(temp.Path, "memory.dpapi"));
        var source = service.Save("Source", "Root note.");
        var targets = Enumerable.Range(0, MemoryService.MaxRelationshipsPerNote + 1)
            .Select(index => service.Save("Target " + index, "Related note " + index + "."))
            .ToArray();
        for (var index = 0; index < MemoryService.MaxRelationshipsPerNote; index++)
            Assert.IsTrue(service.LinkNotes(source.Id, targets[index].Id, "related"));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.LinkNotes(source.Id, targets[^1].Id, "related"));
    }

    [TestMethod]
    public void MemoryBackupPreservesExplicitRelationshipsAndRejectsMalformedOnes()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var sourceService = new MemoryService(System.IO.Path.Combine(temp.Path, "source.dpapi"));
        var first = sourceService.Save("Research", "Research note.");
        var second = sourceService.Save("Findings", "Findings note.");
        Assert.IsTrue(sourceService.LinkNotes(first.Id, second.Id, "informs"));
        var exported = sourceService.GetAll(includeArchived: true);
        var restoreService = new MemoryService(System.IO.Path.Combine(temp.Path, "restored.dpapi"));
        restoreService.ValidateImportMany(exported);
        Assert.AreEqual(2, restoreService.ImportMany(exported));
        Assert.AreEqual("informs", restoreService.GetAll(includeArchived: true).Single(note => note.Id == first.Id).Relationships.Single().Label);
        var malformed = new MemoryNote { Relationships = new() { new MemoryNoteRelationship { TargetNoteId = "not-a-guid", Label = "invalid" } } };
        Assert.ThrowsExactly<InvalidOperationException>(() => restoreService.ValidateImportMany(new[] { malformed }));
    }


    [TestMethod]
    public void MemoryIsEncryptedAtRestAndSurvivesServiceRecreation()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "memory-notes.dpapi");
        const string privateTitle = "Private profile marker 7F3A91";
        const string privateContent = "Personal note marker 9C2B64 must remain encrypted on disk.";

        var service = new MemoryService(path);
        var saved = service.Save(privateTitle, privateContent);

        Assert.IsTrue(File.Exists(path));
        var protectedPayload = File.ReadAllText(path);
        Assert.IsFalse(protectedPayload.Contains(privateTitle, StringComparison.Ordinal));
        Assert.IsFalse(protectedPayload.Contains(privateContent, StringComparison.Ordinal));

        // Recreate the service to model a full application restart under the same Windows identity.
        var restartedService = new MemoryService(path);
        var restored = restartedService.GetAll().Single(note => note.Id == saved.Id);
        Assert.AreEqual(privateTitle, restored.Title);
        Assert.AreEqual(privateContent, restored.Content);
    }

    [TestMethod]
    public void SavingBeyondRetentionLimitKeepsNewest100AcrossActiveAndArchivedNotes()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "memory-notes.dpapi");
        var now = DateTimeOffset.UtcNow;
        var seeded = Enumerable.Range(0, MemoryService.MaxSavedNotes)
            .Select(index => new MemoryNote
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = $"Seed {index:000}",
                Content = $"Seed content {index:000}",
                IsArchived = index < 25,
                CreatedAt = now.AddMinutes(index - MemoryService.MaxSavedNotes),
                UpdatedAt = now.AddMinutes(index - MemoryService.MaxSavedNotes)
            })
            .ToList();
        new ProtectedJsonStore<List<MemoryNote>>(path).Save(seeded);

        var service = new MemoryService(path);
        var newest = service.Save("Newest retained note", "A new note must be retained after applying the cap.");
        var all = new MemoryService(path).GetAll(includeArchived: true);

        Assert.AreEqual(MemoryService.MaxSavedNotes, all.Count);
        Assert.IsTrue(all.Any(note => note.Id == newest.Id));
        Assert.IsFalse(all.Any(note => note.Id == seeded[0].Id), "The oldest note must be evicted.");
        Assert.AreEqual(24, all.Count(note => note.IsArchived), "Retention must count archived notes too.");
        Assert.AreEqual(76, service.GetAll().Count, "The active view must exclude the 24 retained archived notes.");
    }


    [TestMethod]
    public void SearchCanIncludeArchivedNotesWithoutChangingTheDefaultActiveOnlyBehavior()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new MemoryService(System.IO.Path.Combine(temp.Path, "memory.dpapi"));
        var note = service.Save("Archived reference", "A note available only from the archive.");
        Assert.IsTrue(service.SetArchived(note.Id, true));

        Assert.AreEqual(0, service.Search("Archived reference").Count);
        Assert.AreEqual(note.Id, service.Search("Archived reference", includeArchived: true).Single().Id);
    }

    [TestMethod]
    public void StaleMemoryEditsAndDeletesAreRejectedWithoutCreatingOrDeletingNotes()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "memory.dpapi");
        var service = new MemoryService(path);
        var original = service.Save("Original title", "Original content.");
        var staleTimestamp = original.UpdatedAt.AddSeconds(-1);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            service.Save("Overwritten title", "Stale content.", original.Id, expectedUpdatedAt: staleTimestamp));
        Assert.IsFalse(service.Delete(original.Id, staleTimestamp));
        Assert.AreEqual("Original title", new MemoryService(path).GetAll().Single().Title);

        Assert.IsTrue(service.Delete(original.Id, original.UpdatedAt));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            service.Save("Accidental replacement", "This must not resurrect the deleted ID.", original.Id));
        Assert.AreEqual(0, new MemoryService(path).GetAll(includeArchived: true).Count);
    }

    [TestMethod]
    public void MemoryTitlesAndContentRejectControlCharactersOutsideSupportedTextFormatting()
    {
        Assert.AreEqual("A title", MemoryService.NormalizeTitle(" A title "));
        Assert.ThrowsExactly<InvalidOperationException>(() => MemoryService.NormalizeTitle("bad\ntitle"));
        Assert.ThrowsExactly<InvalidOperationException>(() => MemoryService.NormalizeTitle(" "));
        Assert.AreEqual("first line\nsecond line\tindented", MemoryService.NormalizeContent(" first line\nsecond line\tindented "));
        Assert.ThrowsExactly<InvalidOperationException>(() => MemoryService.NormalizeContent("bad\0content"));
        Assert.ThrowsExactly<InvalidOperationException>(() => MemoryService.NormalizeContent(new string('x', 12_001)));
    }

    [TestMethod]
    public void MemoryImportSkipsExistingIdsWithoutOverwritingAndRejectsDuplicateIds()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new MemoryService(System.IO.Path.Combine(temp.Path, "memory.dpapi"));
        var existing = service.Save("User version", "Keep this exact content.");
        var replacement = new MemoryNote
        {
            Id = existing.Id,
            Title = "Backup replacement",
            Content = "This must not overwrite the user's current note."
        };
        var addition = new MemoryNote
        {
            Title = "Imported note",
            Content = "This new note can be merged."
        };

        Assert.AreEqual(1, service.ImportMany(new[] { replacement, addition }));
        Assert.AreEqual("Keep this exact content.", service.GetAll(includeArchived: true).Single(note => note.Id == existing.Id).Content);
        Assert.IsTrue(service.GetAll(includeArchived: true).Any(note => note.Id == addition.Id));

        var duplicateA = new MemoryNote { Id = Guid.NewGuid().ToString("N"), Title = "A", Content = "One." };
        var duplicateB = new MemoryNote { Id = duplicateA.Id, Title = "B", Content = "Two." };
        Assert.ThrowsExactly<InvalidOperationException>(() => service.ValidateImportMany(new[] { duplicateA, duplicateB }));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.ImportMany(new[] { duplicateA, duplicateB }));
        Assert.AreEqual(2, service.GetAll(includeArchived: true).Count);
    }

    [TestMethod]
    public void ExistingMemoryFileIsPrunedToNewestRetentionLimit()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Encrypted memory persistence uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "memory.dpapi");
        var store = new ProtectedJsonStore<List<MemoryNote>>(path);
        var now = DateTimeOffset.UtcNow;
        var notes = Enumerable.Range(0, MemoryService.MaxSavedNotes + 5)
            .Select(i => new MemoryNote
            {
                Id = $"note-{i}",
                Title = $"Note {i}",
                Content = "bounded note",
                CreatedAt = now.AddMinutes(i),
                UpdatedAt = now.AddMinutes(i)
            })
            .ToList();
        store.Save(notes);

        var service = new MemoryService(path);
        var retained = service.GetAll();

        Assert.AreEqual(MemoryService.MaxSavedNotes, retained.Count);
        Assert.IsTrue(retained.Any(note => note.Id == $"note-{MemoryService.MaxSavedNotes + 4}"));
        Assert.IsFalse(retained.Any(note => note.Id == "note-0"));
        Assert.AreEqual(MemoryService.MaxSavedNotes, new ProtectedJsonStore<List<MemoryNote>>(path).Load(new List<MemoryNote>()).Count);
    }

    [TestMethod]
    public void ExportIsValidJsonAndIsExplicitlyPlaintext()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Memory persistence requires Windows DPAPI in this test suite.");
        using var temp = new TestTempDirectory();
        var service = new MemoryService(System.IO.Path.Combine(temp.Path, "memory.dpapi"));
        _ = service.Save("Project", "Layal release notes");

        var exported = service.ExportJson();

        StringAssert.Contains(exported, "Layal release notes");
        Assert.IsTrue(exported.StartsWith("[", StringComparison.Ordinal));
    }
}
