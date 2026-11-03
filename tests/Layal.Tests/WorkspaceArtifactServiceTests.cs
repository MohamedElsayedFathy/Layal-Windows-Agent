using System;
using System.IO;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class WorkspaceArtifactServiceTests
{
    [TestMethod]
    public void ResolvesOnlyExistingRegularFilesUsingCanonicalWorkspaceRelativePaths()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "reports"));
        var filePath = Path.Combine(workspace, "reports", "result.txt");
        File.WriteAllText(filePath, "result");
        var service = new WorkspaceArtifactService(workspace);

        Assert.AreEqual(
            Path.GetFullPath(filePath),
            service.ResolveExistingSafeFilePath("reports/result.txt"));
        Assert.ThrowsExactly<FileNotFoundException>(() =>
            service.ResolveExistingSafeFilePath("reports/missing.txt"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            service.ResolveExistingSafeFilePath("reports"));
    }

    [TestMethod]
    public void RejectsTraversalAbsoluteAndNonCanonicalPaths()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var outside = Path.Combine(temp.Path, "outside.txt");
        File.WriteAllText(outside, "outside");
        var service = new WorkspaceArtifactService(workspace);

        Assert.ThrowsExactly<ArgumentException>(() =>
            service.ResolveExistingSafeFilePath("../outside.txt"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            service.ResolveExistingSafeFilePath(outside));
        Assert.ThrowsExactly<ArgumentException>(() =>
            service.ResolveExistingSafeFilePath("folder//file.txt"));
        Assert.IsTrue(File.Exists(outside), "Rejected paths must not be touched.");
    }

    [TestMethod]
    public void RejectsWorkspaceRootReachedThroughAReparsePointAncestor()
    {
        using var temp = new TestTempDirectory();
        var realParent = Path.Combine(temp.Path, "real-parent");
        var workspace = Path.Combine(realParent, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "file.txt"), "content");
        var linkedParent = Path.Combine(temp.Path, "linked-parent");

        try
        {
            Directory.CreateSymbolicLink(linkedParent, realParent);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("The test environment does not permit creation of a directory symbolic link.");
        }

        var service = new WorkspaceArtifactService(Path.Combine(linkedParent, "workspace"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            service.ResolveExistingSafeFilePath("file.txt"));
        Assert.IsTrue(File.Exists(Path.Combine(workspace, "file.txt")),
            "Rejecting a workspace reached through a reparse point must not modify the original file.");
    }

    [TestMethod]
    public void RejectsReparsePointComponentsWhenTheOperatingSystemAllowsCreatingThem()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        var outside = Path.Combine(temp.Path, "outside");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "linked.txt"), "outside content");
        var link = Path.Combine(workspace, "linked-folder");

        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("The test environment does not permit creation of a directory symbolic link.");
        }

        var service = new WorkspaceArtifactService(workspace);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            service.ResolveExistingSafeFilePath("linked-folder/linked.txt"));
        Assert.IsTrue(File.Exists(Path.Combine(outside, "linked.txt")));
    }
}
