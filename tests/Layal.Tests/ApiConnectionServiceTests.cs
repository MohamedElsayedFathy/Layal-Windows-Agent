using System.Net;
using System.Text;
using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ApiConnectionServiceTests
{
    [TestMethod]
    public async Task TestAsyncBuildsRelativeUrlAndSendsBearerToken()
    {
        using var temp = new TestTempDirectory();
        using var handler = new RecordingHandler(_ => JsonResponse("{\"ok\":true}"));
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new ApiConnectionService(Path.Combine(temp.Path, "api-connections.dpapi"), client);
        var profile = new ApiConnectionProfile
        {
            Name = "Example",
            BaseUrl = "https://api.example.test/v1",
            AuthenticationType = "Bearer",
            AuthSecret = "test-secret"
        };

        var result = await service.TestAsync(profile, "users/me");

        Assert.AreEqual("https://api.example.test/v1/users/me", handler.LastRequestUri?.AbsoluteUri);
        Assert.AreEqual("GET", handler.LastMethod);
        Assert.AreEqual("Bearer", handler.LastAuthorizationScheme);
        Assert.AreEqual("test-secret", handler.LastAuthorizationParameter);
        Assert.AreEqual(200, result.StatusCode);
        StringAssert.Contains(result.ToDisplayText(), "HTTP 200 OK");
        Assert.IsFalse(result.ToDisplayText().Contains("test-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SavedConnectionSendsCustomHeaderQueryAndJsonBody()
    {
        using var temp = new TestTempDirectory();
        using var handler = new RecordingHandler(_ => JsonResponse("{\"id\":17}"));
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new ApiConnectionService(Path.Combine(temp.Path, "api-connections.dpapi"), client);
        service.Save(new ApiConnectionProfile
        {
            Name = "Demo app",
            BaseUrl = "https://api.example.test/v2/",
            AuthenticationType = "CustomHeader",
            AuthHeaderName = "X-API-Key",
            AuthSecret = "private-key",
            AdditionalHeadersJson = "{\"Notion-Version\":\"2022-06-28\",\"X-App-Context\":\"layal-test\"}"
        });

        var result = await service.SendAsync("demo APP", "POST", "records",
            "{\"page\":2,\"filter\":\"open items\"}", "{\"title\":\"Layal test\"}");

        Assert.AreEqual("https://api.example.test/v2/records?page=2&filter=open%20items", handler.LastRequestUri?.AbsoluteUri);
        Assert.AreEqual("POST", handler.LastMethod);
        Assert.AreEqual("private-key", handler.LastCustomHeader);
        Assert.AreEqual("2022-06-28", handler.LastNotionVersion);
        Assert.AreEqual("layal-test", handler.LastAppContext);
        StringAssert.Contains(handler.LastRequestBody ?? string.Empty, "Layal test");
        Assert.IsTrue(result.StatusCode is >= 200 and < 300);
        Assert.IsFalse(service.BuildToolDescription().Contains("private-key", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AdditionalHeadersCannotOverrideAuthenticationOrTransportHeaders()
    {
        using var temp = new TestTempDirectory();
        var service = new ApiConnectionService(Path.Combine(temp.Path, "api-connections.dpapi"));

        Assert.ThrowsExactly<InvalidOperationException>(() => service.Save(new ApiConnectionProfile
        {
            Name = "Bad header",
            BaseUrl = "https://api.example.test",
            AuthenticationType = "None",
            AdditionalHeadersJson = "{\"Authorization\":\"Bearer injected\"}"
        }));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.Save(new ApiConnectionProfile
        {
            Name = "Bad type",
            BaseUrl = "https://api.example.test",
            AuthenticationType = "None",
            AdditionalHeadersJson = "{\"X-Version\":42}"
        }));
        Assert.AreEqual(0, service.GetAll().Count);
    }

    [TestMethod]
    public async Task TestAsyncRejectsPathTraversalAndAbsoluteExternalUrls()
    {
        using var temp = new TestTempDirectory();
        using var handler = new RecordingHandler(_ => JsonResponse("{}"));
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new ApiConnectionService(Path.Combine(temp.Path, "api-connections.dpapi"), client);
        var profile = new ApiConnectionProfile
        {
            Name = "Example",
            BaseUrl = "https://api.example.test/v1",
            AuthenticationType = "None"
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.TestAsync(profile, "../admin"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.TestAsync(profile, "https://other.example.test/data"));
        Assert.IsNull(handler.LastRequestUri);
    }

    [TestMethod]
    public async Task GetRequestRejectsBodyBeforeSending()
    {
        using var temp = new TestTempDirectory();
        using var handler = new RecordingHandler(_ => JsonResponse("{}"));
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new ApiConnectionService(Path.Combine(temp.Path, "api-connections.dpapi"), client);
        service.Save(new ApiConnectionProfile { Name = "Demo", BaseUrl = "https://api.example.test", AuthenticationType = "None" });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.SendAsync("Demo", "GET", "users", "", "{\"id\":1}"));
        Assert.IsNull(handler.LastRequestUri);
    }

    [TestMethod]
    public void InvalidBaseUrlAndAuthTypesAreRejectedBeforeSaving()
    {
        using var temp = new TestTempDirectory();
        var service = new ApiConnectionService(Path.Combine(temp.Path, "api-connections.dpapi"));

        Assert.ThrowsExactly<InvalidOperationException>(() => service.Save(new ApiConnectionProfile
        {
            Name = "Bad URL",
            BaseUrl = "file:///C:/private",
            AuthenticationType = "None"
        }));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.Save(new ApiConnectionProfile
        {
            Name = "Bad auth",
            BaseUrl = "https://api.example.test",
            AuthenticationType = "OAuthMagic"
        }));
        Assert.AreEqual(0, service.GetAll().Count);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? LastMethod { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }
        public string? LastCustomHeader { get; private set; }
        public string? LastNotionVersion { get; private set; }
        public string? LastAppContext { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastMethod = request.Method.Method;
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            request.Headers.TryGetValues("X-API-Key", out var keys);
            LastCustomHeader = keys?.FirstOrDefault();
            request.Headers.TryGetValues("Notion-Version", out var notionVersion);
            LastNotionVersion = notionVersion?.FirstOrDefault();
            request.Headers.TryGetValues("X-App-Context", out var appContext);
            LastAppContext = appContext?.FirstOrDefault();
            if (request.Content is not null) LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
