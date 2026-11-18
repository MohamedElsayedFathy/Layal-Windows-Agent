using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class OllamaRegistryManifestClientTests
{
    private const string ManifestBody =
        "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.docker.distribution.manifest.v2+json\",\"config\":{\"mediaType\":\"application/vnd.docker.container.image.v1+json\",\"digest\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"size\":42},\"layers\":[]}";

    [TestMethod]
    public async Task FetchesFixedOfficialManifestAndComputesTheBodySha256()
    {
        var body = Encoding.UTF8.GetBytes(ManifestBody);
        var digest = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        HttpRequestMessage? observedRequest = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            observedRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", "sha256:" + digest);
            return response;
        }));
        var sut = new OllamaRegistryManifestClient(client);

        var result = await sut.FetchDefaultModelManifestAsync();

        Assert.AreEqual("qwen3.5:2b", result.ModelId);
        Assert.AreEqual(digest, result.ManifestDigest);
        Assert.AreEqual(body.LongLength, result.ManifestSizeBytes);
        Assert.AreEqual(digest, result.ModelDigests[0]);
        Assert.AreEqual(
            "https://registry.ollama.ai/v2/library/qwen3.5/manifests/2b",
            observedRequest!.RequestUri!.AbsoluteUri);
        Assert.AreEqual(HttpMethod.Get, observedRequest.Method);
        Assert.IsTrue(observedRequest.Headers.Accept.ToString().Contains(
            "application/vnd.docker.distribution.manifest.v2+json",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExtractsAllowedChildManifestDigestsFromOllamaManifestList()
    {
        const string childDigest = "c97eb11d70b1acdc88af01eef566c1fe4f7fbe93eb1afc06871132f293ff425a";
        var manifestList = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":2,\"mediaType\":\"application/vnd.ollama.manifest.list.v2+json\"," +
            "\"manifests\":[{\"mediaType\":\"application/vnd.docker.distribution.manifest.v2+json\"," +
            "\"digest\":\"sha256:" + childDigest + "\",\"size\":123,\"runner\":\"llamacpp\",\"format\":\"gguf\"}]}");
        var parentDigest = Convert.ToHexString(SHA256.HashData(manifestList)).ToLowerInvariant();

        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(manifestList)
            };
            response.Headers.TryAddWithoutValidation("Docker-Content-Digest", "sha256:" + parentDigest);
            return response;
        }));
        var sut = new OllamaRegistryManifestClient(client);

        var result = await sut.FetchDefaultModelManifestAsync();

        Assert.AreEqual(parentDigest, result.ManifestDigest);
        Assert.AreEqual(2, result.ModelDigests.Count);
        Assert.IsTrue(result.ContainsModelDigest(childDigest));
        Assert.IsTrue(result.ContainsModelDigest(parentDigest));
        Assert.IsFalse(result.ContainsModelDigest(new string('a', 64)));
        Assert.AreEqual("application/vnd.ollama.manifest.list.v2+json", result.MediaType);
    }

    [TestMethod]
    public async Task RetriesManifestRequestWithBearerTokenAfterOllamaChallenge()
    {
        var requestCount = 0;
        var tokenProviderCalls = 0;
        using var client = new HttpClient(new StubHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                unauthorized.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Bearer",
                    "realm=\"https://registry.ollama.ai/v2/token\",service=\"ollama\",scope=\"-\""));
                return unauthorized;
            }

            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("phase-c-test-token", request.Headers.Authorization?.Parameter);
            return OkResponse();
        }));

        var sut = new OllamaRegistryManifestClient(
            client,
            (registryUri, challenge, _) =>
            {
                tokenProviderCalls++;
                Assert.AreEqual(OllamaRegistryManifestClient.DefaultManifestUri, registryUri);
                Assert.IsTrue(challenge.StartsWith("Bearer ", StringComparison.Ordinal));
                Assert.IsTrue(challenge.Contains("service=\"ollama\"", StringComparison.Ordinal));
                return Task.FromResult("phase-c-test-token");
            });

        var result = await sut.FetchDefaultModelManifestAsync();

        Assert.AreEqual(2, requestCount);
        Assert.AreEqual(1, tokenProviderCalls);
        Assert.AreEqual("qwen3.5:2b", result.ModelId);
    }

    [TestMethod]
    public async Task AppliesRequestTimeoutWhileReadingManifestBody()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new BlockingReadContent()
            }));
        var sut = new OllamaRegistryManifestClient(
            client,
            requestTimeout: TimeSpan.FromMilliseconds(100));
        using var safetyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var stopwatch = Stopwatch.StartNew();

        await AssertThrowsAsync<OperationCanceledException>(
            () => sut.FetchDefaultModelManifestAsync(safetyTimeout.Token));

        stopwatch.Stop();
        Assert.IsTrue(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            "The manifest body read ignored the configured Registry request timeout.");
    }

    [TestMethod]
    public async Task RejectsRegistryDigestHeaderThatDoesNotMatchTheBody()
    {
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var response = OkResponse();
            response.Headers.TryAddWithoutValidation(
                "Docker-Content-Digest", "sha256:" + new string('b', 64));
            return response;
        }));
        var sut = new OllamaRegistryManifestClient(client);

        await AssertThrowsAsync<System.IO.InvalidDataException>(
            () => sut.FetchDefaultModelManifestAsync());
    }

    [TestMethod]
    public async Task ComputesSha256FromExactHttpsManifestBytesWhenDigestHeaderIsAbsent()
    {
        var body = Encoding.UTF8.GetBytes(ManifestBody);
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
        }));
        var sut = new OllamaRegistryManifestClient(client);

        var result = await sut.FetchDefaultModelManifestAsync();

        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(),
            result.ManifestDigest);
    }

    [TestMethod]
    public async Task RejectsRedirectInsteadOfFollowingIt()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://example.invalid/manifest") }
            }));
        var sut = new OllamaRegistryManifestClient(client);

        await AssertThrowsAsync<HttpRequestException>(
            () => sut.FetchDefaultModelManifestAsync());
    }

    [TestMethod]
    public async Task RejectsUnknownManifestMediaType()
    {
        const string invalid = "{\"schemaVersion\":2,\"mediaType\":\"text/plain\"}";
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(invalid, Encoding.UTF8, "application/json")
        }));
        var sut = new OllamaRegistryManifestClient(client);

        await AssertThrowsAsync<System.IO.InvalidDataException>(
            () => sut.FetchDefaultModelManifestAsync());
    }

    [TestMethod]
    public async Task RejectsManifestBodiesLargerThanOneMiB()
    {
        var body = new byte[1024 * 1024 + 1];
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
        }));
        var sut = new OllamaRegistryManifestClient(client);

        await AssertThrowsAsync<System.IO.InvalidDataException>(
            () => sut.FetchDefaultModelManifestAsync());
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            Assert.Fail(
                $"Expected {typeof(TException).Name}, but received {exception.GetType().Name}: {exception.Message}");
            return;
        }

        Assert.Fail($"Expected {typeof(TException).Name}, but the action completed successfully.");
    }

    private static HttpResponseMessage OkResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(ManifestBody, Encoding.UTF8, "application/json")
    };

    private sealed class BlockingReadContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BlockingReadStream());

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new BlockingReadStream());
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_responseFactory(request));
    }
}
