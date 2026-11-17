using System.Linq;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalResourceCatalogTests
{
    [TestMethod]
    public void PythonRuntimeHasPinnedOfficialX64InstallerFallback()
    {
        var python = LocalResourceCatalog.GetRequired(LocalResourceIds.PythonRuntime);
        Assert.AreEqual("3.11–3.13 (managed fallback 3.13.17)", python.Version);
        Assert.AreEqual(29_941_280L, python.ExpectedSizeBytes);
        Assert.AreEqual(
            "5da012101ba2e17113a9a1cd901e9ce1441086f2b7ecb5de06ec94163899a190",
            python.ExpectedSha256);
        Assert.IsTrue(python.Required);
        Assert.IsTrue(python.HasPinnedSha256);
    }

    [TestMethod]
    public void OllamaFallbackInstallerIsPinnedAndOptional()
    {
        var installer = LocalResourceCatalog.GetRequired(LocalResourceIds.OllamaRuntimeInstaller);
        Assert.AreEqual("0.40.16", installer.Version);
        Assert.AreEqual(1_857_663_944L, installer.ExpectedSizeBytes);
        Assert.AreEqual(
            "84db4f756ddc9e091142a85cdc0f7ae5db68bc9b3e075f114388b326f8ed912d",
            installer.ExpectedSha256);
        Assert.IsFalse(installer.Required);
        Assert.AreEqual(LocalResourceKind.Runtime, installer.Kind);
        Assert.IsTrue(installer.HasPinnedSha256);
    }

    [TestMethod]
    public void CatalogResourceIdentifiersRemainUnique()
    {
        var duplicates = LocalResourceCatalog.All
            .GroupBy(resource => resource.Id, System.StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        Assert.AreEqual(0, duplicates.Count());
    }
}
