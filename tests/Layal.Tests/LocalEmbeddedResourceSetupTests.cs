using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalEmbeddedResourceSetupTests
{
    [TestMethod]
    public async Task GitBlobVerificationAcceptsExactBytesAndRejectsTamperingOrWrongLength()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "sample.traineddata");
        var bytes = Encoding.UTF8.GetBytes("pinned OCR model test payload");
        await File.WriteAllBytesAsync(path, bytes);
        var expected = ComputeGitBlobSha1(bytes);

        Assert.IsTrue(await LocalEmbeddedResourceSetupService.VerifyGitBlobAsync(
            path, bytes.LongLength, expected));
        Assert.IsFalse(await LocalEmbeddedResourceSetupService.VerifyGitBlobAsync(
            path, bytes.LongLength + 1, expected));
        Assert.IsFalse(await LocalEmbeddedResourceSetupService.VerifyGitBlobAsync(
            path, bytes.LongLength, new string('0', 40)));

        var tampered = bytes.ToArray();
        tampered[0] ^= 0x01; // Replace one byte while preserving the exact file length.
        Assert.AreEqual(bytes.Length, tampered.Length,
            "The tampering test must replace the file with a same-length payload.");
        await File.WriteAllBytesAsync(path, tampered);
        Assert.IsFalse(await LocalEmbeddedResourceSetupService.VerifyGitBlobAsync(
            path, tampered.LongLength, expected));
    }

    [TestMethod]
    public async Task GitBlobVerificationRejectsReparsePointFiles()
    {
        using var temp = new TestTempDirectory();
        var target = Path.Combine(temp.Path, "target.bin");
        var link = Path.Combine(temp.Path, "linked.bin");
        var bytes = Encoding.UTF8.GetBytes("trusted OCR bytes");
        await File.WriteAllBytesAsync(target, bytes);
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or
                NotSupportedException or System.Security.SecurityException)
        {
            Assert.Inconclusive("This test runner does not permit creating file symbolic links: " + exception.Message);
            return;
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            LocalEmbeddedResourceSetupService.VerifyGitBlobAsync(
                link, bytes.LongLength, ComputeGitBlobSha1(bytes)));
    }

    [TestMethod]
    public void CatalogPinsExactArabicAndEnglishOcrModelIdentities()
    {
        var arabic = LocalResourceCatalog.GetRequired(LocalResourceIds.OcrArabic);
        Assert.AreEqual(1_432_056L, arabic.ExpectedGitBlobSizeBytes);
        Assert.AreEqual("c8d129c67821c1592cac46836686b057ecc203dc", arabic.ExpectedGitBlobSha1);

        var english = LocalResourceCatalog.GetRequired(LocalResourceIds.OcrEnglish);
        Assert.AreEqual(4_113_088L, english.ExpectedGitBlobSizeBytes);
        Assert.AreEqual("bbef4675053b5b468cdb477053e28b1c698ba08e", english.ExpectedGitBlobSha1);
    }

    private static string ComputeGitBlobSha1(byte[] content)
    {
        var header = Encoding.UTF8.GetBytes("blob " + content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0");
        var objectBytes = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, objectBytes, 0, header.Length);
        Buffer.BlockCopy(content, 0, objectBytes, header.Length, content.Length);
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(objectBytes)).ToLowerInvariant();
    }
}
