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
