using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class NativeAgentOrchestratorTests
{
    [TestMethod]
    public async Task UnsupportedGoalFailsClosedWithoutApprovalOrFileChanges()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));
        var approvalCalled = false;
        var events = new System.Collections.Generic.List<string>();

        var task = await orchestrator.RunTaskAsync(
            "Please autonomously refactor my entire codebase",
            (_, _, _) => { approvalCalled = true; return Task.FromResult(true); },
            events.Add,
            CancellationToken.None,
            "en");

        Assert.AreEqual("Failed", task.Status);
        Assert.IsFalse(approvalCalled, "An unknown intent must not reach any tool approval or execution.");
        Assert.AreEqual(0, Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).Length);
        StringAssert.Contains(task.Result, "Nothing was executed");
        Assert.IsTrue(events.Any(item => item.Contains("no model fallback", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(learning.GetRecentExperiences().Any(item =>
            item.Intent == NativeIntent.Unknown.ToString() && !item.Verified));
    }

    [TestMethod]
    public async Task CreateFileRequiresApprovalAndLocalReadBackBeforeCompletedStatus()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));
        var approvals = new System.Collections.Generic.List<string>();
        var events = new System.Collections.Generic.List<string>();

        var task = await orchestrator.RunTaskAsync(
            "create file notes.txt with content Hello Layal",
            (title, _, _) => { approvals.Add(title); return Task.FromResult(true); },
            events.Add,
            CancellationToken.None,
            "en");

        Assert.AreEqual("Completed", task.Status, task.Result);
        CollectionAssert.AreEqual(new[] { "Create file", "Read file locally" }, approvals);
        Assert.AreEqual("Hello Layal", File.ReadAllText(Path.Combine(workspace, "notes.txt")));
        CollectionAssert.Contains(task.Artifacts.ToArray(), "notes.txt");
        Assert.IsTrue(events.Any(item => item.Contains("Local verification passed", StringComparison.Ordinal)));
        Assert.IsTrue(learning.GetRecentExperiences().Any(item =>
            item.Intent == NativeIntent.CreateTextFile.ToString() && item.Verified));
    }

    [TestMethod]
    public async Task DecliningFileCreationLeavesNoArtifactOrDependentRead()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));
        var approvalCalls = 0;

        var task = await orchestrator.RunTaskAsync(
            "create file notes.txt with content Hello Layal",
            (_, _, _) => { approvalCalls++; return Task.FromResult(false); },
            _ => { },
            CancellationToken.None,
            "en");

        Assert.AreEqual("Stopped", task.Status);
        Assert.AreEqual(1, approvalCalls, "A declined creation must prevent the dependent verification read.");
        Assert.IsFalse(File.Exists(Path.Combine(workspace, "notes.txt")));
        Assert.AreEqual(0, task.Artifacts.Count);
        Assert.IsTrue(task.Result.Contains("declined", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DeclinedReadBackNeverReportsCreatedFileAsFullyVerified()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));
        var approvalCalls = 0;

        var task = await orchestrator.RunTaskAsync(
            "create file notes.txt with content Hello Layal",
            (_, _, _) =>
            {
                approvalCalls++;
                return Task.FromResult(approvalCalls == 1);
            },
            _ => { },
            CancellationToken.None,
            "en");

        Assert.AreEqual("CompletedWithWarnings", task.Status);
        Assert.AreEqual(2, approvalCalls, "Creation and local read-back require separate approvals.");
        Assert.IsTrue(File.Exists(Path.Combine(workspace, "notes.txt")));
        Assert.IsTrue(task.Result.Contains("تعذر التحقق", StringComparison.Ordinal));
        Assert.IsFalse(learning.GetRecentExperiences().Any(item =>
            item.Intent == NativeIntent.CreateTextFile.ToString() && item.Verified));
    }

    [TestMethod]
    public async Task PreCancelledTaskStartsNoApprovalOrFileOperation()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var approvalCalled = false;

        var task = await orchestrator.RunTaskAsync(
            "create file notes.txt with content Hello Layal",
            (_, _, _) => { approvalCalled = true; return Task.FromResult(true); },
            _ => { },
            cancellation.Token,
            "en");

        Assert.AreEqual("Cancelled", task.Status);
        Assert.IsFalse(approvalCalled);
        Assert.IsFalse(File.Exists(Path.Combine(workspace, "notes.txt")));
    }

    [TestMethod]
    public async Task ExplicitCommandSequenceIsFullyPlannedAndEachFileIsVerified()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));
        var approvals = new System.Collections.Generic.List<string>();
        var events = new System.Collections.Generic.List<string>();

        var task = await orchestrator.RunTaskAsync(
            "create file first.txt with content First value\ncreate file second.txt with content Second value",
            (title, _, _) => { approvals.Add(title); return Task.FromResult(true); },
            events.Add,
            CancellationToken.None,
            "en");

        Assert.AreEqual("Completed", task.Status, task.Result);
        Assert.AreEqual(4, approvals.Count, "Each create and each local read-back must receive a separate approval.");
        Assert.AreEqual("First value", File.ReadAllText(Path.Combine(workspace, "first.txt")));
        Assert.AreEqual("Second value", File.ReadAllText(Path.Combine(workspace, "second.txt")));
        CollectionAssert.AreEquivalent(new[] { "first.txt", "second.txt" }, task.Artifacts.ToArray());
        Assert.AreEqual(2, events.Count(item => item.Contains("Local verification passed", StringComparison.Ordinal)));
        Assert.IsTrue(events.Any(item => item.Contains("complete request into 2 explicit commands", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NaturalSequenceWordInsideFileContentIsPreservedLiterally()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));
        var approvalCalls = 0;

        var task = await orchestrator.RunTaskAsync(
            "create file sentence.txt with content First then list files",
            (_, _, _) => { approvalCalls++; return Task.FromResult(true); },
            _ => { },
            CancellationToken.None,
            "en");

        Assert.AreEqual("Completed", task.Status, task.Result);
        Assert.AreEqual(2, approvalCalls, "One create approval and one read-back approval are expected.");
        Assert.AreEqual("First then list files", File.ReadAllText(Path.Combine(workspace, "sentence.txt")));
        Assert.AreEqual(1, Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).Length,
            "A natural-language connector inside literal file content must never trigger another command.");
    }

    [TestMethod]
    public async Task SemicolonInsideFileContentIsPreservedLiterally()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));
        var approvalCalls = 0;

        var task = await orchestrator.RunTaskAsync(
            "create file sentence.txt with content first; second",
            (_, _, _) => { approvalCalls++; return Task.FromResult(true); },
            _ => { },
            CancellationToken.None,
            "en");

        Assert.AreEqual("Completed", task.Status, task.Result);
        Assert.AreEqual(2, approvalCalls);
        Assert.AreEqual("first; second", File.ReadAllText(Path.Combine(workspace, "sentence.txt")));
    }

    [TestMethod]
    public async Task UnknownCommandInSequenceRejectsWholePlanBeforeFirstSideEffect()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));
        var approvalCalled = false;

        var task = await orchestrator.RunTaskAsync(
            "create file first.txt with content First value\ndo something outside native capabilities",
            (_, _, _) => { approvalCalled = true; return Task.FromResult(true); },
            _ => { },
            CancellationToken.None,
            "en");

        Assert.AreEqual("Failed", task.Status);
        Assert.IsFalse(approvalCalled, "All commands must be parsed before the first approval or side effect.");
        Assert.IsFalse(File.Exists(Path.Combine(workspace, "first.txt")));
        Assert.AreEqual(0, task.Artifacts.Count);
    }

    [TestMethod]
    public async Task SixCreateCommandsFitTheCommandLimitIncludingVerificationActions()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));
        var approvals = 0;
        var commands = Enumerable.Range(1, 6)
            .Select(index => $"create file file-{index}.txt with content Item {index}");
        var goal = string.Join(Environment.NewLine, commands);

        var task = await orchestrator.RunTaskAsync(
            goal,
            (_, _, _) => { approvals++; return Task.FromResult(true); },
            _ => { },
            CancellationToken.None,
            "en");

        Assert.AreEqual("Completed", task.Status, task.Result);
        Assert.AreEqual(12, approvals, "Six creates each require a separate creation and read-back approval.");
        Assert.AreEqual(6, task.Artifacts.Count);
        Assert.AreEqual(6, Directory.GetFiles(workspace, "*.txt", SearchOption.TopDirectoryOnly).Length);
    }

    [TestMethod]
    public async Task SequenceLongerThanSixCommandsIsRejectedBeforeApproval()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));
        var approvalCalled = false;
        var commands = Enumerable.Range(1, 7)
            .Select(index => $"create file file-{index}.txt with content Item {index}");
        var goal = string.Join(Environment.NewLine, commands);

        var task = await orchestrator.RunTaskAsync(
            goal,
            (_, _, _) => { approvalCalled = true; return Task.FromResult(true); },
            _ => { },
            CancellationToken.None,
            "en");

        Assert.AreEqual("Failed", task.Status);
        Assert.IsFalse(approvalCalled);
        Assert.AreEqual(0, Directory.GetFiles(workspace, "*", SearchOption.AllDirectories).Length);
        StringAssert.Contains(task.Result, "at most 6 explicit commands");
    }

    [TestMethod]
    public async Task NonzeroDotnetBuildExitStopsDependentCommandsAndMarksTaskFailed()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "failing.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <NuGetAudit>false</NuGetAudit>
                <RestoreIgnoreFailedSources>true</RestoreIgnoreFailedSources>
              </PropertyGroup>
              <Target Name="FailNativeBuild" BeforeTargets="CoreCompile">
                <Exec Command="cmd /c exit 23" />
              </Target>
            </Project>
            """);

        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));
        var approvalCalls = 0;
        var task = await orchestrator.RunTaskAsync(
            "build failing.csproj" + Environment.NewLine +
            "create file must-not-exist.txt with content This must not run",
            (_, _, _) => { approvalCalls++; return Task.FromResult(true); },
            _ => { },
            CancellationToken.None,
            "en");

        Assert.AreEqual("Failed", task.Status, task.Result);
        Assert.AreEqual(1, approvalCalls, "The dependent file action must never reach its approval after build failure.");
        StringAssert.Contains(task.Result, "Exit code: ");
        Assert.IsFalse(task.Result.Contains("Exit code: 0", StringComparison.Ordinal),
            "A dotnet process with a nonzero exit code must not be reported as successful.");
        StringAssert.Contains(task.Result, "STDERR");
        Assert.IsFalse(File.Exists(Path.Combine(workspace, "must-not-exist.txt")));
        Assert.IsFalse(learning.GetRecentExperiences().Any(item =>
            item.Intent == "BuildProject" && item.Verified),
            "A nonzero process exit must never be recorded as a verified success.");
    }

    [TestMethod]
    public async Task ExternalProgramLaunchIsNotRecordedAsVerifiedBeforeObservation()
    {
        EnsureWindowsDpapi();

        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(workspace);
        var learning = new NativeLearningService(Path.Combine(temp.Path, "learning.dpapi"));
        var orchestrator = new NativeAgentOrchestrator(
            new NativeLanguageParser(learning),
            learning,
            new AgentToolExecutor(workspace, new MemoryService(Path.Combine(temp.Path, "memory.dpapi"))),
            new TaskHistoryService(Path.Combine(temp.Path, "tasks.dpapi")));

        var task = await orchestrator.RunTaskAsync(
            "open notepad",
            (_, _, _) => Task.FromResult(true),
            _ => { },
            CancellationToken.None,
            "en");

        Assert.AreEqual("CompletedWithWarnings", task.Status, task.Result);
        StringAssert.Contains(task.Result, "النتيجة الخارجية");

        var experience = learning.GetRecentExperiences(1).Single();
        Assert.AreEqual(nameof(NativeIntent.LaunchProgram), experience.Intent);
        Assert.IsFalse(experience.Verified,
            "Dispatching a program launch is not evidence that the requested external application state was observed.");

        const string marker = "Process ID:";
        var markerIndex = task.Result.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var processText = task.Result[(markerIndex + marker.Length)..].TrimStart();
            var processIdText = processText.Split(new[] { '.', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (int.TryParse(processIdText, out var processId))
            {
                try
                {
                    using var launched = Process.GetProcessById(processId);
                    launched.Kill(entireProcessTree: true);
                    launched.WaitForExit(5_000);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }

    private static void EnsureWindowsDpapi()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The native task history, memory, and learning stores use Windows DPAPI.");
    }
}
