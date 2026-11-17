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
    public void VoiceTutModelPinsEveryDownloadedBinaryFromItsImmutableRevision()
    {
        var model = LocalResourceCatalog.GetRequired(LocalResourceIds.VoiceTutModel);
        Assert.AreEqual(
            "b36bdf4416864d84f47ca77ce3decf34f6ab16cc",
            model.Version);
        Assert.IsTrue(model.Required);
        Assert.IsNull(model.ExpectedSizeBytes);
        Assert.IsNotNull(model.ExpectedFiles);
        CollectionAssert.AreEquivalent(
            new[] { "model.safetensors", "tokenizer.json" },
            model.ExpectedFiles!.Select(file => file.RelativePath).ToArray());

        var weights = model.ExpectedFiles.Single(file => file.RelativePath == "model.safetensors");
        Assert.AreEqual(2_450_344_144L, weights.ExpectedSizeBytes);
        Assert.AreEqual(
            "172ac1268fa98cb6253deb9f66fce1115e74e9b66012847b8483addad07a2b78",
            weights.ExpectedSha256);
        Assert.IsTrue(weights.HasPinnedSha256);

        var tokenizer = model.ExpectedFiles.Single(file => file.RelativePath == "tokenizer.json");
        Assert.AreEqual(11_423_986L, tokenizer.ExpectedSizeBytes);
        Assert.AreEqual(
            "408f669b7e2b045fdf54201d815bd364e6667dbd845115da81239c40bc6dcfd1",
            tokenizer.ExpectedSha256);
        Assert.IsTrue(tokenizer.HasPinnedSha256);
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
