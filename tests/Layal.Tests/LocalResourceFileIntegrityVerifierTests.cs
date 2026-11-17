using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalResourceFileIntegrityVerifierTests
{
    [TestMethod]
    public async Task VerifyAsyncAcceptsPinnedFilesInHuggingFaceStyleCacheBlobs()
    {
        using var temp = new TestTempDirectory();
        var cache = Path.Combine(temp.Path, "hf-cache");
        var snapshot = Path.Combine(cache, "hub", "models--voice", "snapshots", "revision");
        var blobs = Path.Combine(cache, "hub", "models--voice", "blobs");
        Directory.CreateDirectory(snapshot);
        Directory.CreateDirectory(blobs);

        var payload = Encoding.UTF8.GetBytes("pinned model payload");
        var blob = Path.Combine(blobs, "model.safetensors");
        await File.WriteAllBytesAsync(blob, payload);
        try
        {
            try
        {
            File.CreateSymbolicLink(Path.Combine(snapshot, "model.safetensors"), blob);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("The Windows test host does not permit creating symbolic links.");
            return;
        }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("The Windows test host does not permit creating symbolic links.");
            return;
        }

        var pins = new[]
        {
            new LocalResourceFilePin(
                "model.safetensors",
                payload.LongLength,
                Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant())
        };

        await LocalResourceFileIntegrityVerifier.VerifyAsync(snapshot, cache, pins);
    }

    [TestMethod]
    public async Task VerifyAsyncRejectsSameSizeTamperedFile()
    {
        using var temp = new TestTempDirectory();
        var cache = Path.Combine(temp.Path, "hf-cache");
        var snapshot = Path.Combine(cache, "snapshot");
        Directory.CreateDirectory(snapshot);
        var expected = Encoding.UTF8.GetBytes("original");
        var tampered = Encoding.UTF8.GetBytes("tamper!!");
        await File.WriteAllBytesAsync(Path.Combine(snapshot, "model.safetensors"), tampered);
        var pins = new[]
        {
            new LocalResourceFilePin(
                "model.safetensors",
                expected.LongLength,
                Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant())
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LocalResourceFileIntegrityVerifier.VerifyAsync(snapshot, cache, pins));
    }

    [TestMethod]
    public async Task VerifyAsyncRejectsFileLinkOutsideApprovedCache()
    {
        using var temp = new TestTempDirectory();
        var cache = Path.Combine(temp.Path, "hf-cache");
        var snapshot = Path.Combine(cache, "snapshot");
        var outside = Path.Combine(temp.Path, "outside", "model.safetensors");
        Directory.CreateDirectory(snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        var payload = Encoding.UTF8.GetBytes("outside");
        await File.WriteAllBytesAsync(outside, payload);

        try
        {
            File.CreateSymbolicLink(Path.Combine(snapshot, "model.safetensors"), outside);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("The Windows test host does not permit creating symbolic links.");
            return;
        }

        var pins = new[]
        {
            new LocalResourceFilePin(
                "model.safetensors",
                payload.LongLength,
                Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant())
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LocalResourceFileIntegrityVerifier.VerifyAsync(snapshot, cache, pins));
    }

    [TestMethod]
    public async Task VerifyAsyncRejectsPathTraversalInCatalogPin()
    {
        using var temp = new TestTempDirectory();
        var cache = Path.Combine(temp.Path, "hf-cache");
        var snapshot = Path.Combine(cache, "snapshot");
        Directory.CreateDirectory(snapshot);
        var pins = new[]
        {
            new LocalResourceFilePin("../outside", 1, new string('0', 64))
        };

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            LocalResourceFileIntegrityVerifier.VerifyAsync(snapshot, cache, pins));
    }
}
