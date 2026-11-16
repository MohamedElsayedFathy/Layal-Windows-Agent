using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalResourceManagerTests
{
    [TestMethod]
    public void CatalogHasUniqueIdsAndPinsKnownWhisperArtifacts()
    {
        var ids = LocalResourceCatalog.All.Select(item => item.Id).ToArray();
        Assert.AreEqual(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(LocalResourceCatalog.All.All(item => item.Required));

        var runtime = LocalResourceCatalog.GetRequired(LocalResourceIds.WhisperRuntime);
        Assert.AreEqual(7_982_101L, runtime.ExpectedSizeBytes);
        Assert.AreEqual(
            "7d8be46ecd31828e1eb7a2ecdd0d6b314feafd82163038ab6092594b0a063539",
            runtime.ExpectedSha256);

        var model = LocalResourceCatalog.GetRequired(LocalResourceIds.WhisperModel);
        Assert.AreEqual(147_951_465L, model.ExpectedSizeBytes);
        Assert.AreEqual(
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
            model.ExpectedSha256);

        var voiceModel = LocalResourceCatalog.GetRequired(LocalResourceIds.VoiceTutModel);
        Assert.IsNull(voiceModel.ExpectedSizeBytes);
        Assert.IsNull(voiceModel.ExpectedSha256,
            "Do not invent a model size or hash when the exact artifact digest is not pinned.");
    }

    [TestMethod]
    public async Task ResourceStateProgressAndReadyStatusPersistAcrossManagerInstances()
    {
        using var temp = new TestTempDirectory();
        var statePath = Path.Combine(temp.Path, "LocalAi", "resource-state.json");
        var first = new LocalResourceManager(statePath);
        await first.InitializeAsync();

        var definition = LocalResourceCatalog.GetRequired(LocalResourceIds.WhisperRuntime);
        await first.BeginDownloadAsync(definition.Id, definition.ExpectedSizeBytes);
        await first.ReportDownloadProgressAsync(
            definition.Id, 4096, definition.ExpectedSizeBytes, 1024, 100);
        await first.BeginVerificationAsync(definition.Id);
        await first.MarkReadyAsync(definition.Id);

        var second = new LocalResourceManager(statePath);
        var snapshot = await second.GetSnapshotAsync();
        var restored = snapshot.Single(item => item.ResourceId == definition.Id);

        Assert.AreEqual(LocalResourceLifecycle.Ready, restored.State);
        Assert.AreEqual(4096L, restored.BytesDownloaded);
        Assert.AreEqual(definition.ExpectedSizeBytes, restored.TotalBytes);
        Assert.IsTrue(File.Exists(statePath));
    }

    [TestMethod]
    public async Task ReadyCannotBeMarkedBeforeVerificationOrInstallation()
    {
        using var temp = new TestTempDirectory();
        var manager = new LocalResourceManager(Path.Combine(temp.Path, "resource-state.json"));
        await manager.InitializeAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            manager.MarkReadyAsync(LocalResourceIds.WhisperModel));

        var state = await manager.GetSnapshotAsync();
        Assert.AreEqual(
            LocalResourceLifecycle.Pending,
            state.Single(item => item.ResourceId == LocalResourceIds.WhisperModel).State);
    }

    [TestMethod]
    public async Task ProgressCannotBeReportedOutsideDownloadingState()
    {
        using var temp = new TestTempDirectory();
        var manager = new LocalResourceManager(Path.Combine(temp.Path, "resource-state.json"));
        await manager.InitializeAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            manager.ReportDownloadProgressAsync(
                LocalResourceIds.WhisperModel, 20, 100, 10, 8));
    }

    [TestMethod]
    public async Task ManagerRejectsProgressBeyondPinnedTotalAndUnknownResourceIds()
    {
        using var temp = new TestTempDirectory();
        var manager = new LocalResourceManager(Path.Combine(temp.Path, "resource-state.json"));
        await manager.InitializeAsync();
        await manager.BeginDownloadAsync(LocalResourceIds.WhisperModel, 100);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            manager.ReportDownloadProgressAsync(
                LocalResourceIds.WhisperModel, 101, 100, 10, 0));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() =>
            manager.BeginDownloadAsync("unknown-resource", 100));
    }

    [TestMethod]
    public async Task CorruptPersistedStateFailsClosedInsteadOfBeingSilentlyOverwritten()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "resource-state.json");
        await File.WriteAllTextAsync(path, "{not-json");

        var manager = new LocalResourceManager(path);
        await Assert.ThrowsExactlyAsync<System.Text.Json.JsonException>(() =>
            manager.InitializeAsync());
    }
}

[TestClass]
public sealed class LocalResourceDownloadServiceTests
{
    private const string Host = "downloads.example.test";

