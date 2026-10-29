using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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

        var png = ScreenRegionCaptureService.EncodeCropPng(source, new Rectangle(24, 14, 12, 10));
        using var stream = new MemoryStream(png, writable: false);
        using var crop = new Bitmap(stream);

        Assert.AreEqual(12, crop.Width);
        Assert.AreEqual(10, crop.Height);
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
}
