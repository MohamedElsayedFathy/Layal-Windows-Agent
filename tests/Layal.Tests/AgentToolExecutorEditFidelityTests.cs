using System.Text;
using System.Text.Json.Nodes;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class AgentToolExecutorEditFidelityTests
{
    [TestMethod]
    public async Task ReplaceMatchesLfOldTextInCrlfFileAndKeepsBomAndLineEndings()
    {
        using var temp = new TestTempDirectory();
        var executor = CreateExecutor(temp.Path);
        var path = System.IO.Path.Combine(temp.Path, "a.cs");
        var originalBytes = Utf8Bom("class A\r\n{\r\n    int x = 1;\r\n    int y = 2;\r\n}\r\n");
        File.WriteAllBytes(path, originalBytes);

        await Replace(executor, "a.cs", "    int x = 1;\n    int y = 2;", "    int x = 10;\n    int z = 3;\n    int y = 2;");

        var after = File.ReadAllBytes(path);
        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, after.Take(3).ToArray(), "The UTF-8 BOM must be preserved.");
        var text = new UTF8Encoding(false).GetString(after, 3, after.Length - 3);
        StringAssert.Contains(text, "int x = 10;\r\n    int z = 3;\r\n    int y = 2;");
        Assert.IsFalse(text.Replace("\r\n", string.Empty).Contains('\n'), "No bare LF may be introduced into a CRLF file.");

        var backup = Directory.GetFiles(System.IO.Path.Combine(temp.Path, ".layal-backups"), "*.bak").Single();
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(backup), "The backup must be byte-identical to the original.");
    }

    [TestMethod]
    public async Task ReplaceKeepsLfFilesWithoutAddingBomOrCarriageReturns()
    {
        using var temp = new TestTempDirectory();
        var executor = CreateExecutor(temp.Path);
        var path = System.IO.Path.Combine(temp.Path, "b.md");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes("one\ntwo\nthree\n"));

        await Replace(executor, "b.md", "two", "2\n2b");

        var bytes = File.ReadAllBytes(path);
        Assert.AreNotEqual((byte)0xEF, bytes[0], "A BOM must not be added.");
        Assert.AreEqual("one\n2\n2b\nthree\n", new UTF8Encoding(false).GetString(bytes));
    }

    [TestMethod]
    public async Task ReplacePreservesUtf16ByteOrderAndBackupBytes()
    {
        using var temp = new TestTempDirectory();
        var executor = CreateExecutor(temp.Path);
        var encodings = new (string FileName, UnicodeEncoding WithBom, UnicodeEncoding WithoutBom)[]
        {
            ("utf16-le.txt", new UnicodeEncoding(false, true, true), new UnicodeEncoding(false, false, true)),
            ("utf16-be.txt", new UnicodeEncoding(true, true, true), new UnicodeEncoding(true, false, true))
        };

        foreach (var item in encodings)
        {
            var path = System.IO.Path.Combine(temp.Path, item.FileName);
            var originalBytes = item.WithBom.GetPreamble().Concat(item.WithBom.GetBytes("hello \u0645\u0631\u062d\u0628\u0627")).ToArray();
            File.WriteAllBytes(path, originalBytes);

            await Replace(executor, item.FileName, "hello", "bye");

            var after = File.ReadAllBytes(path);
            CollectionAssert.AreEqual(item.WithBom.GetPreamble(), after.Take(item.WithBom.GetPreamble().Length).ToArray(),
                "UTF-16 byte order and BOM must be preserved.");
            Assert.AreEqual("bye \u0645\u0631\u062d\u0628\u0627",
                item.WithoutBom.GetString(after, item.WithBom.GetPreamble().Length, after.Length - item.WithBom.GetPreamble().Length));
            var backup = Directory.GetFiles(System.IO.Path.Combine(temp.Path, ".layal-backups"), "*" + item.FileName + ".bak").Single();
            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(backup));
        }
    }

    [TestMethod]
    public async Task AgentCannotCreateOrModifyBuildConfigurationFiles()
    {
        using var temp = new TestTempDirectory();
        var executor = CreateExecutor(temp.Path);
        var names = new[]
        {
            "global.json", "nuget.config", "packages.lock.json", "dotnet-tools.json",
            "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp", "Directory.Packages.props"
        };

        foreach (var name in names)
        {
            var createError = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                executor.ExecuteAsync("create_text_file",
                    new JsonObject { ["path"] = name, ["content"] = "{}" }.ToJsonString(),
                    (_, _, _) => Task.FromResult(true), _ => { }, CancellationToken.None));
            StringAssert.Contains(createError.Message, "Build-configuration files", name);
            Assert.IsFalse(File.Exists(System.IO.Path.Combine(temp.Path, name)), $"{name} must not be created.");

            File.WriteAllText(System.IO.Path.Combine(temp.Path, name), "{}");
            var approvalCalled = false;
            var editError = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                executor.ExecuteAsync("replace_text_in_file",
                    new JsonObject { ["path"] = name, ["old_text"] = "{}", ["new_text"] = "{\"changed\":true}" }.ToJsonString(),
                    (_, _, _) => { approvalCalled = true; return Task.FromResult(true); },
                    _ => { }, CancellationToken.None));
            StringAssert.Contains(editError.Message, "Build-configuration files", name);
            Assert.IsFalse(approvalCalled, "A blocked build file must be rejected before prompting for approval.");
            Assert.AreEqual("{}", File.ReadAllText(System.IO.Path.Combine(temp.Path, name)), $"{name} must remain unchanged.");
            File.Delete(System.IO.Path.Combine(temp.Path, name));
        }
    }

    [TestMethod]
    public async Task OldBackupsArePrunedToTheRetentionLimit()
    {
        using var temp = new TestTempDirectory();
        var executor = CreateExecutor(temp.Path);
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "n.txt"), "one two");
        var backups = System.IO.Path.Combine(temp.Path, ".layal-backups");
        Directory.CreateDirectory(backups);
        for (var i = 0; i < 230; i++)
            File.WriteAllText(System.IO.Path.Combine(backups, $"20200101-0000{i:000}-x-old.bak"), "x");

        await Replace(executor, "n.txt", "one", "1");

        var remaining = Directory.GetFiles(backups, "*.bak");
        Assert.AreEqual(200, remaining.Length);
        Assert.IsTrue(remaining.Any(f => !System.IO.Path.GetFileName(f).StartsWith("2020", StringComparison.Ordinal)),
            "The newest backup must survive pruning.");
    }

    private static byte[] Utf8Bom(string text) =>
        new UTF8Encoding(true).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(text)).ToArray();

    private static Task<string> Replace(AgentToolExecutor executor, string path, string oldText, string newText) =>
        executor.ExecuteAsync("replace_text_in_file",
            new JsonObject { ["path"] = path, ["old_text"] = oldText, ["new_text"] = newText }.ToJsonString(),
            (_, _, _) => Task.FromResult(true), _ => { }, CancellationToken.None);

    private static AgentToolExecutor CreateExecutor(string root) =>
        new(root, new MemoryService(System.IO.Path.Combine(root, "memory.dpapi")));
}
