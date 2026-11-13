using System;
using System.Buffers.Binary;
using System.IO;

namespace Layal.Services;

/// <summary>Signal-only diagnostics for the in-memory 16 kHz mono PCM microphone test.</summary>
public sealed record AudioInputSignalMetrics(
    int SampleCount,
    double DurationSeconds,
    double RmsDbfs,
    double PeakDbfs,
    double ClippedSamplePercent,
    bool HasSignal,
    bool ClippingLikely);

/// <summary>
/// Parses a PCM WAV sample and reports input level/clipping without retaining or exporting audio.
/// This deliberately does not claim to detect speech or measure transcription accuracy.
/// </summary>
public static class AudioInputSignalAnalyzer
{
    private const int ExpectedSampleRate = 16000;
    private const int ExpectedChannels = 1;
    private const int ExpectedBitsPerSample = 16;
    private const double DbfsFloor = -120.0;
    private const int ClippingThreshold = 32760;

    public static AudioInputSignalMetrics Analyze(byte[] waveBytes)
    {
        ArgumentNullException.ThrowIfNull(waveBytes);
        var wave = waveBytes.AsSpan();
        if (wave.Length < 12 || !HasFourCc(wave, 0, "RIFF") || !HasFourCc(wave, 8, "WAVE"))
            throw new InvalidDataException("The microphone diagnostic sample is not a valid RIFF/WAVE file.");

        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(wave.Slice(4, 4));
        if (riffSize < 4 || riffSize > int.MaxValue || riffSize > (uint)(wave.Length - 8))
            throw new InvalidDataException("The microphone diagnostic WAV container has an invalid size.");

        var containerEnd = checked(8 + (int)riffSize);
        var offset = 12;
        var formatFound = false;
        var dataFound = false;
        ushort formatTag = 0;
        ushort channels = 0;
        ushort bitsPerSample = 0;
        ushort blockAlign = 0;
        uint sampleRate = 0;
        var dataOffset = 0;
        var dataLength = 0;

        while (offset <= containerEnd - 8)
        {
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wave.Slice(offset + 4, 4));
            if (chunkSize > int.MaxValue)
                throw new InvalidDataException("The microphone diagnostic WAV contains an oversized chunk.");

            var chunkLength = (int)chunkSize;
            var chunkDataOffset = offset + 8;
            var chunkEnd = (long)chunkDataOffset + chunkLength;
            if (chunkEnd > containerEnd)
                throw new InvalidDataException("The microphone diagnostic WAV contains a truncated chunk.");

            if (HasFourCc(wave, offset, "fmt "))
            {
                if (chunkLength < 16)
                    throw new InvalidDataException("The microphone diagnostic WAV format chunk is incomplete.");

                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(wave.Slice(chunkDataOffset, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wave.Slice(chunkDataOffset + 2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(wave.Slice(chunkDataOffset + 4, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(wave.Slice(chunkDataOffset + 12, 2));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wave.Slice(chunkDataOffset + 14, 2));
                formatFound = true;
            }
            else if (HasFourCc(wave, offset, "data") && !dataFound)
            {
                dataOffset = chunkDataOffset;
                dataLength = chunkLength;
                dataFound = true;
            }

            var nextOffset = chunkEnd + (chunkLength & 1);
            if (nextOffset > containerEnd)
                throw new InvalidDataException("The microphone diagnostic WAV has an incomplete chunk padding byte.");
            offset = (int)nextOffset;
        }

        if (!formatFound || !dataFound)
            throw new InvalidDataException("The microphone diagnostic WAV must contain format and audio data chunks.");
        if (formatTag != 1 || channels != ExpectedChannels ||
            sampleRate != ExpectedSampleRate || bitsPerSample != ExpectedBitsPerSample ||
            blockAlign != ExpectedChannels * (ExpectedBitsPerSample / 8))
            throw new InvalidDataException("The microphone diagnostic WAV must be 16 kHz, mono, 16-bit PCM.");
        if (dataLength < 2 || dataLength % 2 != 0)
            throw new InvalidDataException("The microphone diagnostic WAV contains no complete PCM samples.");

        var sampleCount = dataLength / 2;
        double sumSquares = 0;
        var peak = 0;
        var clippedSamples = 0;
        for (var index = 0; index < sampleCount; index++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(wave.Slice(dataOffset + index * 2, 2));
            var magnitude = Math.Abs((int)sample);
            peak = Math.Max(peak, magnitude);
            if (magnitude >= ClippingThreshold)
                clippedSamples++;
            sumSquares += (double)sample * sample;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount) / 32768.0;
        var peakLevel = peak / 32768.0;
        var rmsDbfs = ToDbfs(rms);
        var peakDbfs = ToDbfs(peakLevel);
        var clippedSamplePercent = clippedSamples * 100.0 / sampleCount;

        return new AudioInputSignalMetrics(
            sampleCount,
            sampleCount / (double)ExpectedSampleRate,
            rmsDbfs,
            peakDbfs,
            clippedSamplePercent,
            HasSignal: peakDbfs >= -55.0 && rmsDbfs >= -60.0,
            ClippingLikely: clippedSamplePercent >= 0.1);
    }

    private static bool HasFourCc(ReadOnlySpan<byte> data, int offset, string value) =>
        data.Length >= offset + 4 &&
        data[offset] == (byte)value[0] &&
        data[offset + 1] == (byte)value[1] &&
        data[offset + 2] == (byte)value[2] &&
        data[offset + 3] == (byte)value[3];

    private static double ToDbfs(double normalizedLevel) =>
        normalizedLevel <= 0 ? DbfsFloor : Math.Max(DbfsFloor, 20.0 * Math.Log10(normalizedLevel));
}
