using System;
using System.IO;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalResourceStorageCapacityGuardTests
{
    [TestMethod]
    public void ReportsTheFreeSpaceOnTheDestinationVolume()
    {
        using var temp = new TestTempDirectory();
        Assert.IsTrue(LocalResourceStorageCapacityGuard.GetAvailableBytes(temp.Path) > 0);
    }

    [TestMethod]
    public void RejectsAReservationLargerThanTheAvailableSpace()
    {
        using var temp = new TestTempDirectory();
        var available = LocalResourceStorageCapacityGuard.GetAvailableBytes(temp.Path);
        Assert.ThrowsExactly<InsufficientStorageException>(() =>
            LocalResourceStorageCapacityGuard.EnsureAvailable(temp.Path, checked(available + 1)));
    }

    [TestMethod]
    public void ReservationIncludesAdditionalTemporaryBytes()
    {
        using var temp = new TestTempDirectory();
        var available = LocalResourceStorageCapacityGuard.GetAvailableBytes(temp.Path);
        Assert.ThrowsExactly<InsufficientStorageException>(() =>
            LocalResourceStorageCapacityGuard.EnsureAvailable(
                temp.Path, Math.Max(0, available - 1), 2));
    }

    [TestMethod]
    public void RejectsNegativeAndOverflowingReservations()
    {
        using var temp = new TestTempDirectory();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LocalResourceStorageCapacityGuard.EnsureAvailable(temp.Path, -1));
        Assert.ThrowsExactly<OverflowException>(() =>
            LocalResourceStorageCapacityGuard.EnsureAvailable(temp.Path, long.MaxValue, 1));
    }

    [TestMethod]
    public void ExceptionPreservesThePathAndRequiredByteCounts()
    {
        using var temp = new TestTempDirectory();
        var exception = new InsufficientStorageException(temp.Path, 200, 100);
        Assert.AreEqual(Path.GetFullPath(temp.Path), exception.DirectoryPath);
        Assert.AreEqual(200L, exception.RequiredBytes);
        Assert.AreEqual(100L, exception.AvailableBytes);
        StringAssert.Contains(exception.Message, "200 B");
        StringAssert.Contains(exception.Message, "100 B");
    }
}
