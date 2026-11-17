using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalResourceSetupCoordinatorTests
{
    [TestMethod]
    public async Task ExecuteAsyncMarksResourceReadyOnlyAfterVerificationSucceeds()
    {
        using var temp = new TestTempDirectory();
        var manager = new LocalResourceManager(Path.Combine(temp.Path, "resource-state.json"));
        var coordinator = new LocalResourceSetupCoordinator(manager);
        var installRan = false;
        var verified = false;

        await coordinator.ExecuteAsync(
            LocalResourceIds.PyTorch,
            _ =>
            {
                installRan = true;
                return Task.CompletedTask;
            },
            _ => Task.FromResult(verified = installRan));

        Assert.IsTrue(installRan);
        Assert.IsTrue(verified);
        var state = (await manager.GetSnapshotAsync())
            .Single(item => item.ResourceId == LocalResourceIds.PyTorch);
        Assert.AreEqual(LocalResourceLifecycle.Ready, state.State);
    }

    [TestMethod]
    public async Task ExecuteAsyncMarksFailedWhenResourceHealthCheckFails()
    {
        using var temp = new TestTempDirectory();
        var manager = new LocalResourceManager(Path.Combine(temp.Path, "resource-state.json"));
        var coordinator = new LocalResourceSetupCoordinator(manager);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            coordinator.ExecuteAsync(
                LocalResourceIds.VoiceTut,
                _ => Task.CompletedTask,
                _ => Task.FromResult(false)));

        var state = (await manager.GetSnapshotAsync())
            .Single(item => item.ResourceId == LocalResourceIds.VoiceTut);
        Assert.AreEqual(LocalResourceLifecycle.Failed, state.State);
        StringAssert.Contains(state.ErrorMessage ?? string.Empty, LocalResourceIds.VoiceTut);
    }

    [TestMethod]
    public async Task ExecuteAsyncResetsLifecycleWhenUserCancelsSetup()
    {
        using var temp = new TestTempDirectory();
        using var cancellation = new CancellationTokenSource();
        var manager = new LocalResourceManager(Path.Combine(temp.Path, "resource-state.json"));
        var coordinator = new LocalResourceSetupCoordinator(manager);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            coordinator.ExecuteAsync(
                LocalResourceIds.OllamaRuntime,
                token =>
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                _ => Task.FromResult(true),
                cancellation.Token));

        var state = (await manager.GetSnapshotAsync())
            .Single(item => item.ResourceId == LocalResourceIds.OllamaRuntime);
        Assert.AreEqual(LocalResourceLifecycle.Pending, state.State);
        Assert.IsNull(state.ErrorMessage);
    }

    [TestMethod]
    public async Task EnsureReadyAsyncReconcilesAnExistingInstallIntoReadyState()
    {
        using var temp = new TestTempDirectory();
        var manager = new LocalResourceManager(Path.Combine(temp.Path, "resource-state.json"));
        var coordinator = new LocalResourceSetupCoordinator(manager);
        await manager.InitializeAsync();

        await coordinator.EnsureReadyAsync(
            LocalResourceIds.WhisperModel,
            _ => Task.FromResult(true));

        var state = (await manager.GetSnapshotAsync())
            .Single(item => item.ResourceId == LocalResourceIds.WhisperModel);
        Assert.AreEqual(LocalResourceLifecycle.Ready, state.State);
    }

    [TestMethod]
    public async Task ReportDownloadProgressAsyncPersistsOllamaLayerBytesAndEstimate()
    {
        using var temp = new TestTempDirectory();
        var manager = new LocalResourceManager(Path.Combine(temp.Path, "resource-state.json"));
        var coordinator = new LocalResourceSetupCoordinator(manager);
        await manager.BeginDownloadAsync(LocalResourceIds.ChatModel, null);

        await coordinator.ReportDownloadProgressAsync(
            LocalResourceIds.ChatModel,
            512L * 1024 * 1024,
            2L * 1024 * 1024 * 1024,
            8L * 1024 * 1024,
            192,
            CancellationToken.None);

        var state = (await coordinator.GetSnapshotAsync())
            .Single(item => item.ResourceId == LocalResourceIds.ChatModel);
        Assert.AreEqual(LocalResourceLifecycle.Downloading, state.State);
        Assert.AreEqual(512L * 1024 * 1024, state.BytesDownloaded);
        Assert.AreEqual(2L * 1024 * 1024 * 1024, state.TotalBytes);
        Assert.AreEqual(8L * 1024 * 1024, state.BytesPerSecond);
        Assert.AreEqual(192d, state.RemainingSeconds);
    }

    [TestMethod]
    public async Task DownloadAsyncPersistsRealByteProgressAndVerifiesBeforeReady()
    {
        using var temp = new TestTempDirectory();
        var payload = Encoding.UTF8.GetBytes("coordinator verified download payload");
        var definition = LocalResourceCatalog.GetRequired(LocalResourceIds.WhisperRuntime) with
        {
            Source = "https://downloads.example.test/whisper.bin",
            ExpectedSizeBytes = payload.LongLength,
            ExpectedSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()
        };
        var handler = new StubHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
                RequestMessage = request
            }));
        using var httpClient = new HttpClient(handler);
        using var downloader = new LocalResourceDownloadService(httpClient, new[] { "downloads.example.test" });
        var manager = new LocalResourceManager(Path.Combine(temp.Path, "resource-state.json"));
        var coordinator = new LocalResourceSetupCoordinator(manager, downloader);
        var destination = Path.Combine(temp.Path, "whisper.bin");

        await coordinator.DownloadAsync(definition, destination);

        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(destination));
        var state = (await manager.GetSnapshotAsync())
            .Single(item => item.ResourceId == LocalResourceIds.WhisperRuntime);
        Assert.AreEqual(LocalResourceLifecycle.Ready, state.State);
        Assert.AreEqual(payload.LongLength, state.BytesDownloaded);
        Assert.AreEqual(payload.LongLength, state.TotalBytes);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
