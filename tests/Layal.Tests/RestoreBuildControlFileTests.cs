using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class RestoreBuildControlFileTests
{
    [TestMethod]
    public void RestoreAllowlistRejectsBuildControlFileNames()
    {
        foreach (var blocked in new[] { "global.json", "sub/nuget.config", "dotnet-tools.json", "packages.lock.json", "Directory.Build.props", "a/Directory.Packages.props", "GLOBAL.JSON" })
            Assert.IsFalse(WorkspaceSnapshotService.IsRestorableExtension(blocked), blocked);
        foreach (var allowed in new[] { "notes.txt", "settings.json", "sub/data.csv" })
            Assert.IsTrue(WorkspaceSnapshotService.IsRestorableExtension(allowed), allowed);
    }
}
