using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace Layal.Services;

/// <summary>Encodes a user-approved clipboard image to a bounded PNG in memory without modifying the clipboard.</summary>
public static class ClipboardImageAttachmentEncoder
{
    public const long MaximumPixels = 40_000_000;
    public const int MaximumBytes = 10 * 1024 * 1024;

    public static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Clipboard image dimensions must be positive.");
        if ((long)width * height > MaximumPixels)
            throw new InvalidDataException($"Clipboard image exceeds the {MaximumPixels:N0}-pixel limit.");
    }

    public static byte[] EncodePng(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateDimensions(image.PixelWidth, image.PixelHeight);

        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);

        if (stream.Length <= 0 || stream.Length > MaximumBytes)
            throw new InvalidDataException($"The encoded clipboard image must be between 1 byte and {MaximumBytes / (1024 * 1024)} MiB.");

        return stream.ToArray();
    }
}
