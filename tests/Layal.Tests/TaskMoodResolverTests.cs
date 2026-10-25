using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class TaskMoodResolverTests
{
    [TestMethod]
    public void TaskLifecycleUsesPlanningAndThinkingStates()
    {
        Assert.AreEqual("planning", TaskMoodResolver.Resolve("Task accepted; building a bounded plan."));
        Assert.AreEqual("thinking", TaskMoodResolver.Resolve("Requesting model turn 2/8."));
        Assert.AreEqual("responding", TaskMoodResolver.Resolve("Model provided an intermediate note before requesting tools."));
    }

    [TestMethod]
    public void ToolsSelectDifferentWorkingExpressions()
    {
        Assert.AreEqual("researching", TaskMoodResolver.Resolve("12:00 Tool requested: list_workspace_files (1/12)."));
        Assert.AreEqual("reading", TaskMoodResolver.Resolve("12:00 Tool requested: read_text_file (1/12)."));
        Assert.AreEqual("writing", TaskMoodResolver.Resolve("12:00 Tool requested: create_text_file (1/12)."));
        Assert.AreEqual("coding", TaskMoodResolver.Resolve("12:00 Tool requested: run_dotnet_build (1/12)."));
        Assert.AreEqual("using_tool", TaskMoodResolver.Resolve("12:00 Tool requested: open_notepad (1/12)."));
    }

    [TestMethod]
    public void BuildAndTestResultsEnterValidationState()
    {
        Assert.AreEqual("validating", TaskMoodResolver.Resolve("12:00 Tool result recorded: run_dotnet_build."));
        Assert.AreEqual("validating", TaskMoodResolver.Resolve("12:00 Tool result recorded: run_dotnet_test."));
    }

    [TestMethod]
    public void FailureAndDeclinedApprovalAreNotShownAsSuccess()
    {
        Assert.AreEqual("error", TaskMoodResolver.Resolve("12:00 Tool failed: read_text_file; access denied."));
        Assert.AreEqual("error", TaskMoodResolver.Resolve(
            "12:00 Tool result recorded: read_text_file.",
            new[] { "12:00 Tool requested: read_text_file (1/12).", "12:00 Tool failed: read_text_file; access denied." }));
        Assert.AreEqual("warning", TaskMoodResolver.Resolve(
            "12:00 Tool result recorded: create_text_file.",
            new[] { "12:00 User declined the requested file creation." }));
        Assert.AreEqual("warning", TaskMoodResolver.Resolve("12:00 An action requires approval."));
    }

    [TestMethod]
    public void UnknownEventsDoNotReplaceTheCurrentMascotState()
    {
        Assert.IsNull(TaskMoodResolver.Resolve("12:00 Informational log line."));
        Assert.IsNull(TaskMoodResolver.Resolve(null));
    }
}
