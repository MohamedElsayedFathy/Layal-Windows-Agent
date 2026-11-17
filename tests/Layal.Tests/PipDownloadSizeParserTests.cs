using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class PipDownloadSizeParserTests
{
    [DataTestMethod]
    [DataRow("Downloading torch-2.8.0-cp313-win_amd64.whl (619.4 MB)", 619_400_000L)]
    [DataRow("Downloading dependency.whl (2.5 MB)", 2_500_000L)]
    [DataRow("Downloading source.tar.gz (12.25 MB)", 12_250_000L)]
    [DataRow("Downloading package.whl (1.2 GB)", 1_200_000_000L)]
    [DataRow("Downloading package.whl (42 kB)", 42_000L)]
    [DataRow("Downloading package.whl (513 bytes)", 513L)]
    public void ParsesPipReportedPackageSizes(string line, long expected)
    {
        Assert.IsTrue(PipDownloadSizeParser.TryParseAnnouncedBytes(line, out var actual));
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void ParsesPipRawProgressIntoBytesSpeedAndRemainingTime()
    {
        Assert.IsTrue(PipDownloadSizeParser.TryParseProgressLine(
            " 312.5/619.4 MB 31.1 MB/s eta 0:09",
            out var progress));

        Assert.AreEqual(312_500_000L, progress.DownloadedBytes);
        Assert.AreEqual(619_400_000L, progress.TotalBytes);
        Assert.AreEqual(31_100_000d, progress.BytesPerSecond, 0.1);
        Assert.AreEqual(9d, progress.RemainingSeconds!.Value, 0.1);
    }

    [TestMethod]
    public void ParsesPipProgressWithLongMinuteEtaAndDecimalComma()
    {
        Assert.IsTrue(PipDownloadSizeParser.TryParseProgressLine(
            " 1,2/4,5 MB 0,5 MB/s eta 120:00",
            out var progress));

        Assert.AreEqual(1_200_000L, progress.DownloadedBytes);
        Assert.AreEqual(4_500_000L, progress.TotalBytes);
        Assert.AreEqual(120d * 60d, progress.RemainingSeconds!.Value, 0.1);
    }

    [TestMethod]
    public void RejectsOverflowingOrInconsistentProgressValues()
    {
        Assert.IsFalse(PipDownloadSizeParser.TryParseProgressLine(
            "999999999999999999999999999999/999999999999999999999999999999 GB 1 GB/s eta 0:01",
            out _));
        Assert.IsFalse(PipDownloadSizeParser.TryParseProgressLine(
            "500/400 MB 1 MB/s eta 0:01",
            out _));
    }

    [TestMethod]
    public void CalculatesRemainingTimeWhenPipProgressDoesNotIncludeEta()
    {
        Assert.IsTrue(PipDownloadSizeParser.TryParseProgressLine(
            "100/400 kB 50 kB/s",
            out var progress));

        Assert.AreEqual(300_000d / 50_000d, progress.RemainingSeconds!.Value, 0.0001);
    }

    [DataTestMethod]
    [DataRow("Downloading package.whl")]
    [DataRow("Installing collected packages: torch, numpy")]
    [DataRow("Downloading package.whl (0 MB)")]
    [DataRow("Downloading package.whl (unknown MB)")]
    public void DoesNotInventSizesWhenPipDoesNotReportThem(string line)
    {
        Assert.IsFalse(PipDownloadSizeParser.TryParseAnnouncedBytes(line, out var actual));
        Assert.AreEqual(0L, actual);
    }
}
