using System;
using System.IO;
using System.Linq;
using System.Text;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class AudioInputSignalAnalyzerTests
{
    [TestMethod]
    public void Analyze_SilenceReportsNoUsableSignal()
    {
        var metrics = AudioInputSignalAnalyzer.Analyze(CreatePcmWave(Enumerable.Repeat((short)0, 1600).ToArray()));

        Assert.AreEqual(1600, metrics.SampleCount);
        Assert.AreEqual(0.1, metrics.DurationSeconds, 0.0001);
        Assert.AreEqual(-120.0, metrics.RmsDbfs, 0.001);
        Assert.AreEqual(-120.0, metrics.PeakDbfs, 0.001);
        Assert.IsFalse(metrics.HasSignal);
        Assert.IsFalse(metrics.ClippingLikely);
    }

    [TestMethod]
    public void Analyze_CleanToneReportsSignalWithoutClipping()
    {
        var samples = Enumerable.Range(0, 1600)
            .Select(index => (short)(8000 * Math.Sin(2 * Math.PI * 440 * index / 16000.0)))
            .ToArray();

        var metrics = AudioInputSignalAnalyzer.Analyze(CreatePcmWave(samples));

        Assert.IsTrue(metrics.HasSignal);
        Assert.IsFalse(metrics.ClippingLikely);
        Assert.IsTrue(metrics.RmsDbfs < -10 && metrics.RmsDbfs > -20);
        Assert.IsTrue(metrics.PeakDbfs < -10 && metrics.PeakDbfs > -15);
        Assert.AreEqual(0.0, metrics.ClippedSamplePercent, 0.001);
    }

    [TestMethod]
    public void Analyze_FullScaleSamplesAreReportedAsClipping()
    {
        var metrics = AudioInputSignalAnalyzer.Analyze(
            CreatePcmWave(Enumerable.Repeat(short.MaxValue, 1000).ToArray()));

        Assert.IsTrue(metrics.HasSignal);
        Assert.IsTrue(metrics.ClippingLikely);
        Assert.AreEqual(100.0, metrics.ClippedSamplePercent, 0.001);
        Assert.IsTrue(metrics.PeakDbfs > -0.01);
    }

    [TestMethod]
    public void Analyze_RejectsUnsupportedSampleRate()
    {
        var exception = Assert.ThrowsExactly<InvalidDataException>(
            () => AudioInputSignalAnalyzer.Analyze(CreatePcmWave(new short[] { 1, 2, 3 }, sampleRate: 8000)));

        StringAssert.Contains(exception.Message, "16 kHz");
    }

    [TestMethod]
    public void Analyze_RejectsNonPcmEncoding()
    {
        var exception = Assert.ThrowsExactly<InvalidDataException>(
            () => AudioInputSignalAnalyzer.Analyze(CreatePcmWave(new short[] { 1, 2, 3 }, formatTag: 3)));

        StringAssert.Contains(exception.Message, "16-bit PCM");
    }

    [TestMethod]
    public void Analyze_RejectsMalformedRiffHeader()
    {
        var wave = CreatePcmWave(new short[] { 1, 2, 3 });
        wave[0] = (byte)'X';

        Assert.ThrowsExactly<InvalidDataException>(() => AudioInputSignalAnalyzer.Analyze(wave));
    }

    [TestMethod]
    public void Analyze_RejectsEmptyPcmData()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => AudioInputSignalAnalyzer.Analyze(CreatePcmWave(Array.Empty<short>())));
    }

    private static byte[] CreatePcmWave(
        short[] samples,
        int sampleRate = 16000,
        ushort channels = 1,
        ushort bitsPerSample = 16,
        ushort formatTag = 1)
    {
        var blockAlign = (ushort)(channels * (bitsPerSample / 8));
        var dataLength = samples.Length * blockAlign;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write(formatTag);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * blockAlign);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            foreach (var sample in samples)
                writer.Write(sample);
        }
        return stream.ToArray();
    }
}
