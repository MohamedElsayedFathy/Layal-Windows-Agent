using System.Reflection;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Layal.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Layal.Tests;

[TestClass]
public sealed class LocalVoiceIntegrityTests
{
    [TestMethod]
    public async Task PinnedHashVerificationRejectsSameSizeTampering()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "piper.exe");
        var original = Encoding.UTF8.GetBytes("expected-piper-bytes");
        await File.WriteAllBytesAsync(path, original);
        var expectedHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();

        Assert.IsTrue(await MatchesExpectedFileAsync(path, original.Length, expectedHash, null));
        Assert.IsFalse(await MatchesExpectedFileAsync(path, original.Length, new string('0', 64), null));

        var tampered = Encoding.UTF8.GetBytes("tampered-piper-bytes");
        Assert.AreEqual(original.Length, tampered.Length, "The test must simulate a same-length replacement.");
        await File.WriteAllBytesAsync(path, tampered);
        Assert.IsFalse(await MatchesExpectedFileAsync(path, tampered.Length, expectedHash, null));
    }

    [TestMethod]
    public async Task PinnedHashVerificationRejectsWrongFileSize()
    {
        using var temp = new TestTempDirectory();
        var path = Path.Combine(temp.Path, "voice-config.json");
        var bytes = Encoding.UTF8.GetBytes("{\"audio\":{\"sample_rate\":22050}}");
        await File.WriteAllBytesAsync(path, bytes);
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.IsFalse(await MatchesExpectedFileAsync(path, bytes.Length + 1, expectedHash, null));
    }

    [TestMethod]
    public async Task WhisperRuntimeMustMatchEveryFileInThePinnedArchive()
    {
        using var temp = new TestTempDirectory();
        var archivePath = Path.Combine(temp.Path, "whisper.zip");
        var installRoot = Path.Combine(temp.Path, "whisper");
        Directory.CreateDirectory(installRoot);

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            await WriteArchiveEntryAsync(archive, "bin/whisper-cli.exe", "trusted-exe");
            await WriteArchiveEntryAsync(archive, "bin/ggml.dll", "trusted-dll");
        }
        ZipFile.ExtractToDirectory(archivePath, installRoot);

        var verifiedPath = await FindVerifiedWhisperExecutableAsync(archivePath, installRoot);
        Assert.AreEqual(Path.Combine(installRoot, "bin", "whisper-cli.exe"), verifiedPath);

        // Same-length replacement must be rejected; checking file size alone is insufficient.
        await File.WriteAllTextAsync(verifiedPath!, "tampered-exe");
        Assert.IsNull(await FindVerifiedWhisperExecutableAsync(archivePath, installRoot));
    }

    [TestMethod]
    public async Task WhisperRuntimeRejectsFilesNotPresentInThePinnedArchive()
    {
        using var temp = new TestTempDirectory();
        var archivePath = Path.Combine(temp.Path, "whisper.zip");
        var installRoot = Path.Combine(temp.Path, "whisper");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            await WriteArchiveEntryAsync(archive, "whisper-cli.exe", "trusted-exe");
        ZipFile.ExtractToDirectory(archivePath, installRoot);
        await File.WriteAllTextAsync(Path.Combine(installRoot, "unexpected.dll"), "unreviewed-code");

        Assert.IsNull(await FindVerifiedWhisperExecutableAsync(archivePath, installRoot));
    }

    [TestMethod]
    public void WhisperModelUsesAnImmutableRevisionAndPinnedSha256()
    {
        var type = typeof(LocalVoiceService);
        var url = type.GetField("WhisperModelUrl", BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue() as string;
        var sha256 = type.GetField("WhisperModelSha256", BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue() as string;

        StringAssert.Contains(url ?? string.Empty, "/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/");
        Assert.AreEqual("60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe", sha256);
        Assert.AreEqual(64, sha256?.Length);
    }

    private static async Task WriteArchiveEntryAsync(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: false);
        await writer.WriteAsync(content);
    }

    private static async Task<string?> FindVerifiedWhisperExecutableAsync(string archivePath, string installRoot)
    {
        var method = typeof(LocalVoiceService).GetMethod(
            "FindVerifiedWhisperExecutableAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "The archive-backed Whisper integrity verifier must exist.");
        var result = method!.Invoke(null, new object?[] { archivePath, installRoot, CancellationToken.None });
        Assert.IsNotNull(result);
        Assert.IsInstanceOfType(result, typeof(Task<string?>));
        return await ((Task<string?>)result!);
    }

    private static async Task<bool> MatchesExpectedFileAsync(
        string path,
        long expectedSize,
        string? expectedSha256,
        string? expectedSha1)
    {
        var method = typeof(LocalVoiceService).GetMethod(
            "MatchesExpectedFileAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "The resource-integrity helper must exist.");
        var result = method!.Invoke(
            null,
            new object?[] { path, expectedSize, expectedSha256, expectedSha1, CancellationToken.None });
        Assert.IsNotNull(result);
        Assert.IsInstanceOfType(result, typeof(Task<bool>));
        return await ((Task<bool>)result!);
    }
}
