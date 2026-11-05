using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class SafeBrowserUploadServiceTests
{
    [TestMethod]
    public void PrepareUploadAcceptsOnlyBoundedPassiveDocumentAndImageTypesInsideWorkspace()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "LayalWorkspace");
        Directory.CreateDirectory(Path.Combine(workspace, "reports"));
        var file = Path.Combine(workspace, "reports", "summary.PDF");
        File.WriteAllText(file, "%PDF-1.7\n1 0 obj<<>>endobj\n%%EOF");

        var service = new SafeBrowserUploadService(workspace);
        var candidate = service.PrepareUpload(@"reports\summary.PDF");

        Assert.AreEqual("reports/summary.PDF", candidate.RelativePath);
        Assert.AreEqual("summary.PDF", candidate.FileName);
        Assert.AreEqual(".PDF", candidate.Extension, ignoreCase: true);
        Assert.AreEqual(new FileInfo(file).Length, candidate.LengthBytes);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), candidate.Sha256);
    }

    [TestMethod]
    public void PrepareUploadRejectsContentThatDoesNotMatchItsExtension()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "LayalWorkspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "fake.pdf"), "MZ executable content");
        File.WriteAllText(Path.Combine(workspace, "binary.txt"), "text\0with a binary null");
        File.WriteAllText(Path.Combine(workspace, "invalid.json"), "{not-json}");

        var service = new SafeBrowserUploadService(workspace);
        Assert.ThrowsExactly<InvalidDataException>(() => service.PrepareUpload("fake.pdf"));
        Assert.ThrowsExactly<InvalidDataException>(() => service.PrepareUpload("binary.txt"));
        Assert.ThrowsExactly<InvalidDataException>(() => service.PrepareUpload("invalid.json"));
    }

    [TestMethod]
    public void PrepareUploadRejectsTraversalAbsoluteAndNonCanonicalPaths()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "LayalWorkspace");
        Directory.CreateDirectory(workspace);
        var service = new SafeBrowserUploadService(workspace);

        foreach (var path in new[] { "", " ", "../outside.pdf", "folder/../outside.pdf", "/outside.pdf", @"C:\outside.pdf", "folder//file.pdf", "./file.pdf" })
            Assert.ThrowsExactly<InvalidOperationException>(() => service.PrepareUpload(path), "Path should be rejected: " + path);
    }

    [TestMethod]
    public void PrepareUploadRejectsExecutableActiveAndUnsupportedFileTypes()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "LayalWorkspace");
        Directory.CreateDirectory(workspace);
        var service = new SafeBrowserUploadService(workspace);

        foreach (var extension in new[] { ".exe", ".msi", ".dll", ".ps1", ".bat", ".cmd", ".js", ".html", ".svg", ".zip", ".7z", ".docm", ".xlsm" })
        {
            var path = "payload" + extension;
            File.WriteAllText(Path.Combine(workspace, path), "not a supported upload");
            Assert.ThrowsExactly<InvalidOperationException>(() => service.PrepareUpload(path), "Extension should be rejected: " + extension);
        }
    }

    [TestMethod]
    public void PrepareUploadRejectsEmptyAndOversizedFiles()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "LayalWorkspace");
        Directory.CreateDirectory(workspace);
        var emptyPath = Path.Combine(workspace, "empty.txt");
        File.WriteAllBytes(emptyPath, Array.Empty<byte>());
        var largePath = Path.Combine(workspace, "large.txt");
        using (var stream = new FileStream(largePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(SafeBrowserUploadService.MaximumUploadBytes + 1);

        var service = new SafeBrowserUploadService(workspace);
        Assert.ThrowsExactly<InvalidOperationException>(() => service.PrepareUpload("empty.txt"));
        Assert.ThrowsExactly<InvalidOperationException>(() => service.PrepareUpload("large.txt"));
    }

    [TestMethod]
    public void RevalidateUploadRejectsContentChangedWhileApprovalWasPendingEvenWhenLengthAndTimestampMatch()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "LayalWorkspace");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(workspace, "report.txt");
        File.WriteAllText(path, "original");
        var service = new SafeBrowserUploadService(workspace);
        var approved = service.PrepareUpload("report.txt");

        File.WriteAllText(path, "modified");
        File.SetLastWriteTimeUtc(path, approved.LastWriteTimeUtc);

        Assert.AreEqual(approved.LengthBytes, new FileInfo(path).Length);
        Assert.ThrowsExactly<IOException>(() => service.RevalidateUnchanged(approved));
    }

    [TestMethod]
    public void RevalidateUploadAcceptsAnUnchangedFile()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "LayalWorkspace");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "readme.md"), "review before sharing");
        var service = new SafeBrowserUploadService(workspace);
        var approved = service.PrepareUpload("readme.md");

        var current = service.RevalidateUnchanged(approved);

        Assert.AreEqual(approved.FullPath, current.FullPath);
        Assert.AreEqual(approved.Sha256, current.Sha256);
    }

    [TestMethod]
    public void OpenVerifiedReadLeaseRejectsChangedFileAndKeepsAnUnchangedFileReadOnly()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "LayalWorkspace");
        Directory.CreateDirectory(workspace);
        var file = Path.Combine(workspace, "report.txt");
        File.WriteAllText(file, "approved");
        var service = new SafeBrowserUploadService(workspace);
        var approved = service.PrepareUpload("report.txt");

        using (var lease = service.OpenVerifiedReadLease(approved))
        {
            Assert.IsTrue(lease.CanRead);
            Assert.AreEqual(approved.LengthBytes, lease.Length);
            if (OperatingSystem.IsWindows())
            {
                Assert.ThrowsExactly<IOException>(() =>
                {
                    using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                }, "The read lease must deny concurrent write access while the browser receives the file path.");
            }
        }

        using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            writer.WriteByte((byte)'!');
        Assert.ThrowsExactly<IOException>(() => service.OpenVerifiedReadLease(approved));
    }

    [TestMethod]
    public void PrepareUploadAcceptsPlainOfficePackagesButRejectsExternalRelationshipsAndActiveParts()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "LayalWorkspace");
        Directory.CreateDirectory(workspace);
        var service = new SafeBrowserUploadService(workspace);

        CreateOfficePackage(Path.Combine(workspace, "safe.docx"), "word/document.xml", null, null);
        CreateOfficePackage(
            Path.Combine(workspace, "external.docx"),
            "word/document.xml",
            "word/_rels/document.xml.rels",
            """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://untrusted.example/" TargetMode="External"/></Relationships>""");
        CreateOfficePackage(
            Path.Combine(workspace, "embedded.xlsx"),
            "xl/workbook.xml",
            "xl/embeddings/oleObject1.bin",
            "not executable, but an active embedded object");
        CreateOfficePackage(
            Path.Combine(workspace, "macro.docx"),
            "word/document.xml",
            "[Content_Types].xml",
            """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/word/document.xml" ContentType="application/vnd.ms-word.document.macroEnabled.main+xml"/></Types>""");

        Assert.AreEqual(".docx", service.PrepareUpload("safe.docx").Extension);
        Assert.ThrowsExactly<InvalidDataException>(() => service.PrepareUpload("external.docx"));
        Assert.ThrowsExactly<InvalidDataException>(() => service.PrepareUpload("embedded.xlsx"));
        Assert.ThrowsExactly<InvalidDataException>(() => service.PrepareUpload("macro.docx"));
    }

    [TestMethod]
    public void PrepareUploadRejectsZipSlipAndMalformedOfficeXml()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "LayalWorkspace");
        Directory.CreateDirectory(workspace);
        CreateOfficePackage(
            Path.Combine(workspace, "zip-slip.docx"),
            "word/document.xml",
            "../outside.txt",
            "bad");
        CreateOfficePackage(
            Path.Combine(workspace, "bad-xml.docx"),
            "word/document.xml",
            "word/_rels/document.xml.rels",
            "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/private.txt'>]><Relationships/>");

        var service = new SafeBrowserUploadService(workspace);
        Assert.ThrowsExactly<InvalidDataException>(() => service.PrepareUpload("zip-slip.docx"));
        Assert.ThrowsExactly<InvalidDataException>(() => service.PrepareUpload("bad-xml.docx"));
    }

    private static void CreateOfficePackage(
        string path,
        string mainDocumentName,
        string? extraEntryName,
        string? extraEntryContent)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml",
            """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/></Types>""");
        var mainXml = mainDocumentName.StartsWith("word/", StringComparison.Ordinal)
            ? """<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body/></w:document>"""
            : """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"/>""";
        WriteEntry(archive, mainDocumentName, mainXml);
        if (extraEntryName is not null && !string.Equals(extraEntryName, "[Content_Types].xml", StringComparison.Ordinal))
            WriteEntry(archive, extraEntryName, extraEntryContent ?? string.Empty);
        else if (string.Equals(extraEntryName, "[Content_Types].xml", StringComparison.Ordinal))
        {
            // Recreate the second content-type entry with the supplied macro-enabled content.
            WriteEntry(archive, "word/_rels/document.xml.rels",
                """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>""");
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    [TestMethod]
    public void PrepareUploadRejectsSymbolicLinksWhenWindowsAllowsCreatingThem()
    {
        using var temp = new TestTempDirectory();
        var workspace = Path.Combine(temp.Path, "LayalWorkspace");
        Directory.CreateDirectory(workspace);
        var outside = Path.Combine(temp.Path, "outside.txt");
        File.WriteAllText(outside, "outside the workspace");
        var link = Path.Combine(workspace, "linked.txt");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
        {
            Assert.Inconclusive("The current Windows runner does not permit creating file symbolic links: " + ex.GetType().Name);
        }

        var service = new SafeBrowserUploadService(workspace);
        Assert.ThrowsExactly<InvalidOperationException>(() => service.PrepareUpload("linked.txt"));
    }
}
