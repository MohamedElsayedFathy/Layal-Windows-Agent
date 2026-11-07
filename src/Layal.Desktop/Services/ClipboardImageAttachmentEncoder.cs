using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

        using var stream = new BoundedPngMemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);

        ValidateEncodedByteLength(stream.Length);
        return stream.ToArray();
    }

    public static void ValidateEncodedByteLength(long byteCount)
    {
        if (byteCount <= 0 || byteCount > MaximumBytes)
            throw new InvalidDataException($"The encoded clipboard image must be between 1 byte and {MaximumBytes / (1024 * 1024)} MiB.");
    }

    private sealed class BoundedPngMemoryStream : MemoryStream
    {
        private static void EnsureWithinBudget(long currentPosition, long count)
        {
            if (count < 0 || currentPosition > MaximumBytes - count)
                throw new InvalidDataException($"The encoded clipboard image cannot exceed {MaximumBytes / (1024 * 1024)} MiB.");
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWithinBudget(Position, count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWithinBudget(Position, buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureWithinBudget(Position, 1);
            base.WriteByte(value);
        }

        public override void SetLength(long value)
        {
            if (value > MaximumBytes)
                throw new InvalidDataException($"The encoded clipboard image cannot exceed {MaximumBytes / (1024 * 1024)} MiB.");
            base.SetLength(value);
        }

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureWithinBudget(Position, count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureWithinBudget(Position, buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