    [TestMethod]
    public async Task DownloadVerifiesSha256BeforePublishingDestinationFile()
    {
        using var temp = new TestTempDirectory();
        var payload = Encoding.UTF8.GetBytes("verified resource payload");
        var resource = CreateResource(payload);
        var handler = new StubHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
                RequestMessage = request
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"stable-v1\"");
            return Task.FromResult(response);
        });

        using var client = new HttpClient(handler);
        using var downloader = new LocalResourceDownloadService(client, new[] { Host });
        var destination = Path.Combine(temp.Path, "resource.bin");

        await downloader.DownloadAsync(resource, destination);

        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(destination));
        Assert.IsFalse(File.Exists(destination + ".partial"));
        Assert.IsFalse(File.Exists(destination + ".partial.json"));
    }

    [TestMethod]
    public async Task DownloadResumesOnlyWithMatchingStrongEtagAndVerifiesCombinedBytes()
    {
        using var temp = new TestTempDirectory();
        var payload = Enumerable.Range(0, 400_000)
            .Select(value => (byte)(value % 251))
            .ToArray();
        var resource = CreateResource(payload);
        var destination = Path.Combine(temp.Path, "resumed.bin");
        using var cancellation = new CancellationTokenSource();

        var firstHandler = new StubHandler((request, _) =>
        {
            Assert.IsNull(request.Headers.Range);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ChunkedContent(payload, 4096),
                RequestMessage = request
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"resource-v1\"");
            return Task.FromResult(response);
        });

        using (var firstClient = new HttpClient(firstHandler))
        using (var firstDownloader = new LocalResourceDownloadService(firstClient, new[] { Host }))
        {
            var cancelAfterFirstChunk = new InlineProgress<LocalResourceDownloadProgress>(value =>
            {
                if (value.DownloadedBytes > 0)
                    cancellation.Cancel();
            });

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                firstDownloader.DownloadAsync(resource, destination, cancelAfterFirstChunk, cancellation.Token));
        }

        var partialPath = destination + ".partial";
        var partialLength = new FileInfo(partialPath).Length;
        Assert.IsTrue(partialLength > 0 && partialLength < payload.Length);
        Assert.IsTrue(File.Exists(partialPath + ".json"));

        var secondHandler = new StubHandler((request, _) =>
        {
            Assert.AreEqual(partialLength, request.Headers.Range?.Ranges.Single().From);
            Assert.AreEqual("\"resource-v1\"", request.Headers.IfRange?.EntityTag?.ToString());

            var remaining = payload[(int)partialLength..];
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(remaining),
                RequestMessage = request
            };
            response.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(partialLength, payload.Length - 1, payload.Length);
            response.Headers.ETag = new EntityTagHeaderValue("\"resource-v1\"");
            return Task.FromResult(response);
        });

        using var secondClient = new HttpClient(secondHandler);
        using var secondDownloader = new LocalResourceDownloadService(secondClient, new[] { Host });
        await secondDownloader.DownloadAsync(resource, destination);

        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(destination));
        Assert.IsFalse(File.Exists(partialPath));
        Assert.IsFalse(File.Exists(partialPath + ".json"));
    }

    [TestMethod]
    public async Task DownloadRejectsIncorrectSha256AndDoesNotPublishFile()
    {
        using var temp = new TestTempDirectory();
        var payload = Encoding.UTF8.GetBytes("tampered payload");
        var resource = CreateResource(payload) with { ExpectedSha256 = new string('0', 64) };
        var handler = new StubHandler((request, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
                RequestMessage = request
            }));

        using var client = new HttpClient(handler);
        using var downloader = new LocalResourceDownloadService(client, new[] { Host });
        var destination = Path.Combine(temp.Path, "tampered.bin");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            downloader.DownloadAsync(resource, destination));

        Assert.IsFalse(File.Exists(destination));
        Assert.IsFalse(File.Exists(destination + ".partial"));
    }

    [TestMethod]
    public async Task DownloadRejectsUnapprovedHostsAndNonHttpsUrlsBeforeNetworkAccess()
    {
        using var temp = new TestTempDirectory();
        var payload = Encoding.UTF8.GetBytes("payload");
        var resource = CreateResource(payload) with
        {
            Source = "http://" + Host + "/resource.bin"
        };
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        using var downloader = new LocalResourceDownloadService(client, new[] { Host });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            downloader.DownloadAsync(resource, Path.Combine(temp.Path, "resource.bin")));
    }

    [TestMethod]
    public async Task DownloadRefusesCatalogEntryWithoutPinnedSizeAndHash()
    {
        using var temp = new TestTempDirectory();
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        using var downloader = new LocalResourceDownloadService(client, new[] { Host });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            downloader.DownloadAsync(
                LocalResourceCatalog.GetRequired(LocalResourceIds.VoiceTutModel),
                Path.Combine(temp.Path, "model.safetensors")));
    }

    private static LocalResourceDefinition CreateResource(byte[] payload)
    {
        return new LocalResourceDefinition(
            "test-resource",
            "Test resource",
            "1",
            "https://" + Host + "/resource.bin",
            LocalResourceKind.Runtime,
            true,
            payload.LongLength,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            "Unit test SHA-256");
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class ChunkedContent(byte[] bytes, int chunkSize) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.LongLength;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ChunkedReadStream(bytes, chunkSize));
    }

    private sealed class ChunkedReadStream(byte[] bytes, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.LongLength;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var available = Math.Min(Math.Min(count, chunkSize), bytes.Length - _position);
            if (available <= 0) return 0;
            Array.Copy(bytes, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = Math.Min(Math.Min(buffer.Length, chunkSize), bytes.Length - _position);
            if (available <= 0) return ValueTask.FromResult(0);
            bytes.AsMemory(_position, available).CopyTo(buffer);
            _position += available;
            return ValueTask.FromResult(available);
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
