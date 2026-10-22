using System.IO.Compression;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class SkillPackageServiceTests
{
    [TestMethod]
    public void ImportsValidZipAndReadsSkillMarkdown()
    {
        using var temp = new TestTempDirectory();
        var zipPath = CreateSkillZip(temp.Path, "sample-skill", "A test skill.", "# Sample\nThis is a sample skill body.");
        var service = new SkillPackageService(System.IO.Path.Combine(temp.Path, "installed"));

        var imported = service.ImportZip(zipPath, overwrite: false);
        var readBack = service.ReadSkillMarkdown(imported);

        Assert.AreEqual("sample-skill", imported.Name);
        Assert.AreEqual("A test skill.", imported.Description);
        Assert.IsTrue(File.Exists(imported.PackagePath));
        Assert.IsTrue(readBack.Contains("This is a sample skill body.", StringComparison.Ordinal));
        Assert.AreEqual(1, service.GetInstalled().Count);
        Assert.IsTrue(imported.ValidationStatus.Contains("scripts not executed", StringComparison.OrdinalIgnoreCase));

        // Common Agent Skills repositories use YAML folded block scalars for descriptions.
        var blockZip = CreateSkillZip(
            temp.Path,
            "folded-skill",
            ">-\n  Use when the user asks about WPF.\n  Preserve data binding and accessibility.",
            "# WPF\nBody");
        var importedBlock = service.ImportZip(blockZip, overwrite: false);
        Assert.AreEqual(
            "Use when the user asks about WPF. Preserve data binding and accessibility.",
            importedBlock.Description);
    }

    [TestMethod]
    public void DuplicateNameRequiresExplicitOverwrite()
    {
        using var temp = new TestTempDirectory();
        var zipPath = CreateSkillZip(temp.Path, "sample-skill", "A test skill.", "body");
        var service = new SkillPackageService(System.IO.Path.Combine(temp.Path, "installed"));
        _ = service.ImportZip(zipPath, overwrite: false);

        Assert.ThrowsExactly<SkillAlreadyExistsException>(() => service.ImportZip(zipPath, overwrite: false));

        var replaced = service.ImportZip(zipPath, overwrite: true);
        Assert.AreEqual("sample-skill", replaced.Name);
        Assert.AreEqual(1, service.GetInstalled().Count);
    }

    [TestMethod]
    public void RejectsSkillWithoutYamlFrontMatter()
    {
        using var temp = new TestTempDirectory();
        var zipPath = CreateRawZip(temp.Path, ("skill/SKILL.md", "name: missing-delimiters\ndescription: bad"));
        var service = new SkillPackageService(System.IO.Path.Combine(temp.Path, "installed"));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => service.ImportZip(zipPath, overwrite: false));
        StringAssert.Contains(error.Message, "YAML front matter");
    }

    [TestMethod]
    public void RejectsInvalidSkillName()
    {
        using var temp = new TestTempDirectory();
        var zipPath = CreateSkillZip(temp.Path, "Invalid_Name", "A test skill.", "body", validateName: false);
        var service = new SkillPackageService(System.IO.Path.Combine(temp.Path, "installed"));

        Assert.ThrowsExactly<InvalidOperationException>(() => service.ImportZip(zipPath, overwrite: false));
    }

    [TestMethod]
    public void RejectsArchiveWithoutSkillMarkdown()
    {
        using var temp = new TestTempDirectory();
        var zipPath = CreateRawZip(temp.Path, ("README.md", "not a skill"));
        var service = new SkillPackageService(System.IO.Path.Combine(temp.Path, "installed"));

        Assert.ThrowsExactly<InvalidOperationException>(() => service.ImportZip(zipPath, overwrite: false));
    }

    [TestMethod]
    public async Task RejectsNonGitHubImportUrlBeforeNetworkAccess()
    {
        using var temp = new TestTempDirectory();
        var service = new SkillPackageService(System.IO.Path.Combine(temp.Path, "installed"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ImportFromGitHubAsync("https://example.com/owner/repo", overwrite: false));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ImportFromGitHubAsync("http://github.com/owner/repo", overwrite: false));
    }

    [TestMethod]
    public void MarkdownReadIsBoundedAndMarksTruncation()
    {
        using var temp = new TestTempDirectory();
        var body = new string('x', 200);
        var zipPath = CreateSkillZip(temp.Path, "long-skill", "A long test skill.", body);
        var service = new SkillPackageService(System.IO.Path.Combine(temp.Path, "installed"));
        var imported = service.ImportZip(zipPath, overwrite: false);

        var result = service.ReadSkillMarkdown(imported, maxCharacters: 30);

        Assert.IsTrue(result.StartsWith("---", StringComparison.Ordinal));
        StringAssert.Contains(result, "[skill text truncated by Layal]");
        Assert.IsTrue(result.Length < 100);
    }

    private static string CreateSkillZip(string directory, string name, string description, string body, bool validateName = true)
    {
        var frontName = validateName ? name : "Invalid_Name";
        var markdown = $"---\nname: {frontName}\ndescription: {description}\n---\n{body}\n";
        return CreateRawZip(directory, ($"{name}/SKILL.md", markdown), ($"{name}/reference.md", "reference"));
    }

    private static string CreateRawZip(string directory, params (string Name, string Content)[] entries)
    {
        var path = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(item.Content);
        }
        return path;
    }
}
