using System;
using System.IO;
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
