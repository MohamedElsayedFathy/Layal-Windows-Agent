using System.Net.Http;
using System.Reflection;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class OllamaLocalModelSetupServiceTests
{
    [TestMethod]
    public void PullHttpHandlerDisablesRedirectsCookiesAndProxy()
    {
        var factory = typeof(OllamaLocalModelSetupService).GetMethod(
            "CreatePullHttpHandler",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(factory);

        using var handler = (SocketsHttpHandler)factory.Invoke(null, null)!;
        Assert.IsFalse(handler.AllowAutoRedirect);
        Assert.IsFalse(handler.UseCookies);
        Assert.IsFalse(handler.UseProxy);
    }

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
