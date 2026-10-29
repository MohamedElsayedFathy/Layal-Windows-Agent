using System;
using System.IO;
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
