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

    [TestMethod]
    public void PreviewFitPreservesAspectRatioAndCentersLetterboxedImages()
    {
        var bounds = ScreenRegionSelectionGeometry.FitImageToCanvas(
            new Size(1920, 1080),
            new Size(800, 600));

        Assert.AreEqual(0f, bounds.X, 0.01f);
        Assert.AreEqual(75f, bounds.Y, 0.01f);
        Assert.AreEqual(800f, bounds.Width, 0.01f);
        Assert.AreEqual(450f, bounds.Height, 0.01f);
    }

    [TestMethod]
    public void PreviewFitAlsoCentersPillarboxedImages()
    {
        var bounds = ScreenRegionSelectionGeometry.FitImageToCanvas(
            new Size(1920, 1080),
            new Size(400, 800));

        Assert.AreEqual(0f, bounds.X, 0.01f);
        Assert.AreEqual(287.5f, bounds.Y, 0.01f);
        Assert.AreEqual(400f, bounds.Width, 0.01f);
        Assert.AreEqual(225f, bounds.Height, 0.01f);
    }

    [TestMethod]
    public void PreviewCoordinatesMapToCapturedPixelsAndRejectLetterboxClicks()
    {
        var bounds = ScreenRegionSelectionGeometry.FitImageToCanvas(
            new Size(1920, 1080),
            new Size(800, 600));

        Assert.IsTrue(ScreenRegionSelectionGeometry.TryMapPreviewPoint(
            new Point(400, 300), bounds, new Size(1920, 1080), clamp: false, out var center));
        Assert.AreEqual(new Point(960, 540), center);

        Assert.IsFalse(ScreenRegionSelectionGeometry.TryMapPreviewPoint(
            new Point(400, 40), bounds, new Size(1920, 1080), clamp: false, out _));

        Assert.IsTrue(ScreenRegionSelectionGeometry.TryMapPreviewPoint(
            new Point(400, 40), bounds, new Size(1920, 1080), clamp: true, out var clamped));
        Assert.AreEqual(new Point(960, 0), clamped);
    }

    [TestMethod]
    public void DragSelectionNormalizesReverseDirectionAndClipsToImageEdges()
    {
        var reversed = ScreenRegionSelectionGeometry.CreateSelection(
            new Point(90, 70), new Point(20, 10), new Size(100, 80));
        Assert.AreEqual(new Rectangle(20, 10, 71, 61), reversed);

        var clipped = ScreenRegionSelectionGeometry.CreateSelection(
            new Point(-20, 4), new Point(150, 100), new Size(100, 80));
        Assert.AreEqual(new Rectangle(0, 4, 100, 76), clipped);
    }

    [TestMethod]
    public void KeyboardMovementKeepsSelectionInsideTheImageAtEveryEdge()
    {
        var selection = new Rectangle(30, 20, 40, 30);
        var bottomRight = ScreenRegionSelectionGeometry.MoveSelection(
            selection, new Size(100, 80), 10_000, 10_000);
        Assert.AreEqual(new Rectangle(60, 50, 40, 30), bottomRight);

        var topLeft = ScreenRegionSelectionGeometry.MoveSelection(
            bottomRight, new Size(100, 80), -10_000, -10_000);
        Assert.AreEqual(new Rectangle(0, 0, 40, 30), topLeft);
    }

    [TestMethod]
    public void KeyboardResizeHonorsMinimumCropSizeAndCapturedImageEdges()
    {
        var selection = new Rectangle(20, 10, 30, 25);
        var minimum = ScreenRegionSelectionGeometry.ResizeSelection(
            selection, new Size(100, 80), -10_000, -10_000);
        Assert.AreEqual(new Rectangle(20, 10, 16, 16), minimum);

        var maximum = ScreenRegionSelectionGeometry.ResizeSelection(
            selection, new Size(100, 80), 10_000, 10_000);
        Assert.AreEqual(new Rectangle(20, 10, 80, 70), maximum);
    }

    [TestMethod]
    public void SpaceKeyboardSelectionCreatesCenteredUsableCrop()
    {
        var selection = ScreenRegionSelectionGeometry.CreateCenteredSelection(new Size(1920, 1080));

        Assert.AreEqual(new Rectangle(640, 360, 640, 360), selection);
        Assert.IsTrue(selection.Width >= ScreenRegionCaptureService.MinimumRegionPixels);
        Assert.IsTrue(selection.Height >= ScreenRegionCaptureService.MinimumRegionPixels);
    }

    [TestMethod]
    public void SelectionGeometryRejectsInvalidImageDimensionsAndInvalidKeyboardState()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ScreenRegionSelectionGeometry.FitImageToCanvas(Size.Empty, new Size(800, 600)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ScreenRegionSelectionGeometry.CreateCenteredSelection(new Size(12, 12)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ScreenRegionSelectionGeometry.MoveSelection(
                new Rectangle(-1, 0, 16, 16), new Size(100, 100), 1, 1));
    }

}
