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
    public void MissingStoreDoesNotCreateRecoveryCopies()
    {
        using var temp = new TestTempDirectory();
        var store = new ProtectedJsonStore<List<string>>(System.IO.Path.Combine(temp.Path, "absent.dpapi"));

        _ = store.Load(new List<string>());

        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "absent.dpapi.*").Length);
    }
}
