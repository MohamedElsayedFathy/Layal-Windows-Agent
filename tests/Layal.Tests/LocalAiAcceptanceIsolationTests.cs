using System;
using System.IO;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalAiAcceptanceIsolationTests
{
    [TestMethod]
    public async Task IsolatedAcceptanceUsesPrivateLocalAiRootAndDoesNotProbeOrInstallOllama()
    {
        var variable = LayalDataPathPolicy.AcceptanceRootEnvironmentVariable;
        var previousValue = Environment.GetEnvironmentVariable(variable);
        var acceptanceRoot = Path.Combine(
            Path.GetTempPath(),
            "LayalAcceptanceIsolation",
            Guid.NewGuid().ToString("N"));

        try
        {
            Environment.SetEnvironmentVariable(variable, acceptanceRoot);

            var expectedDataRoot = Path.GetFullPath(
                Path.Combine(acceptanceRoot, "LocalAppData", "Layal", "LocalAi"));
            Assert.AreEqual(expectedDataRoot, LocalAiService.DataRoot);

            var service = new LocalAiService();
            Assert.IsFalse(await service.IsReadyAsync(),
                "Acceptance must not connect to a user-installed Ollama server on loopback.");

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => service.EnsureReadyAsync(),
                "Acceptance must not start Ollama, download an installer, or install models.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previousValue);
        }
    }
}
