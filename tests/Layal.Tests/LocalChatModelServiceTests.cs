using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalChatModelServiceTests
{
    [TestMethod]
    public void LocalChatEndpointAcceptsOnlyLoopbackHosts()
    {
        Assert.AreEqual(
            "http://127.0.0.1:11434/v1",
            LocalChatModelService.ValidateBaseUrl("http://127.0.0.1:11434/v1/"));
        Assert.AreEqual(
            "http://localhost:11434/v1",
            LocalChatModelService.ValidateBaseUrl("http://localhost:11434/v1"));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            LocalChatModelService.ValidateBaseUrl("https://example.com/v1"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            LocalChatModelService.ValidateBaseUrl("http://192.168.1.10:11434/v1"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            LocalChatModelService.ValidateBaseUrl("http://127.0.0.1:11434/v1?token=secret"));
    }

    [TestMethod]
    public void SettingsNormalizationNeverPersistsRemoteChatEndpoint()
    {
        Assert.AreEqual(
            LocalChatModelService.DefaultBaseUrl,
            LocalChatModelService.NormalizeBaseUrl("https://remote.example/v1"));
        Assert.AreEqual(
            LocalChatModelService.DefaultBaseUrl,
            LocalChatModelService.NormalizeBaseUrl(""));

        Assert.AreEqual("qwen2.5:7b", LocalChatModelService.NormalizeModelId(" qwen2.5:7b "));
        Assert.AreEqual(string.Empty, LocalChatModelService.NormalizeModelId(new string('x', 201)));
    }

    [TestMethod]
    public async Task LocalChatProbeDoesNotSendBearerAuthorization()
    {
        using var handler = new RecordingHandler();
        var client = new OpenAiCompatibleClient(_ => new HttpClient(handler, disposeHandler: false));
        var service = new LocalChatModelService(client);

        await service.TestConnectionAsync("http://127.0.0.1:11434/v1");

        Assert.AreEqual("http://127.0.0.1:11434/v1/models", handler.LastRequestUri?.AbsoluteUri);
        Assert.IsNull(handler.LastAuthorizationScheme);
    }

    [TestMethod]
    public async Task ModelSmokeTestPerformsInferenceOnlyAgainstLoopbackEndpoint()
    {
        using var handler = new ChatRecordingHandler();
        var client = new OpenAiCompatibleClient(_ => new HttpClient(handler, disposeHandler: false));
        var service = new LocalChatModelService(client);

        var reply = await service.RunModelSmokeTestAsync(
            LocalChatModelService.DefaultBaseUrl, "qwen3.5:2b");

        Assert.AreEqual("OK", reply);
        Assert.AreEqual("http://127.0.0.1:11434/v1/chat/completions", handler.LastRequestUri?.AbsoluteUri);
        StringAssert.Contains(handler.LastRequestBody ?? string.Empty, "\"model\":\"qwen3.5:2b\"");
        StringAssert.Contains(handler.LastRequestBody ?? string.Empty, "\"role\":\"user\"");
        Assert.IsNull(handler.LastAuthorizationScheme);
    }

    private sealed class ChatRecordingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            });
        }
    }
}
