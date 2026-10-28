using System.Net;
using System.Text;
using System.Text.Json;
using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class AgentOrchestratorTests
{
    [DataTestMethod]
    [DataRow("", "Failed")]
    [DataRow("   ", "Failed")]
    [DataRow("The task is complete.", "Completed")]
    public async Task FinalResponseStatusReflectsWhetherModelReturnedContent(string responseContent, string expectedStatus)
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);

        var responseJson = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = responseContent
                    }
                }
            }
        });
        var client = new OpenAiCompatibleClient(_ => new HttpClient(new StaticResponseHandler(responseJson)));
        var tools = new AgentToolExecutor(
            workspace,
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")));
        var skills = new SkillPackageService(Path.Combine(temp.Path, "skills"));
        var history = new TaskHistoryService(Path.Combine(temp.Path, "task-history.json"));
        var orchestrator = new AgentOrchestrator(client, tools, skills, history);

        var task = await orchestrator.RunTaskAsync(
            "Run a deterministic test task",
            "https://provider.example/v1",
            "test-model",
            "test-key",
            Array.Empty<SkillRecord>(),
            (_, _, _) => Task.FromResult(true),
            _ => { },
            CancellationToken.None);

        Assert.AreEqual(expectedStatus, task.Status);
        if (expectedStatus == "Failed")
        {
            StringAssert.Contains(task.Result, "empty final answer");
            Assert.IsTrue(task.Events.Any(e => e.Contains("empty final answer", StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            Assert.AreEqual(responseContent, task.Result);
        }

        var persisted = history.GetRecent().SingleOrDefault(item => item.Id == task.Id);
        Assert.IsNotNull(persisted);
        Assert.AreEqual(expectedStatus, persisted.Status);
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
