using Microsoft.VisualStudio.TestTools.UnitTesting;
using Layal.Services;

namespace Layal.Tests;

[TestClass]
public sealed class CompleteSetupPreflightServiceTests
{
    [TestMethod]
    public void FormatBytesUsesStableBinaryUnits()
    {
        Assert.AreEqual("0 B", CompleteSetupPreflightService.FormatBytes(0));
        Assert.AreEqual("1.0 KB", CompleteSetupPreflightService.FormatBytes(1024));
        Assert.AreEqual("1.0 MB", CompleteSetupPreflightService.FormatBytes(1024L * 1024));
        Assert.AreEqual("1.0 GB", CompleteSetupPreflightService.FormatBytes(1024L * 1024 * 1024));
    }

    [TestMethod]
    public void FormatBytesRejectsNegativeValues()
    {
        try
        {
            _ = CompleteSetupPreflightService.FormatBytes(-1);
            Assert.Fail("Expected a negative byte count to be rejected.");
        }
        catch (System.ArgumentOutOfRangeException)
        {
            // Expected validation result.
        }
    }
}
