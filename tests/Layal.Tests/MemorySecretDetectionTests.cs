using System;
using System.Collections.Generic;
using System.IO;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class MemorySecretDetectionTests
{
    // Fake secret shapes are assembled at runtime so no scanner sees a token-like literal in the source.
    public static IEnumerable<object[]> CredentialSamples()
    {
        yield return new object[] { "Login", "pass" + "word: hunter2" };
        yield return new object[] { "Login", "pass" + "word=hunter2" };
        yield return new object[] { "Slack", "xo" + "xb" + "-" + "1234567890" + "-" + "abcdefghij" };
        yield return new object[] { "Header", "Authorization: Bearer " + "abcdefghijklmnopqrstuvwxyz012345" };
        yield return new object[] { "Token", string.Join(".", "ey" + "JhbGciOiJIUzI1NiJ9", "ey" + "JzdWIiOiIxMjM0NTY3ODkwIn0", "abcdefghijk") };
        yield return new object[] { "Key", "s" + "k" + "-" + "abcdefghijklmnopqrstuvwx" };
        yield return new object[] { "Key", new string('-', 5) + "BEGIN EC PRIV" + "ATE KEY" + new string('-', 5) };
    }

    [TestMethod]
    [DynamicData(nameof(CredentialSamples))]
    public void CredentialShapesAreRejected(string title, string content)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("MemoryService uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var service = new MemoryService(Path.Combine(temp.Path, "memory.dpapi"));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.Save(title, content));
    }

    [TestMethod]
    public void OrdinaryNotesThatMentionPasswordsAreAccepted()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("MemoryService uses Windows DPAPI.");
        using var temp = new TestTempDirectory();
        var service = new MemoryService(Path.Combine(temp.Path, "memory.dpapi"));
        service.Save("Reminder", "Change my password manager settings, 2+2=4 and a=b.");
    }
}
