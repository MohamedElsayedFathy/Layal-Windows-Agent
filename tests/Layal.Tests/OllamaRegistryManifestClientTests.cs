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
    [TestMethod]
    public async Task RequestsTheExactOfficialManifestWithoutFollowingRedirects()
    {
        var body = Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": 2,
              "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
              "config": { "mediaType": "application/vnd.ollama.image.model", "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "size": 1 },
              "layers": []
            }
            """);
        var handler = new StubHandler((request, _) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://registry.ollama.ai/v2/library/qwen3.5/manifests/2b", request.RequestUri!.AbsoluteUri);
            Assert.IsTrue(request.Headers.Accept.ToString().Contains(
                OllamaRegistryManifestPolicy.ManifestListMediaType, StringComparison.Ordinal));
            Assert.IsTrue(request.Headers.Accept.ToString().Contains(
                OllamaRegistryManifestPolicy.ManifestMediaType, StringComparison.Ordinal));
            Assert.AreEqual("identity", string.Join(",", request.Headers.AcceptEncoding));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(OllamaRegistryManifestPolicy.ManifestMediaType);
            response.Headers.TryAddWithoutValidation(
                "Docker-Content-Digest",
                "sha256:" + Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new OllamaRegistryManifestClient(http);

        var snapshot = await client.GetDefaultModelManifestAsync();

        Assert.AreEqual(OllamaRegistryManifestPolicy.ManifestMediaType, snapshot.MediaType);
        Assert.AreEqual(64, snapshot.ManifestDigest.Length);
    }

    [TestMethod]
    public async Task FailsClosedWhenRegistryRequiresAuthentication()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var http = new HttpClient(handler);
        var client = new OllamaRegistryManifestClient(http);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.GetDefaultModelManifestAsync());
    }

    [TestMethod]
    public async Task FailsClosedWhenRegistryAttemptsRedirect()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://attacker.example/manifest");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var client = new OllamaRegistryManifestClient(http);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => client.GetDefaultModelManifestAsync());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _response;

        public StubHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _response(request, cancellationToken);
        }
    }
}
