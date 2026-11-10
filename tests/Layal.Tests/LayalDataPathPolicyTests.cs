using System;
using System.IO;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LayalDataPathPolicyTests
{
    [TestMethod]
    public void DefaultModePreservesExistingPerUserStorageLocations()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "LayalPolicyTests", "Local");
        var documents = Path.Combine(Path.GetTempPath(), "LayalPolicyTests", "Documents");

        var paths = LayalDataPathPolicy.Resolve(null, localAppData, documents);

        Assert.IsFalse(paths.IsolatedAcceptance);
        Assert.IsNull(paths.AcceptanceRoot);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(localAppData, "Layal")), paths.DataRoot);
        Assert.AreEqual(Path.GetFullPath(Path.Combine(documents, "LayalWorkspace")), paths.WorkspaceRoot);
    }

    [TestMethod]
    public void AcceptanceModeUsesSeparateDataAndWorkspaceRoots()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "LayalPolicyTests", Guid.NewGuid().ToString("N"));
        var localAppData = Path.Combine(basePath, "User", "Local");
        var documents = Path.Combine(basePath, "User", "Documents");
        var acceptanceRoot = Path.Combine(basePath, "AI", "LayalAcceptance", "session");

        var paths = LayalDataPathPolicy.Resolve(acceptanceRoot, localAppData, documents);

        Assert.IsTrue(paths.IsolatedAcceptance);
        Assert.AreEqual(Path.GetFullPath(acceptanceRoot), paths.AcceptanceRoot);
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(acceptanceRoot, "LocalAppData", "Layal")),
            paths.DataRoot);
        Assert.AreEqual(
            Path.GetFullPath(Path.Combine(acceptanceRoot, "Documents", "LayalWorkspace")),
            paths.WorkspaceRoot);
        Assert.AreNotEqual(Path.GetFullPath(Path.Combine(localAppData, "Layal")), paths.DataRoot);
        Assert.AreNotEqual(Path.GetFullPath(Path.Combine(documents, "LayalWorkspace")), paths.WorkspaceRoot);
        Assert.AreEqual(
            LayalDataPathPolicy.GetAcceptanceScopeSuffix(acceptanceRoot),
            LayalDataPathPolicy.GetAcceptanceScopeSuffix(Path.GetFullPath(acceptanceRoot)));
    }

    [TestMethod]
    public void AcceptanceModeRejectsDataRootInsidePersonalLayalData()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "LayalPolicyTests", "Local");
        var documents = Path.Combine(Path.GetTempPath(), "LayalPolicyTests", "Documents");
        var acceptanceRoot = Path.Combine(localAppData, "Layal");

        Assert.ThrowsExactly<ArgumentException>(() =>
            LayalDataPathPolicy.Resolve(acceptanceRoot, localAppData, documents));
    }

    [TestMethod]
    public void AcceptanceModeRejectsWorkspaceInsidePersonalLayalWorkspace()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "LayalPolicyTests", "Local");
        var documents = Path.Combine(Path.GetTempPath(), "LayalPolicyTests", "Documents");
        var acceptanceRoot = Path.Combine(documents, "LayalWorkspace");

        Assert.ThrowsExactly<ArgumentException>(() =>
            LayalDataPathPolicy.Resolve(acceptanceRoot, localAppData, documents));
    }

    [TestMethod]
    public void AcceptanceRootMustBeAbsolute()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            LayalDataPathPolicy.Resolve("relative\\acceptance", Path.GetTempPath(), Path.GetTempPath()));
    }
}
