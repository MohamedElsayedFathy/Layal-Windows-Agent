using System.Net;
using System.Text;
using System.Text.Json;
using Layal.Views;
using System.Windows.Input;
using System.Windows;
using System.Threading;
using System.Runtime.ExceptionServices;
using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class AgentOrchestratorTests
{
    [TestMethod]
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

    [TestMethod]
    public async Task CreatedWorkspaceFileIsLinkedToTheTaskAndPersistedForWorkspaceHub()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The task history store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "reports"));

        var toolResponse = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new
                            {
                                id = "call_create_report",
                                type = "function",
                                function = new
                                {
                                    name = "create_text_file",
                                    arguments = JsonSerializer.Serialize(new
                                    {
                                        path = "reports/final-report.txt",
                                        content = "Verified task artifact"
                                    })
                                }
                            }
                        }
                    }
                }
            }
        });
        var finalResponse = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = "The report was created and linked to this task."
                    }
                }
            }
        });

        using var handler = new QueueResponseHandler(toolResponse, finalResponse);
        var client = new OpenAiCompatibleClient(_ => new HttpClient(handler, disposeHandler: false));
        var tools = new AgentToolExecutor(
            workspace,
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")));
        var skills = new SkillPackageService(Path.Combine(temp.Path, "skills"));
        var history = new TaskHistoryService(Path.Combine(temp.Path, "task-history.dpapi"));
        var orchestrator = new AgentOrchestrator(client, tools, skills, history);
        var approvalCount = 0;

        var task = await orchestrator.RunTaskAsync(
            "Create a report and associate its output with this task",
            "https://provider.example/v1",
            "test-model",
            "test-key",
            Array.Empty<SkillRecord>(),
            (_, preview, _) =>
            {
                approvalCount++;
                StringAssert.Contains(preview.Replace('\\', '/'), "reports/final-report.txt");
                return Task.FromResult(true);
            },
            _ => { },
            CancellationToken.None);

        Assert.AreEqual("Completed", task.Status, string.Join(Environment.NewLine, task.Events));
        Assert.AreEqual(1, approvalCount);
        CollectionAssert.AreEqual(new[] { "reports/final-report.txt" }, task.Artifacts);
        Assert.AreEqual("Verified task artifact",
            File.ReadAllText(Path.Combine(workspace, "reports", "final-report.txt")));

        var persisted = history.GetRecent(10).Single(item => item.Id == task.Id);
        CollectionAssert.AreEqual(new[] { "reports/final-report.txt" }, persisted.Artifacts);
        Assert.IsTrue(task.Events.Any(line => line.Contains("Linked workspace artifact", StringComparison.Ordinal)));
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task CancellingDuringFileApprovalDoesNotCreateTheFile()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The task history store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "reports"));
        var destination = Path.Combine(workspace, "reports", "cancelled.txt");
        var toolResponse = BuildToolCallResponse(
            "create_text_file",
            new { path = "reports/cancelled.txt", content = "must not be written" });

        using var handler = new QueueResponseHandler(toolResponse);
        var client = new OpenAiCompatibleClient(_ => new HttpClient(handler, disposeHandler: false));
        var tools = new AgentToolExecutor(
            workspace,
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")));
        var skills = new SkillPackageService(Path.Combine(temp.Path, "skills"));
        var history = new TaskHistoryService(Path.Combine(temp.Path, "task-history.dpapi"));
        var orchestrator = new AgentOrchestrator(client, tools, skills, history);
        using var cancellation = new CancellationTokenSource();

        var task = await orchestrator.RunTaskAsync(
            "Create a file but cancel while approval is open",
            "https://provider.example/v1",
            "test-model",
            "test-key",
            Array.Empty<SkillRecord>(),
            (_, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            },
            _ => { },
            cancellation.Token);

        Assert.AreEqual("Cancelled", task.Status);
        Assert.IsFalse(File.Exists(destination), "Cancellation must be checked before any file is written.");
        Assert.AreEqual(0, task.Artifacts.Count);
        Assert.AreEqual("Cancelled", history.GetRecent(10).Single(item => item.Id == task.Id).Status);
        Assert.AreEqual(1, handler.RequestCount);
    }


    [TestMethod]
    public async Task EscapeDuringFileApprovalCancelsTheTaskBeforeWritingOrStartingTheNextTool()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The task history store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "reports"));
        var destination = Path.Combine(workspace, "reports", "cancelled-by-escape.txt");
        var toolResponse = BuildToolCallsResponse(
            ("create_text_file", (object)new { path = "reports/cancelled-by-escape.txt", content = "must not be written" }, "call_create"),
            ("get_current_time", (object)new { }, "call_follow_up"));

        using var handler = new QueueResponseHandler(toolResponse);
        var client = new OpenAiCompatibleClient(_ => new HttpClient(handler, disposeHandler: false));
        var tools = new AgentToolExecutor(
            workspace,
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")));
        var skills = new SkillPackageService(Path.Combine(temp.Path, "skills"));
        var history = new TaskHistoryService(Path.Combine(temp.Path, "task-history.dpapi"));
        var orchestrator = new AgentOrchestrator(client, tools, skills, history);
        using var cancellation = new CancellationTokenSource();

        var task = await orchestrator.RunTaskAsync(
            "Create a file, then check the current time; cancel the first approval with Escape",
            "https://provider.example/v1",
            "test-model",
            "test-key",
            Array.Empty<SkillRecord>(),
            (_, details, token) => Task.FromResult(ShowApprovalDialogAndPressEscape(
                "Create file", details, token, cancellation.Cancel)),
            _ => { },
            cancellation.Token);

        Assert.AreEqual("Cancelled", task.Status, string.Join(Environment.NewLine, task.Events));
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsFalse(File.Exists(destination), "Escape must stop before the approved file is created.");
        Assert.AreEqual(0, task.Artifacts.Count, "A cancelled creation must not create an artifact reference.");
        Assert.IsFalse(task.Events.Any(line => line.Contains("Tool requested: get_current_time", StringComparison.OrdinalIgnoreCase)),
            "No later tool may start after Escape cancels the first approval.");
        Assert.AreEqual(1, handler.RequestCount, "The model must not be called again after task cancellation.");
        Assert.AreEqual("Cancelled", history.GetRecent(10).Single(item => item.Id == task.Id).Status);
    }

    [TestMethod]
    public async Task SavedRoutineStopsAfterADeclinedToolAndDoesNotStartLaterCallsFromSameModelTurn()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The task history store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "reports"));
        var destination = Path.Combine(workspace, "reports", "declined-routine.txt");
        var turn = BuildToolCallsResponse(
            ("create_text_file", (object)new { path = "reports/declined-routine.txt", content = "should not be created" }, "call_create"),
            ("get_current_time", (object)new { }, "call_time"));
        using var handler = new QueueResponseHandler(turn);
        var client = new OpenAiCompatibleClient(_ => new HttpClient(handler, disposeHandler: false));
        var tools = new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi")));
        var skills = new SkillPackageService(Path.Combine(temp.Path, "skills"));
        var history = new TaskHistoryService(Path.Combine(temp.Path, "task-history.dpapi"));
        var orchestrator = new AgentOrchestrator(client, tools, skills, history);
        var approvalCount = 0;

        var task = await orchestrator.RunTaskAsync(
            "Execute this saved routine in order and stop if any action is declined.",
            "https://provider.example/v1", "test-model", "test-key", Array.Empty<SkillRecord>(),
            (_, _, _) =>
            {
                approvalCount++;
                return Task.FromResult(false);
            },
            _ => { }, CancellationToken.None,
            stopAfterToolFailureOrDecline: true);

        Assert.AreEqual("Stopped", task.Status, string.Join(Environment.NewLine, task.Events));
        StringAssert.Contains(task.Result, "No later tool calls or steps were started");
        Assert.AreEqual(1, approvalCount);
        Assert.AreEqual(1, handler.RequestCount, "The model must not be called again after a declined routine step.");
        Assert.IsFalse(File.Exists(destination), "A declined file creation must have no side effect.");
        Assert.IsFalse(task.Events.Any(line => line.Contains("Tool requested: get_current_time", StringComparison.OrdinalIgnoreCase)),
            "A later tool returned in the same model turn must not start after the first declined action.");
        Assert.AreEqual("Stopped", history.GetRecent(10).Single(item => item.Id == task.Id).Status);
    }

    [TestMethod]
    public async Task MultiStepFileTaskKeepsOneArtifactReferenceAcrossCreateAndReplace()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The task history store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "reports"));

        var createResponse = BuildToolCallResponse(
            "create_text_file",
            new { path = "reports/summary.txt", content = "Draft report" },
            "call_create_report");
        var replaceResponse = BuildToolCallResponse(
            "replace_text_in_file",
            new { path = "reports/summary.txt", old_text = "Draft", new_text = "Final" },
            "call_update_report");
        var finalResponse = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = "The report was created and finalized."
                    }
                }
            }
        });

        using var handler = new QueueResponseHandler(createResponse, replaceResponse, finalResponse);
        var client = new OpenAiCompatibleClient(_ => new HttpClient(handler, disposeHandler: false));
        var tools = new AgentToolExecutor(
            workspace,
            new MemoryService(Path.Combine(temp.Path, "memory.dpapi")));
        var skills = new SkillPackageService(Path.Combine(temp.Path, "skills"));
        var history = new TaskHistoryService(Path.Combine(temp.Path, "task-history.dpapi"));
        var orchestrator = new AgentOrchestrator(client, tools, skills, history);
        var approvalCount = 0;

        var task = await orchestrator.RunTaskAsync(
            "Create a draft report then replace Draft with Final",
            "https://provider.example/v1",
            "test-model",
            "test-key",
            Array.Empty<SkillRecord>(),
            (_, preview, _) =>
            {
                approvalCount++;
                Assert.IsTrue(preview.Replace('\\', '/').Contains("reports/summary.txt", StringComparison.Ordinal));
                return Task.FromResult(true);
            },
            _ => { },
            CancellationToken.None);

        Assert.AreEqual("Completed", task.Status, string.Join(Environment.NewLine, task.Events));
        Assert.AreEqual(2, approvalCount);
        Assert.AreEqual(3, handler.RequestCount);
        Assert.AreEqual("Final report", File.ReadAllText(Path.Combine(workspace, "reports", "summary.txt")));
        CollectionAssert.AreEqual(new[] { "reports/summary.txt" }, task.Artifacts);

        var persisted = history.GetRecent(10).Single(item => item.Id == task.Id);
        CollectionAssert.AreEqual(new[] { "reports/summary.txt" }, persisted.Artifacts);
        Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(workspace, ".layal-backups")).Any(),
            "The approved replacement should keep the source backup.");
    }


    private static bool ShowApprovalDialogAndPressEscape(
        string title,
        string details,
        CancellationToken cancellationToken,
        Action cancelTask)
    {
        var approved = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dialog = new TaskApprovalWindow(false, title, details, cancelTask);
                dialog.Loaded += (_, _) =>
                {
                    var source = PresentationSource.FromVisual(dialog)
                        ?? throw new InvalidOperationException("The approval dialog has no presentation source.");
                    var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    };
                    dialog.RaiseEvent(escape);
                    Assert.IsTrue(escape.Handled, "Escape must be handled by the approval dialog.");
                };

                approved = dialog.ShowForApproval(cancellationToken);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        return approved;
    }

    private static string BuildToolCallsResponse(params (string Name, object Arguments, string CallId)[] calls) =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = (string?)null,
                        tool_calls = calls.Select(call => new
                        {
                            id = call.CallId,
                            type = "function",
                            function = new
                            {
                                name = call.Name,
                                arguments = JsonSerializer.Serialize(call.Arguments)
                            }
                        }).ToArray()
                    }
                }
            }
        });

    private static string BuildToolCallResponse(string toolName, object arguments, string callId = "call_test_tool") =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new
                            {
                                id = callId,
                                type = "function",
                                function = new
                                {
                                    name = toolName,
                                    arguments = JsonSerializer.Serialize(arguments)
                                }
                            }
                        }
                    }
                }
            }
        });

    private sealed class QueueResponseHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly Queue<string> _bodies = new(responseBodies);
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            RequestCount++;
            if (_bodies.Count == 0)
                throw new InvalidOperationException("The test model response queue was exhausted.");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/json")
            });
        }
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
