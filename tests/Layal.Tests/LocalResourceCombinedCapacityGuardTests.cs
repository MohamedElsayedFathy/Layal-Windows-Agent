using System;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalResourceCombinedCapacityGuardTests
{
    [TestMethod]
    public void AggregatesReservationsThatShareOneVolume()
    {
        using var temp = new TestTempDirectory();
        var available = LocalResourceStorageCapacityGuard.GetAvailableBytes(temp.Path);
        var halfPlusOne = available / 2 + 1;

        Assert.ThrowsExactly<InsufficientStorageException>(() =>
            LocalResourceCombinedCapacityGuard.EnsureAvailable(new[]
            {
                new LocalResourceStorageReservation(temp.Path, halfPlusOne),
                new LocalResourceStorageReservation(temp.Path, halfPlusOne)
            }));
    }

    [TestMethod]
    public void EmptyReservationSetIsAllowed()
    {
        LocalResourceCombinedCapacityGuard.EnsureAvailable(Array.Empty<LocalResourceStorageReservation>());
    }

    [TestMethod]
    public void RejectsNegativeReservation()
    {
        using var temp = new TestTempDirectory();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            LocalResourceCombinedCapacityGuard.EnsureAvailable(new[]
            {
                new LocalResourceStorageReservation(temp.Path, -1)
            }));
    }

    [TestMethod]
    public void DetectsAggregateOverflow()
    {
        using var temp = new TestTempDirectory();
        Assert.ThrowsExactly<OverflowException>(() =>
            LocalResourceCombinedCapacityGuard.EnsureAvailable(new[]
            {
                new LocalResourceStorageReservation(temp.Path, long.MaxValue),
                new LocalResourceStorageReservation(temp.Path, 1)
            }));
    }
}
