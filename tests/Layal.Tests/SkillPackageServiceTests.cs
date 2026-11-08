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

        var literalZip = CreateSkillZip(
            temp.Path,
            "literal-skill",
            "|-\n  First description line.\n  Second description line.",
            "# Literal\nBody");
        var importedLiteral = service.ImportZip(literalZip, overwrite: false);
        Assert.AreEqual("First description line." + Environment.NewLine + "Second description line.", importedLiteral.Description);
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
    public void RejectsWindowsReservedSkillNamesBeforeCreatingPackageFolders()
    {
        using var temp = new TestTempDirectory();
        var root = System.IO.Path.Combine(temp.Path, "installed");
        var service = new SkillPackageService(root);

        foreach (var reservedName in new[] { "con", "nul", "aux", "prn", "com1", "lpt1" })
        {
            var zipPath = CreateRawZip(temp.Path,
                ("incoming/SKILL.md", $"---\nname: {reservedName}\ndescription: Reserved name.\n---\nBody"));

            Assert.ThrowsExactly<InvalidOperationException>(() => service.ImportZip(zipPath, overwrite: false));
            Assert.IsFalse(Directory.Exists(System.IO.Path.Combine(root, reservedName)),
                $"Reserved skill name '{reservedName}' must be rejected before creating its package directory.");
        }

        Assert.AreEqual(0, service.GetInstalled().Count);
        Assert.IsFalse(File.Exists(System.IO.Path.Combine(root, "skills-index.json")));
    }

    [TestMethod]
    public void RejectsReservedSkillNameInIndexBeforeInstallingAnotherPackage()
    {
        using var temp = new TestTempDirectory();
        var root = System.IO.Path.Combine(temp.Path, "installed");
        var service = new SkillPackageService(root);
        var indexPath = System.IO.Path.Combine(root, "skills-index.json");
        var invalidRecord = new Layal.Models.SkillRecord
        {
            Name = "con",
            Description = "Invalid reserved device name.",
            PackagePath = System.IO.Path.Combine(root, "con", "con.zip")
        };
        var invalidIndex = System.Text.Json.JsonSerializer.Serialize(new[] { invalidRecord });
        File.WriteAllText(indexPath, invalidIndex);

        var replacementZip = CreateSkillZip(temp.Path, "safe-skill", "A safe skill.", "body");

        Assert.ThrowsExactly<System.Text.Json.JsonException>(
            () => service.ImportZip(replacementZip, overwrite: false));

        Assert.IsFalse(Directory.Exists(System.IO.Path.Combine(root, "safe-skill")));
        Assert.AreEqual(invalidIndex, File.ReadAllText(indexPath));
    }

    [TestMethod]
    public void ReadSkillMarkdownRejectsReservedDeviceNameInSuppliedRecord()
    {
        using var temp = new TestTempDirectory();
        var root = System.IO.Path.Combine(temp.Path, "installed");
        var sourceZip = CreateSkillZip(temp.Path, "sample-skill", "A safe skill.", "body");
        var service = new SkillPackageService(root);
        var imported = service.ImportZip(sourceZip, overwrite: false);

        imported.Name = "nul";
        imported.PackagePath = System.IO.Path.Combine(root, "nul", "nul.zip");

        Assert.ThrowsExactly<InvalidOperationException>(() => service.ReadSkillMarkdown(imported));
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

    [TestMethod]
    public void OrphanedPackageRequiresExplicitOverwrite()
    {
        using var temp = new TestTempDirectory();
        var root = System.IO.Path.Combine(temp.Path, "installed");
        var orphanFolder = System.IO.Path.Combine(root, "sample-skill");
        Directory.CreateDirectory(orphanFolder);
        var orphanPackage = System.IO.Path.Combine(orphanFolder, "sample-skill.zip");
        var originalBytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x55, 0xAA };
        File.WriteAllBytes(orphanPackage, originalBytes);

        var replacementZip = CreateSkillZip(temp.Path, "sample-skill", "Replacement package.", "replacement body");
        var service = new SkillPackageService(root);

        Assert.ThrowsExactly<SkillAlreadyExistsException>(
            () => service.ImportZip(replacementZip, overwrite: false));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(orphanPackage),
            "An unindexed package must not be overwritten without explicit consent.");

        var imported = service.ImportZip(replacementZip, overwrite: true);
        Assert.AreEqual("sample-skill", imported.Name);
        Assert.AreEqual(1, service.GetInstalled().Count);
    }


    [TestMethod]
    public void NullSkillNameInIndexIsRejectedWithoutReplacingInstalledPackage()
    {
        using var temp = new TestTempDirectory();
        var root = System.IO.Path.Combine(temp.Path, "installed");
        var originalZip = CreateSkillZip(temp.Path, "sample-skill", "Original package.", "original body");
        var service = new SkillPackageService(root);
        var original = service.ImportZip(originalZip, overwrite: false);
        var originalPackageBytes = File.ReadAllBytes(original.PackagePath);

        var indexPath = System.IO.Path.Combine(root, "skills-index.json");
        File.WriteAllText(indexPath, "[{\"Name\":null,\"Description\":\"invalid\",\"PackagePath\":\"\"}]");
        var replacementZip = CreateSkillZip(temp.Path, "sample-skill", "Replacement package.", "replacement body");

        Assert.ThrowsExactly<System.Text.Json.JsonException>(
            () => service.ImportZip(replacementZip, overwrite: true));

        CollectionAssert.AreEqual(originalPackageBytes, File.ReadAllBytes(original.PackagePath),
            "A null skill name must be rejected before changing an installed package.");
        StringAssert.Contains(File.ReadAllText(indexPath), "\"Name\":null");
    }

    [TestMethod]
    public void CorruptSkillsIndexIsPreservedAndImportFailsClosed()
    {
        using var temp = new TestTempDirectory();
        var root = System.IO.Path.Combine(temp.Path, "installed");
        var originalZip = CreateSkillZip(temp.Path, "sample-skill", "Original package.", "original body");
        var service = new SkillPackageService(root);
        var original = service.ImportZip(originalZip, overwrite: false);
        var originalPackageBytes = File.ReadAllBytes(original.PackagePath);

        var indexPath = System.IO.Path.Combine(root, "skills-index.json");
        File.WriteAllText(indexPath, "{ this is not valid JSON");

        // Refreshing the UI remains non-fatal and preserves the unreadable index for recovery.
        Assert.AreEqual(0, service.GetInstalled().Count);
        Assert.IsTrue(Directory.GetFiles(root, "skills-index.json.corrupt-*.bak").Length > 0);

        var replacementZip = CreateSkillZip(temp.Path, "sample-skill", "Replacement package.", "replacement body");
        Assert.ThrowsExactly<System.Text.Json.JsonException>(
            () => service.ImportZip(replacementZip, overwrite: true));

        CollectionAssert.AreEqual(originalPackageBytes, File.ReadAllBytes(original.PackagePath),
            "A failed catalog read must not replace a package that was previously installed.");
        Assert.AreEqual("{ this is not valid JSON", File.ReadAllText(indexPath),
            "The unreadable index must remain untouched until explicitly recovered.");
    }

    [TestMethod]
    public void RejectsTraversalAndWindowsSpecialPathsBeforeInstallingAnyPackage()
    {
        using var temp = new TestTempDirectory();
        var root = System.IO.Path.Combine(temp.Path, "installed");
        var service = new SkillPackageService(root);

        foreach (var unsafePath in new[] { "sample-skill/../escape.txt", "sample-skill/CON.txt", "sample-skill/report.txt:secret" })
        {
            var zipPath = CreateRawZip(temp.Path,
                ("sample-skill/SKILL.md", "---\nname: sample-skill\ndescription: Safe metadata.\n---\nBody"),
                (unsafePath, "must not be accepted"));

            Assert.ThrowsExactly<InvalidOperationException>(() => service.ImportZip(zipPath, overwrite: false));
            Assert.IsFalse(Directory.Exists(System.IO.Path.Combine(root, "sample-skill")),
                "An unsafe archive must be rejected before creating an installed skill folder.");
        }
    }

    [TestMethod]
    public void RejectsCaseInsensitiveDuplicateArchivePaths()
    {
        using var temp = new TestTempDirectory();
        var zipPath = CreateRawZip(temp.Path,
            ("sample-skill/SKILL.md", "---\nname: sample-skill\ndescription: Safe metadata.\n---\nBody"),
            ("sample-skill/Readme.md", "one"),
            ("sample-skill/README.md", "two"));
        var service = new SkillPackageService(System.IO.Path.Combine(temp.Path, "installed"));

        Assert.ThrowsExactly<InvalidOperationException>(() => service.ImportZip(zipPath, overwrite: false));
        Assert.AreEqual(0, service.GetInstalled().Count);
    }

    [TestMethod]
    public void RejectsSymbolicLinkEntriesInLocalSkillArchives()
    {
        using var temp = new TestTempDirectory();
        var zipPath = System.IO.Path.Combine(temp.Path, "symlink-skill.zip");
        using (var file = File.Create(zipPath))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var markdown = archive.CreateEntry("sample-skill/SKILL.md");
            using (var writer = new StreamWriter(markdown.Open()))
                writer.Write("---\nname: sample-skill\ndescription: Safe metadata.\n---\nBody");

            var link = archive.CreateEntry("sample-skill/reference-link.md");
            link.ExternalAttributes = unchecked((int)0xA0000000u);
            using (var writer = new StreamWriter(link.Open()))
                writer.Write("../../outside.txt");
        }

        var service = new SkillPackageService(System.IO.Path.Combine(temp.Path, "installed"));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.ImportZip(zipPath, overwrite: false));
        Assert.AreEqual(0, service.GetInstalled().Count);
    }

    [TestMethod]
    public void RejectsAmbiguousSkillMarkdownAndExcessiveEntryCounts()
    {
        using var temp = new TestTempDirectory();
        var service = new SkillPackageService(System.IO.Path.Combine(temp.Path, "installed"));

        var multipleSkills = CreateRawZip(temp.Path,
            ("first/SKILL.md", "---\nname: first\ndescription: First.\n---\nBody"),
            ("second/SKILL.md", "---\nname: second\ndescription: Second.\n---\nBody"));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.ImportZip(multipleSkills, overwrite: false));

        var manyEntries = new List<(string Name, string Content)>
        {
            ("sample-skill/SKILL.md", "---\nname: sample-skill\ndescription: Safe metadata.\n---\nBody")
        };
        for (var index = 0; index < 200; index++)
            manyEntries.Add(($"sample-skill/reference-{index:D3}.md", "reference"));

        var oversizedCount = CreateRawZip(temp.Path, manyEntries.ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => service.ImportZip(oversizedCount, overwrite: false));
        Assert.AreEqual(0, service.GetInstalled().Count);
    }

    [TestMethod]
    public void RejectsCompressedArchivesWhoseExpandedContentExceedsLimits()
    {
        using var temp = new TestTempDirectory();
        var largeContent = new string('x', 4 * 1024 * 1024);
        var zipPath = CreateRawZip(temp.Path,
            ("sample-skill/SKILL.md", "---\nname: sample-skill\ndescription: Safe metadata.\n---\nBody"),
            ("sample-skill/a.txt", largeContent),
            ("sample-skill/b.txt", largeContent),
            ("sample-skill/c.txt", largeContent));
        var service = new SkillPackageService(System.IO.Path.Combine(temp.Path, "installed"));

        Assert.ThrowsExactly<InvalidOperationException>(() => service.ImportZip(zipPath, overwrite: false));
        Assert.AreEqual(0, service.GetInstalled().Count);
    }

    [TestMethod]
    public void RejectsSkillCatalogWhoseAncestorIsAReparsePoint()
    {
        using var temp = new TestTempDirectory();
        var realParent = System.IO.Path.Combine(temp.Path, "real-parent");
        var catalog = System.IO.Path.Combine(realParent, "installed");
        Directory.CreateDirectory(realParent);
        var linkedParent = System.IO.Path.Combine(temp.Path, "linked-parent");

        try
        {
            Directory.CreateSymbolicLink(linkedParent, realParent);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("The test environment does not permit creation of a directory symbolic link.");
        }

        Assert.ThrowsExactly<IOException>(() =>
            new SkillPackageService(System.IO.Path.Combine(linkedParent, "installed")));
        Assert.IsFalse(Directory.Exists(catalog),
            "The constructor must reject a linked ancestor before creating the catalog outside the requested path.");
    }

    [TestMethod]
    public void ReadSkillMarkdownRejectsPackagePathOutsideManagedSkillDirectory()
    {
        using var temp = new TestTempDirectory();
        var root = System.IO.Path.Combine(temp.Path, "installed");
        var sourceZip = CreateSkillZip(temp.Path, "sample-skill", "A safe skill.", "body");
        var service = new SkillPackageService(root);
        var imported = service.ImportZip(sourceZip, overwrite: false);
        var outsidePackage = CreateSkillZip(temp.Path, "outside-skill", "Outside package.", "must not be read");

        imported.PackagePath = outsidePackage;

        Assert.ThrowsExactly<InvalidOperationException>(() => service.ReadSkillMarkdown(imported));
    }

    [TestMethod]
    public void ReadSkillMarkdownRejectsInstalledPackageReparsePoint()
    {
        using var temp = new TestTempDirectory();
        var root = System.IO.Path.Combine(temp.Path, "installed");
        var sourceZip = CreateSkillZip(temp.Path, "sample-skill", "A safe skill.", "body");
        var service = new SkillPackageService(root);
        var imported = service.ImportZip(sourceZip, overwrite: false);
        var outsidePackage = CreateSkillZip(temp.Path, "outside-skill", "Outside package.", "outside content");

        File.Delete(imported.PackagePath);
        try
        {
            File.CreateSymbolicLink(imported.PackagePath, outsidePackage);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("The test environment does not permit creation of a file symbolic link.");
        }

        Assert.ThrowsExactly<IOException>(() => service.ReadSkillMarkdown(imported));
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
