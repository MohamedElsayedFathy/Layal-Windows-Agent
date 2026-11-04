using System;
using System.IO;
using System.Linq;
using Layal.Models;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class SavedRoutineServiceTests
{
    [TestMethod]
    public void SavedRoutinesPersistAcrossServiceInstancesAndCanBeDeleted()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The routine store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "routines.dpapi");
        var service = new SavedRoutineService(path);
        var saved = service.Create("Morning review", "Read my calendar and summarize today's priorities.");

        var reloaded = new SavedRoutineService(path);
        var all = reloaded.GetAll();

        Assert.AreEqual(1, all.Count);
        Assert.AreEqual(saved.Id, all[0].Id);
        Assert.AreEqual("Morning review", all[0].Name);
        Assert.AreEqual("Read my calendar and summarize today's priorities.", all[0].Goal);
        Assert.IsTrue(reloaded.Delete(saved.Id));
        Assert.AreEqual(0, service.GetAll().Count);
    }

    [TestMethod]
    public void SavedRoutineCanBeEditedWithoutChangingItsCreationTime()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The routine store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "routines.dpapi");
        var service = new SavedRoutineService(path);
        var original = service.Create("Daily brief", "Summarize today's calendar.");
        var updated = service.Update(original.Id, "Daily brief", "Summarize calendar and priority emails.");

        Assert.IsNotNull(updated);
        Assert.AreEqual(original.Id, updated!.Id);
        Assert.AreEqual("Daily brief", updated.Name);
        Assert.AreEqual("Summarize calendar and priority emails.", updated.Goal);
        Assert.AreEqual(original.CreatedAt, updated.CreatedAt);
        Assert.IsTrue(updated.UpdatedAt >= original.UpdatedAt);

        var loaded = new SavedRoutineService(path).GetAll();
        Assert.AreEqual(1, loaded.Count);
        Assert.AreEqual("Summarize calendar and priority emails.", loaded[0].Goal);
    }

    [TestMethod]
    public void EditingRoutineRejectsAnotherRoutinesNameAndUnknownIdentifiers()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The routine store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new SavedRoutineService(Path.Combine(temp.Path, "routines.dpapi"));
        var first = service.Create("Morning", "Read today's calendar.");
        _ = service.Create("Evening", "Summarize today's notes.");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            service.Update(first.Id, "Evening", "Updated prompt."));
        Assert.IsNull(service.Update("missing-id", "Other", "Prompt."));
    }

    [TestMethod]
    public void RoutineNamesMustBeUniqueWithoutCaseSensitivity()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The routine store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new SavedRoutineService(Path.Combine(temp.Path, "routines.dpapi"));
        _ = service.Create("Weekly review", "Summarize the last week's notes.");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            service.Create("weekly REVIEW", "A second conflicting template."));
    }

    [TestMethod]
    public void RoutineStepsPersistCloneAndAreIncludedInReviewOnlyDraft()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The routine store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "routines.dpapi");
        var service = new SavedRoutineService(path);
        var created = service.Create(
            "Release review",
            "Prepare a release review.",
            new[] { "Inspect pending changes", "Run the test suite", "Summarize the results" });

        var loaded = new SavedRoutineService(path).GetAll().Single();
        CollectionAssert.AreEqual(
            new[] { "Inspect pending changes", "Run the test suite", "Summarize the results" },
            loaded.Steps);
        StringAssert.Contains(SavedRoutineService.BuildReviewPrompt(loaded), "Proposed steps to review");
        StringAssert.Contains(SavedRoutineService.BuildReviewPrompt(loaded), "2. Run the test suite");

        var updated = service.Update(
            created.Id,
            "Release review",
            "Prepare and verify a release.",
            new[] { "Build", "Test" });
        Assert.IsNotNull(updated);
        CollectionAssert.AreEqual(new[] { "Build", "Test" }, updated!.Steps);

        var updatedWithoutSteps = service.Update(
            created.Id,
            "Release review",
            "Prepare a verified release.");
        Assert.IsNotNull(updatedWithoutSteps);
        CollectionAssert.AreEqual(new[] { "Build", "Test" }, updatedWithoutSteps!.Steps);
    }

    [TestMethod]
    public void RoutineExecutionPromptKeepsStepsOrderedAndRequiresPerActionApproval()
    {
        var routine = new SavedRoutineRecord
        {
            Name = "Release review",
            Goal = "Prepare a verified release.",
            Steps = new() { "Inspect pending changes", "Run tests", "Summarize results" }
        };

        var prompt = SavedRoutineService.BuildExecutionPrompt(routine);
        StringAssert.Contains(prompt, "1. Inspect pending changes");
        StringAssert.Contains(prompt, "2. Run tests");
        StringAssert.Contains(prompt, "3. Summarize results");
        StringAssert.Contains(prompt, "normal local approval before every tool");
        StringAssert.Contains(prompt, "not scripts or permissions");
        StringAssert.Contains(SavedRoutineService.BuildExecutionPrompt(routine, isArabic: true), "لا تجمع الموافقات");
    }

    [TestMethod]
    public void LegacyRoutineJsonDefaultsToNoSteps()
    {
        const string json = """{"Id":"legacy","Name":"Legacy routine","Goal":"Review a file"}""";
        var record = System.Text.Json.JsonSerializer.Deserialize<SavedRoutineRecord>(json)
            ?? throw new AssertFailedException("Legacy routine JSON did not deserialize.");

        Assert.AreEqual(0, record.Steps.Count);
        Assert.AreEqual("Review a file", SavedRoutineService.BuildReviewPrompt(record));
    }

    [TestMethod]
    public void RoutineStepsAreBoundedAndRejectControlCharacters()
    {
        CollectionAssert.AreEqual(
            new[] { "First step", "Second step" },
            SavedRoutineService.NormalizeSteps(new[] { " ", " First step ", "Second step" }));

        Assert.ThrowsExactly<ArgumentException>(() =>
            SavedRoutineService.NormalizeSteps(Enumerable.Range(0, SavedRoutineService.MaximumSteps + 1)
                .Select(index => $"Step {index}")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            SavedRoutineService.NormalizeSteps(new[] { new string('x', SavedRoutineService.MaximumStepCharacters + 1) }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            SavedRoutineService.NormalizeSteps(new[] { "bad\tstep" }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            SavedRoutineService.NormalizeSteps(Enumerable.Repeat(new string('x', SavedRoutineService.MaximumStepCharacters), 17)));
    }

    [TestMethod]
    public void RoutineNamesAndGoalsAreBoundedAndCannotBeEmpty()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("The routine store uses Windows DPAPI.");

        using var temp = new TestTempDirectory();
        var service = new SavedRoutineService(Path.Combine(temp.Path, "routines.dpapi"));
        Assert.ThrowsExactly<ArgumentException>(() => service.Create("  ", "Do a thing."));
        Assert.ThrowsExactly<ArgumentException>(() => service.Create("Routine", "  "));
        Assert.ThrowsExactly<ArgumentException>(() =>
            service.Create(new string('n', SavedRoutineService.MaximumNameCharacters + 1), "Do a thing."));
        Assert.ThrowsExactly<ArgumentException>(() =>
            service.Create("Routine", new string('g', SavedRoutineService.MaximumGoalCharacters + 1)));
    }
}
