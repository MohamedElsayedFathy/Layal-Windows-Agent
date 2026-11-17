using System;
using System.IO;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class OllamaModelDigestPolicyTests
{
    [TestMethod]
    public void AcceptsOnlyFullHexSha256Digests()
    {
        Assert.IsTrue(OllamaModelDigestPolicy.IsValidSha256Digest(new string('a', 64)));
        Assert.IsTrue(OllamaModelDigestPolicy.IsValidSha256Digest(new string('A', 64)));
        Assert.IsFalse(OllamaModelDigestPolicy.IsValidSha256Digest(null));
        Assert.IsFalse(OllamaModelDigestPolicy.IsValidSha256Digest(new string('a', 63)));
        Assert.IsFalse(OllamaModelDigestPolicy.IsValidSha256Digest("sha256:" + new string('a', 64)));
        Assert.IsFalse(OllamaModelDigestPolicy.IsValidSha256Digest(new string('g', 64)));
    }

    [TestMethod]
    public void DigestComparisonIsCaseInsensitiveButRequiresEveryByteToMatch()
    {
        var expected = new string('a', 64);
        Assert.IsTrue(OllamaModelDigestPolicy.Matches(expected, expected.ToUpperInvariant()));
        Assert.IsFalse(OllamaModelDigestPolicy.Matches(expected, new string('a', 63) + "b"));
    }

    [TestMethod]
    public void DigestDriftFailsClosed()
    {
        var expected = new string('a', 64);
        var changed = new string('b', 64);

        try
        {
            OllamaModelDigestPolicy.EnsureMatches(expected, changed);
            Assert.Fail("Expected a changed model digest to be rejected.");
        }
        catch (InvalidDataException exception)
        {
            StringAssert.Contains(exception.Message, "Silent model replacement is blocked");
        }
    }
}
