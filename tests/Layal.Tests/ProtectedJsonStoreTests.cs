using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ProtectedJsonStoreTests
{
    [TestMethod]
    public void UnreadableExistingStoreIsPreservedBeforeItCanBeOverwritten()
    {
        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "state.dpapi");
        File.WriteAllText(path, "not-a-valid-dpapi-payload");
        var store = new ProtectedJsonStore<List<string>>(path);

        var loaded = store.Load(new List<string>());
        _ = store.Load(new List<string>());

        Assert.AreEqual(0, loaded.Count);
        var copies = Directory.GetFiles(temp.Path, "state.dpapi.unreadable-*");
        Assert.AreEqual(1, copies.Length, "Exactly one recovery copy is expected per store instance.");
        Assert.AreEqual("not-a-valid-dpapi-payload", File.ReadAllText(copies[0]));
    }

    [TestMethod]
    public void DifferentUnreadablePayloadsAreEachPreservedByTheSameStoreInstance()
    {
        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "changing-state.dpapi");
        const string firstPayload = "first unreadable payload";
        const string secondPayload = "second unreadable payload";
        File.WriteAllText(path, firstPayload);
        var store = new ProtectedJsonStore<List<string>>(path);

        _ = store.Load(new List<string>());
        File.WriteAllText(path, secondPayload);
        _ = store.Load(new List<string>());

        var copies = Directory.GetFiles(temp.Path, "changing-state.dpapi.unreadable-*");
        Assert.AreEqual(2, copies.Length, "A new corrupt payload must not be skipped just because this instance recovered an earlier payload.");
        CollectionAssert.AreEquivalent(
            new[] { firstPayload, secondPayload },
            copies.Select(File.ReadAllText).ToArray());
    }

    [TestMethod]
    public void LockedStoreFailsClosedInsteadOfReturningFallbackState()
    {
        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "locked.dpapi");
        const string original = "unreadable original payload";
        File.WriteAllText(path, original);
        var store = new ProtectedJsonStore<List<string>>(path);

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsExactly<IOException>(() => store.Load(new List<string>()));
        }

        Assert.AreEqual(original, File.ReadAllText(path), "A failed recovery copy must not permit the unreadable original to be replaced.");
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "locked.dpapi.unreadable-*").Length);
    }


    [TestMethod]
    public void JsonNullRootIsPreservedAsUnreadableState()
    {
        using var temp = new TestTempDirectory();
        var path = System.IO.Path.Combine(temp.Path, "null-state.dpapi");
        var producer = new DpapiSecretStore(path, "Layal encrypted JSON state");
        producer.Save("null");
        var originalBytes = File.ReadAllBytes(path);
        var store = new ProtectedJsonStore<List<string>>(path);

        var loaded = store.Load(new List<string>());

        Assert.AreEqual(0, loaded.Count);
        var copies = Directory.GetFiles(temp.Path, "null-state.dpapi.unreadable-*");
        Assert.AreEqual(1, copies.Length, "A JSON null root must be treated as corrupt and preserved.");
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(copies[0]),
            "The recovery copy must retain the original encrypted payload.");
    }

    [TestMethod]
    public void MissingStoreDoesNotCreateRecoveryCopies()
    {
        using var temp = new TestTempDirectory();
        var store = new ProtectedJsonStore<List<string>>(System.IO.Path.Combine(temp.Path, "absent.dpapi"));

        _ = store.Load(new List<string>());

        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "absent.dpapi.*").Length);
    }
}
