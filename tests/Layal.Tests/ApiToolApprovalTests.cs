using System.Net;
using System.Text;
using System.Text.Json;
using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ApiToolApprovalTests
{
    [TestMethod]
    public async Task DeclinedApiCallDoesNotSendRequest()
    {
        using var temp = new TestTempDirectory();
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var connections = new ApiConnectionService(Path.Combine(temp.Path, "api-connections.dpapi"), client);
        connections.Save(new ApiConnectionProfile
        {
            Name = "Demo CRM",
            BaseUrl = "https://crm.example.test/v1",
            AuthenticationType = "None",
            Instructions = "GET contacts; POST notes."
        });
        var executor = new AgentToolExecutor(temp.Path, new MemoryService(Path.Combine(temp.Path, "memory.dpapi")), connections);
        var approvalCount = 0;
        var args = JsonSerializer.Serialize(new
        {
            connection_name = "Demo CRM",
            method = "POST",
            path = "notes",
            query_json = "",
            body_json = "{\"text\":\"test note\"}"
        });

        var result = await executor.ExecuteAsync("call_app_api", args,
            (title, preview, token) =>
            {
                approvalCount++;
                StringAssert.Contains(title, "API");
                StringAssert.Contains(preview, "POST");
                StringAssert.Contains(preview, "/v1/notes");
                return Task.FromResult(false);
            }, _ => { }, CancellationToken.None);

        Assert.AreEqual(1, approvalCount);
        StringAssert.Contains(result, "declined");
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task ApprovedApiCallSendsExactlyOneRequestAndReturnsHttpResult()
    {
        using var temp = new TestTempDirectory();
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var connections = new ApiConnectionService(Path.Combine(temp.Path, "api-connections.dpapi"), client);
        connections.Save(new ApiConnectionProfile
        {
            Name = "Demo CRM",
            BaseUrl = "https://crm.example.test/v1",
            AuthenticationType = "Bearer",
            AuthSecret = "never-shared-token",
            Instructions = "GET contacts; POST notes."
        });
        var executor = new AgentToolExecutor(temp.Path, new MemoryService(Path.Combine(temp.Path, "memory.dpapi")), connections);
        var approvalCount = 0;
        var reportCount = 0;
        var args = JsonSerializer.Serialize(new
        {
            connection_name = "Demo CRM",
            method = "GET",
            path = "contacts",
            query_json = "{\"limit\":5}",
            body_json = ""
        });

        var result = await executor.ExecuteAsync("call_app_api", args,
            (title, preview, token) =>
            {
                approvalCount++;
                StringAssert.Contains(preview, "/v1/contacts?limit=5");
                Assert.IsFalse(preview.Contains("never-shared-token", StringComparison.Ordinal));
                return Task.FromResult(true);
            }, _ => reportCount++, CancellationToken.None);

        Assert.AreEqual(1, approvalCount);
        Assert.AreEqual(1, handler.RequestCount);
        Assert.AreEqual("https://crm.example.test/v1/contacts?limit=5", handler.LastRequestUri?.AbsoluteUri);
        Assert.AreEqual("Bearer", handler.LastAuthScheme);
        Assert.AreEqual("never-shared-token", handler.LastAuthValue);
        Assert.AreEqual(1, reportCount);
        StringAssert.Contains(result, "HTTP 201 Created");
        Assert.IsFalse(result.Contains("never-shared-token", StringComparison.Ordinal));

        var definition = executor.GetToolDefinitions().First(x => x?["function"]?["name"]?.GetValue<string>() == "call_app_api");
        var description = definition?["function"]?["description"]?.GetValue<string>() ?? string.Empty;
        StringAssert.Contains(description, "Demo CRM");
        StringAssert.Contains(description, "GET contacts; POST notes");
        Assert.IsFalse(description.Contains("never-shared-token", StringComparison.Ordinal));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthScheme { get; private set; }
        public string? LastAuthValue { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            LastAuthScheme = request.Headers.Authorization?.Scheme;
            LastAuthValue = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"created\":true}", Encoding.UTF8, "application/json")
            });
        }
    }
}
