using System;
using System.IO;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class DpapiSecretStoreTests
{
    [TestMethod]
    public void RestoreUnreadableCopyRestoresExactPreviousStoreBytes()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "provider-secret.dpapi");
        const string originalContents = "previous-encrypted-payload";
        File.WriteAllText(path, originalContents);
        var store = new DpapiSecretStore(path);

        var backupPath = store.PreserveUnreadableCopy();
        Assert.IsNotNull(backupPath, "The existing store should be preserved before replacement.");

        File.WriteAllText(path, "replacement-payload");
        store.RestoreUnreadableCopy(backupPath!);

        Assert.AreEqual(originalContents, File.ReadAllText(path));
        Assert.IsFalse(File.Exists(path + ".restore.tmp"));
        Assert.IsTrue(File.Exists(backupPath));
    }

    [TestMethod]
    public void RestoreUnreadableCopyRejectsFilesOutsideThisStoresBackupNamespace()
    {
        using var temp = new TestTempDirectory();
        var storePath = Path.Combine(temp.Path, "provider-secret.dpapi");
        var unrelatedPath = Path.Combine(temp.Path, "unrelated.backup");
        File.WriteAllText(unrelatedPath, "not a store backup");
        var store = new DpapiSecretStore(storePath);

        Assert.ThrowsExactly<ArgumentException>(() => store.RestoreUnreadableCopy(unrelatedPath));
        Assert.IsFalse(File.Exists(storePath));
    }
}
