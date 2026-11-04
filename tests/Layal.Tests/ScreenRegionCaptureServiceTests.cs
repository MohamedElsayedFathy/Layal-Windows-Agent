using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class ScreenRegionCaptureServiceTests
{
    [TestMethod]
    public void CropReturnsOnlyTheChosenRectangleAndKeepsItsPixelContent()
    {
        using var source = new Bitmap(80, 60, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.Red);
            graphics.FillRectangle(Brushes.Blue, new Rectangle(20, 10, 30, 25));
        }

        var png = ScreenRegionCaptureService.EncodeCropPng(source, new Rectangle(24, 14, 18, 18));
        using var stream = new MemoryStream(png, writable: false);
        using var crop = new Bitmap(stream);

        Assert.AreEqual(18, crop.Width);
        Assert.AreEqual(18, crop.Height);
        Assert.AreEqual(Color.Blue.ToArgb(), crop.GetPixel(0, 0).ToArgb());
        Assert.IsTrue(png.Length > 0 && png.Length <= ScreenRegionCaptureService.MaximumImageBytes);
    }

    [TestMethod]
    public void CropClipsTheRequestedRectangleToTheCapturedDisplay()
    {
        using var source = new Bitmap(80, 60, PixelFormat.Format32bppArgb);
        var png = ScreenRegionCaptureService.EncodeCropPng(source, new Rectangle(-5, -4, 30, 28));
        using var stream = new MemoryStream(png, writable: false);
        using var crop = new Bitmap(stream);

        Assert.AreEqual(25, crop.Width);
        Assert.AreEqual(24, crop.Height);
    }

    [TestMethod]
    public void CropRejectsSelectionsThatAreTooSmallOrOutsideTheScreen()
    {
        using var source = new Bitmap(80, 60, PixelFormat.Format32bppArgb);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ScreenRegionCaptureService.EncodeCropPng(source, new Rectangle(1, 1, 8, 10)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ScreenRegionCaptureService.EncodeCropPng(source, new Rectangle(100, 100, 24, 24)));
    }

    [TestMethod]
    public void DisplayAtTheExactSafePixelLimitIsAllowed()
    {
        var bounds = new Rectangle(-1920, 0, 8_000, 5_000);

        ScreenRegionCaptureService.ValidateDisplayBounds(bounds, isArabic: false);

        Assert.AreEqual(
            ScreenRegionCaptureService.MaximumScreenPixels,
            (long)bounds.Width * bounds.Height);
    }

    [TestMethod]
    public void DisplayAboveTheSafePixelLimitIsRejectedBeforeBitmapAllocation()
    {
        var bounds = new Rectangle(0, 0, 8_001, 5_000);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            ScreenRegionCaptureService.ValidateDisplayBounds(bounds, isArabic: false));

        Assert.IsTrue(exception.Message.Contains("safe capture memory limit", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InvalidDisplayDimensionsAreRejected()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ScreenRegionCaptureService.ValidateDisplayBounds(new Rectangle(0, 0, 0, 1080), isArabic: true));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ScreenRegionCaptureService.ValidateDisplayBounds(new Rectangle(0, 0, 1920, -1), isArabic: false));
    }

    [TestMethod]
    public void PreCancelledCaptureStopsBeforeReadingTheCursorOrDisplay()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new ScreenRegionCaptureService().CaptureRegionUnderCursor(
                isArabic: false,
                cancellationToken: cancellation.Token));
    }

}
