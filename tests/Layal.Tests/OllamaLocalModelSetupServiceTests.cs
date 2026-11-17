using System.Reflection;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class OllamaLocalModelSetupServiceTests
{
    [TestMethod]
    public void DefaultSetupServiceWiresTheVerifiedResourceDownloader()
    {
        var service = new OllamaLocalModelSetupService();
        var coordinatorField = typeof(OllamaLocalModelSetupService).GetField(
            "_resourceCoordinator",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(coordinatorField);

        var coordinator = coordinatorField!.GetValue(service);
        Assert.IsNotNull(coordinator);

        var downloaderField = typeof(LocalResourceSetupCoordinator).GetField(
            "_downloader",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(downloaderField);

        Assert.IsNotNull(
            downloaderField!.GetValue(coordinator),
            "The default Ollama setup path must support the pinned installer fallback when WinGet is unavailable.");
    }
}
