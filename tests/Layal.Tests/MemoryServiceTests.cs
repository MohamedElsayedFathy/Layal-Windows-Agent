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
