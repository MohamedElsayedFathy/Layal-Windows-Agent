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
