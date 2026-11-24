using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class CrashLogFormatterTests
{
    [TestMethod]
    public void FormatterOmitsExceptionMessagesThatMayContainSensitiveData()
    {
        const string sensitiveMarker = "user-controlled-secret-marker-should-never-be-logged";
        var timestamp = new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);
        Exception exception;

        try
        {
            throw new InvalidOperationException(sensitiveMarker);
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        var entry = CrashLogFormatter.Format(timestamp, "UI thread", exception);

        StringAssert.Contains(entry, timestamp.ToString("O"));
        StringAssert.Contains(entry, "UI thread");
        StringAssert.Contains(entry, typeof(InvalidOperationException).FullName!);
        Assert.IsFalse(entry.Contains(sensitiveMarker, StringComparison.Ordinal),
            "Exception messages can contain user-controlled data and must not be persisted.");
    }

    [TestMethod]
    public void FormatterHandlesMissingExceptionWithoutWritingMessageContent()
    {
        var timestamp = new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

        var entry = CrashLogFormatter.Format(timestamp, "AppDomain", null);

        StringAssert.Contains(entry, timestamp.ToString("O"));
        StringAssert.Contains(entry, "AppDomain");
        StringAssert.Contains(entry, "Unknown");
    }
}
