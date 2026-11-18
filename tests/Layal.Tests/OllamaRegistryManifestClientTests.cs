using System;
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
        Assert.AreEqual(digest, result.Digest);
        Assert.AreEqual(body.LongLength, result.ManifestSizeBytes);
        Assert.AreEqual(
            "https://registry.ollama.ai/v2/library/qwen3.5/manifests/2b",
            observedRequest!.RequestUri!.AbsoluteUri);
        Assert.AreEqual(HttpMethod.Get, observedRequest.Method);
        Assert.IsTrue(observedRequest.Headers.Accept.ToString().Contains(
            "application/vnd.docker.distribution.manifest.v2+json",
            StringComparison.Ordinal));
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

        await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(
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
            result.Digest);
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

        await Assert.ThrowsExceptionAsync<HttpRequestException>(
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

        await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(
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

        await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(
            () => sut.FetchDefaultModelManifestAsync());
    }

    private static HttpResponseMessage OkResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(ManifestBody, Encoding.UTF8, "application/json")
    };

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
