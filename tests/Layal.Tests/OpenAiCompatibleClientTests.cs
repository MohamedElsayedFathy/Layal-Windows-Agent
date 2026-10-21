using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class OpenAiCompatibleClientTests
{
    [TestMethod]
    public async Task ProbeCallsModelsEndpointWithBearerAuthorization()
    {
        using var handler = new RecordingHandler(_ => JsonResponse("{\"data\":[]}"));
        var client = new OpenAiCompatibleClient(timeout => new HttpClient(handler, disposeHandler: false) { Timeout = timeout });

        var result = await client.ProbeAsync("https://provider.example/v1", "test-secret");

        Assert.IsTrue(result.Contains("successful", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("https://provider.example/v1/models", handler.LastRequestUri?.AbsoluteUri);
        Assert.AreEqual("Bearer", handler.LastAuthorizationScheme);
        Assert.AreEqual("test-secret", handler.LastAuthorizationParameter);
    }

    [TestMethod]
    public async Task ChatSendsModelAndParsesTextResponse()
    {
        using var handler = new RecordingHandler(_ => JsonResponse("{\"choices\":[{\"message\":{\"content\":\"Hello from test provider\"}}]}"));
        var client = new OpenAiCompatibleClient(timeout => new HttpClient(handler, disposeHandler: false) { Timeout = timeout });

        var reply = await client.ChatAsync("https://provider.example/v1", "test-model", "test-secret",
            new[] { new ChatMessage { Role = "user", Content = "Hello" } });

        Assert.AreEqual("Hello from test provider", reply);
        Assert.AreEqual("https://provider.example/v1/chat/completions", handler.LastRequestUri?.AbsoluteUri);
        StringAssert.Contains(handler.LastRequestBody ?? string.Empty, "test-model");
        StringAssert.Contains(handler.LastRequestBody ?? string.Empty, "Hello");
    }

    [TestMethod]
    public async Task AgentTurnParsesToolCalls()
    {
        const string response = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"get_current_time\",\"arguments\":\"{}\"}}]}}]}";
        using var handler = new RecordingHandler(_ => JsonResponse(response));
        var client = new OpenAiCompatibleClient(timeout => new HttpClient(handler, disposeHandler: false) { Timeout = timeout });
        var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "What time is it?" } };
        var tools = new JsonArray { new JsonObject { ["type"] = "function" } };

        var result = await client.AgentTurnAsync("https://provider.example/v1", "test-model", "test-secret", messages, tools);

        Assert.AreEqual(1, result.ToolCalls.Count);
        Assert.AreEqual("call-1", result.ToolCalls[0].Id);
        Assert.AreEqual("get_current_time", result.ToolCalls[0].Name);
        Assert.AreEqual("{}", result.ToolCalls[0].ArgumentsJson);
    }

    [TestMethod]
    public async Task ChatRejectsEmptyMessagesWithoutHttpRequest()
    {
        using var handler = new RecordingHandler(_ => JsonResponse("{}"));
        var client = new OpenAiCompatibleClient(timeout => new HttpClient(handler, disposeHandler: false) { Timeout = timeout });

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.ChatAsync("https://provider.example/v1", "test-model", "key", Array.Empty<ChatMessage>()));
        Assert.IsNull(handler.LastRequestUri);
    }

    [TestMethod]
    public async Task ProbeRejectsInvalidSchemeAndUrlCredentials()
    {
        using var handler = new RecordingHandler(_ => JsonResponse("{}"));
        var client = new OpenAiCompatibleClient(timeout => new HttpClient(handler, disposeHandler: false) { Timeout = timeout });

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.ProbeAsync("file:///tmp/v1", "key"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.ProbeAsync("https://user:password@provider.example/v1", "key"));
        Assert.IsNull(handler.LastRequestUri);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            if (request.Content is not null) LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
