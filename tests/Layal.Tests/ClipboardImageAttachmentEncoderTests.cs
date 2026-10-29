using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ClipboardImageAttachmentEncoderTests
{
    [TestMethod]
    public void ClipboardImageEncodesToBoundedPngWithSamePixelDimensions()
    {
        const int width = 32;
        const int height = 24;
        var pixels = new byte[width * height * 4];
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        var source = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();

        var png = ClipboardImageAttachmentEncoder.EncodePng(source);

        Assert.IsTrue(png.Length > 0);
        Assert.IsTrue(png.Length <= ClipboardImageAttachmentEncoder.MaximumBytes);
        using var stream = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.AreEqual(width, decoder.Frames[0].PixelWidth);
        Assert.AreEqual(height, decoder.Frames[0].PixelHeight);
    }

    [TestMethod]
    public void ClipboardImageDimensionsRejectZeroAndPixelBombInputs()
    {
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ClipboardImageAttachmentEncoder.ValidateDimensions(0, 10));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ClipboardImageAttachmentEncoder.ValidateDimensions(10, 0));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ClipboardImageAttachmentEncoder.ValidateDimensions(10_000, 5_000));
    }

    [TestMethod]
    public void ClipboardImageDimensionsAcceptExactPixelBudget()
    {
        ClipboardImageAttachmentEncoder.ValidateDimensions(20_000, 2_000);
    }
}
