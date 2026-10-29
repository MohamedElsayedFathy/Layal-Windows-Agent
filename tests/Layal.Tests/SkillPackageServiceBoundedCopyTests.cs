using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class SkillPackageServiceBoundedCopyTests
{
    [TestMethod]
    public void CopyBoundedRejectsAnOversizedChunkBeforeWritingIt()
    {
        const long limit = 100_000;
        using var source = new MemoryStream(new byte[200_000]);
        using var destination = new MemoryStream();

        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            InvokeCopyBounded(source, destination, limit));

        StringAssert.Contains(error.Message, "expanded beyond its allowed size");
        Assert.IsTrue(destination.Length <= limit, "The destination must never receive bytes beyond the configured limit.");
    }

    private static long InvokeCopyBounded(Stream source, Stream destination, long maxBytes)
    {
        var method = typeof(SkillPackageService).GetMethod(
            "CopyBounded", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "The bounded-copy guard must remain present.");

        try
        {
            return (long)method!.Invoke(null, new object[] { source, destination, maxBytes })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
