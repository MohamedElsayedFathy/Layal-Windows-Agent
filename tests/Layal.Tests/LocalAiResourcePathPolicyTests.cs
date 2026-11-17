using System;
using System.IO;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalAiResourcePathPolicyTests
{
    [TestMethod]
    public void EmptySettingKeepsTheExistingLocalAiFolder()
    {
        using var temp = new TestTempDirectory();
        var existing = Path.Combine(temp.Path, "LocalAi");
        Assert.AreEqual(Path.GetFullPath(existing), LocalAiResourcePathPolicy.Resolve("", existing));
    }

    [TestMethod]
    public void ResolvingTheExistingFolderDoesNotTreatItAsAnOverlapError()
    {
        using var temp = new TestTempDirectory();
        var existing = Path.Combine(temp.Path, "LocalAi");
        Assert.IsTrue(LocalAiResourcePathPolicy.SamePath(
            existing, LocalAiResourcePathPolicy.Resolve(existing, existing)));
    }

    [TestMethod]
    public void ResolveRejectsRelativeSavedResourceDirectory()
    {
        using var temp = new TestTempDirectory();
        Assert.ThrowsExactly<IOException>(() =>
            LocalAiResourcePathPolicy.Resolve(
                "relative-resource-folder",
                Path.Combine(temp.Path, "LocalAi")));
    }

    [TestMethod]
    public void ValidateLocalDirectoryRequiresAnAbsoluteLocalSubdirectory()
    {
        using var temp = new TestTempDirectory();
        var accepted = Path.Combine(temp.Path, "ollama-models");
        Assert.AreEqual(Path.GetFullPath(accepted),
            LocalAiResourcePathPolicy.ValidateLocalDirectory(accepted));
        Assert.ThrowsExactly<ArgumentException>(() =>
            LocalAiResourcePathPolicy.ValidateLocalDirectory("relative-model-folder"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            LocalAiResourcePathPolicy.ValidateLocalDirectory(Path.GetPathRoot(temp.Path)!));
    }

    [TestMethod]
    public void RejectsDriveRootAndRelativePaths()
    {
        using var temp = new TestTempDirectory();
        var root = Path.GetPathRoot(temp.Path)!;
        Assert.ThrowsExactly<ArgumentException>(() =>
            LocalAiResourcePathPolicy.Validate(root, Path.Combine(temp.Path, "LocalAi")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            LocalAiResourcePathPolicy.Validate("relative-resource-folder", Path.Combine(temp.Path, "LocalAi")));
    }

    [TestMethod]
    public void RejectsOverlappingSourceAndDestinationFolders()
    {
        using var temp = new TestTempDirectory();
        var source = Path.Combine(temp.Path, "LocalAi");
        var nested = Path.Combine(source, "new");
        Directory.CreateDirectory(source);
        Assert.ThrowsExactly<ArgumentException>(() =>
            LocalAiResourcePathPolicy.Validate(nested, source));
    }

    [TestMethod]
    public void AcceptsAnIndependentLocalDestinationFolder()
    {
        using var temp = new TestTempDirectory();
        var source = Path.Combine(temp.Path, "old", "LocalAi");
        var destination = Path.Combine(temp.Path, "new", "LayalResources");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var accepted = LocalAiResourcePathPolicy.Validate(destination, source);
        Assert.AreEqual(Path.GetFullPath(destination), accepted);
    }
}
