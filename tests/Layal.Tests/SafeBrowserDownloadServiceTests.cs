using System;
using System.Net;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class SafeBrowserDownloadServiceTests
{
    [TestMethod]
    public void DownloadTargetRequiresHttpsAndExactOrigin()
    {
        Assert.AreEqual(
            "https://docs.example/reports/a.pdf",
            SafeBrowserDownloadService.ValidateDownloadTarget(
                "https://docs.example/home",
                "https://docs.example/reports/a.pdf").AbsoluteUri);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SafeBrowserDownloadService.ValidateDownloadTarget(
                "http://docs.example/home",
                "https://docs.example/reports/a.pdf"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SafeBrowserDownloadService.ValidateDownloadTarget(
                "https://docs.example/home",
                "http://docs.example/reports/a.pdf"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SafeBrowserDownloadService.ValidateDownloadTarget(
                "https://docs.example/home",
                "https://cdn.example/reports/a.pdf"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SafeBrowserDownloadService.ValidateDownloadTarget(
                "https://docs.example/home",
                "https://docs.example:8443/reports/a.pdf"));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SafeBrowserDownloadService.ValidateDownloadTarget(
                "https://user:secret@docs.example/home",
                "https://docs.example/reports/a.pdf"));
    }

    [TestMethod]
    public void DownloadTargetAllowsOnlyDocumentAndImageExtensions()
    {
        foreach (var extension in new[] { ".pdf", ".txt", ".md", ".csv", ".json", ".docx", ".xlsx", ".png", ".jpg", ".jpeg", ".webp" })
        {
            var uri = SafeBrowserDownloadService.ValidateDownloadTarget(
                "https://docs.example/home",
                "https://docs.example/files/report" + extension);
            Assert.AreEqual(extension, System.IO.Path.GetExtension(uri.AbsolutePath));
        }

        foreach (var extension in new[] { ".exe", ".msi", ".js", ".html", ".svg", ".zip", ".ps1", ".bat", ".dll" })
        {
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                SafeBrowserDownloadService.ValidateDownloadTarget(
                    "https://docs.example/home",
                    "https://docs.example/files/payload" + extension),
                "Active or unsupported type should be rejected: " + extension);
        }
    }

    [TestMethod]
    public void PublicAddressPolicyRejectsPrivateLoopbackLinkLocalAndDocumentationRanges()
    {
        var rejected = new[]
        {
            "0.0.0.0",
            "10.1.2.3",
            "100.64.0.1",
            "127.0.0.1",
            "169.254.10.20",
            "172.16.0.1",
            "172.31.255.255",
            "192.168.1.10",
            "192.0.2.1",
            "198.18.0.1",
            "198.51.100.10",
            "203.0.113.10",
            "224.0.0.1",
            "::",
            "::1",
            "fc00::1",
            "fe80::1",
            "ff02::1",
            "2001:db8::1",
            "2001:0::1"
        };

        foreach (var value in rejected)
            Assert.IsFalse(SafeBrowserDownloadService.IsPublicAddress(IPAddress.Parse(value)),
                "Special-use address must be rejected: " + value);

        Assert.IsFalse(SafeBrowserDownloadService.IsPublicAddress(
            IPAddress.Parse("::ffff:192.168.1.10")),
            "IPv4-mapped private addresses must be rejected.");
    }

    [TestMethod]
    public void PublicAddressPolicyAllowsOrdinaryGlobalUnicastAddresses()
    {
        foreach (var value in new[] { "1.1.1.1", "8.8.8.8", "93.184.216.34", "2606:4700:4700::1111" })
            Assert.IsTrue(SafeBrowserDownloadService.IsPublicAddress(IPAddress.Parse(value)),
                "Ordinary public unicast address should be allowed: " + value);
    }

    [TestMethod]
    public void FileNameSanitizerRemovesPathsAndWindowsReservedNames()
    {
        var pathLike = SafeBrowserDownloadService.SanitizeFileName(
            @"C:\\private\\..\\report?.pdf",
            ".pdf");
        Assert.AreEqual("report.pdf", pathLike);
        Assert.IsFalse(pathLike.Contains(':'));
        Assert.IsFalse(pathLike.Contains('/'));
        Assert.IsFalse(pathLike.Contains('\\'));

        foreach (var reserved in new[] { "CON.pdf", "PRN.txt", "AUX.csv", "NUL.json", "COM1.pdf", "LPT9.txt" })
        {
            var safe = SafeBrowserDownloadService.SanitizeFileName(reserved, System.IO.Path.GetExtension(reserved));
            Assert.IsFalse(string.Equals(reserved, safe, StringComparison.OrdinalIgnoreCase),
                "Windows reserved filename should be replaced: " + reserved);
        }

        Assert.AreEqual("download.pdf", SafeBrowserDownloadService.SanitizeFileName("payload.exe", ".pdf"));
        Assert.AreEqual("download.pdf", SafeBrowserDownloadService.SanitizeFileName("...", ".pdf"));
    }

    [TestMethod]
    public void DownloadAndRedirectLimitsAreExplicitAndBounded()
    {
        Assert.AreEqual(20L * 1024 * 1024, SafeBrowserDownloadService.MaximumDownloadBytes);
        Assert.AreEqual(3, SafeBrowserDownloadService.MaximumRedirects);
        Assert.IsTrue(SafeBrowserDownloadService.MaximumRedirects is >= 0 and <= 5);
    }
}
