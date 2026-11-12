using System.Net;
using System.Text;
using System.Text.Json;
using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalAgentToolCallingTests
{
    [TestMethod]
    public async Task LocalAgentRunsCapabilityCheckToolAndFinalTurnWithoutTouchingCloudClient()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);

        var handler = new QueueJsonHandler(
            ToolCallResponse("layal_tool_call_probe"),
            ToolCallResponse("get_current_time"),
            FinalResponse("The local task completed."));
        using var localHttp = new HttpClient(handler);
        var local = new LocalAiService(localHttp);
        var cloud = new OpenAiCompatibleClient(_ =>
            throw new AssertFailedException("The cloud provider must not be contacted for a local task."));
        var router = new AgentModelClientRouter(cloud, local);
        var tools = new AgentToolExecutor(
            workspace,
            new MemoryService(Path.Combine(temp.Path, "memory.json")));
        var skills = new SkillPackageService(Path.Combine(temp.Path, "skills"));
        var history = new TaskHistoryService(Path.Combine(temp.Path, "task-history.json"));
        var orchestrator = new AgentOrchestrator(router, tools, skills, history);

        var task = await orchestrator.RunTaskAsync(
            "Tell me the current local time.",
            string.Empty,
            LocalAiService.DefaultModelId,
            string.Empty,
            Array.Empty<SkillRecord>(),
            (_, _, _) => Task.FromResult(false),
            _ => { },
            CancellationToken.None,
            interfaceLanguage: "en",
            useLocalAi: true);

        Assert.AreEqual("Completed", task.Status, string.Join(Environment.NewLine, task.Events));
        Assert.AreEqual("The local task completed.", task.Result);
        Assert.AreEqual(3, handler.Requests.Count, "Expected capability check, one tool call, and final response.");

        foreach (var request in handler.Requests)
        {
            Assert.AreEqual("http://127.0.0.1:11434/api/chat", request.Uri?.AbsoluteUri);
            Assert.IsNull(request.Authorization, "The local agent must not send a bearer/API key.");
        }

        using var followUp = JsonDocument.Parse(handler.Requests[2].Body);
        var messages = followUp.RootElement.GetProperty("messages");
        var assistantToolCall = messages.EnumerateArray().Single(message =>
            message.GetProperty("role").GetString() == "assistant" &&
            message.TryGetProperty("tool_calls", out _));
        var function = assistantToolCall.GetProperty("tool_calls")[0].GetProperty("function");
        Assert.AreEqual("get_current_time", function.GetProperty("name").GetString());
        Assert.AreEqual(JsonValueKind.Object, function.GetProperty("arguments").ValueKind,
            "Ollama requires tool arguments as a JSON object, not an OpenAI JSON string.");

        var toolResult = messages.EnumerateArray().Single(message => message.GetProperty("role").GetString() == "tool");
        Assert.AreEqual("get_current_time", toolResult.GetProperty("tool_name").GetString());
        Assert.IsFalse(toolResult.TryGetProperty("tool_call_id", out _),
            "Ollama tool results are linked by tool_name rather than OpenAI tool_call_id.");
    }

    [TestMethod]
    public async Task LocalAgentStopsWhenCapabilityProbeReturnsNoToolCallAndNeverFallsBackToCloud()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);

        var handler = new QueueJsonHandler(FinalResponse("I cannot call tools."));
        using var localHttp = new HttpClient(handler);
        var local = new LocalAiService(localHttp);
        var cloud = new OpenAiCompatibleClient(_ =>
            throw new AssertFailedException("A failed local capability check must not trigger cloud fallback."));
        var router = new AgentModelClientRouter(cloud, local);
        var tools = new AgentToolExecutor(
            workspace,
            new MemoryService(Path.Combine(temp.Path, "memory.json")));
        var orchestrator = new AgentOrchestrator(
            router,
            tools,
            new SkillPackageService(Path.Combine(temp.Path, "skills")),
            new TaskHistoryService(Path.Combine(temp.Path, "task-history.json")));

        var task = await orchestrator.RunTaskAsync(
            "Create a report using workspace tools.",
            string.Empty,
            LocalAiService.DefaultModelId,
            string.Empty,
            Array.Empty<SkillRecord>(),
            (_, _, _) => Task.FromResult(false),
            _ => { },
            CancellationToken.None,
            interfaceLanguage: "en",
            useLocalAi: true);

        Assert.AreEqual("Failed", task.Status);
        StringAssert.Contains(task.Result, "did not return a structured tool call");
        Assert.AreEqual(1, handler.Requests.Count, "The task must stop at the failed capability check.");
        Assert.IsFalse(task.Events.Any(line => line.Contains("Tool requested:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task LocalAgentRejectsCloudEndpointOrCredentialsBeforeSendingAnyRequest()
    {
        var handler = new QueueJsonHandler();
        using var http = new HttpClient(handler);
        var local = new LocalAiService(http);
        var request = new AgentModelRequest(
            AgentModelProvider.Local,
            LocalAiService.DefaultModelId,
            new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "test" }
            },
            new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject { ["type"] = "function" }
            },
            "https://provider.example/v1",
            "should-never-be-forwarded");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => local.AgentTurnAsync(request, CancellationToken.None));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    private static string ToolCallResponse(string name) =>
        JsonSerializer.Serialize(new
        {
            message = new
            {
                role = "assistant",
                content = "",
                tool_calls = new[]
                {
                    new
                    {
                        function = new
                        {
                            name,
                            arguments = new { }
                        }
                    }
                }
            },
            done = true
        });

    private static string FinalResponse(string content) =>
        JsonSerializer.Serialize(new
        {
            message = new { role = "assistant", content },
            done = true
        });

    private sealed record CapturedRequest(Uri? Uri, string? Authorization, string Body);

    private sealed class QueueJsonHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public QueueJsonHandler(params string[] responses) => _responses = new Queue<string>(responses);

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.RequestUri, request.Headers.Authorization?.ToString(), body));

            if (_responses.Count == 0)
                throw new InvalidOperationException("The test did not queue a response for this local request.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }
}
