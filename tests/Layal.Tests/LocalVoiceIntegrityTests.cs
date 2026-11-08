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

        var verifiedPath = await VerifyWhisperExtractedFilesAsync(archivePath, installRoot);
        Assert.AreEqual(Path.Combine(installRoot, "bin", "whisper-cli.exe"), verifiedPath);

        // Same-length replacement must be rejected; checking file size alone is insufficient.
        var tamperedBytes = Encoding.UTF8.GetBytes("damaged-exe");
        Assert.AreEqual(new FileInfo(verifiedPath!).Length, tamperedBytes.Length,
            "The replacement must have exactly the same byte length as the trusted executable.");
        await File.WriteAllBytesAsync(verifiedPath!, tamperedBytes);
        Assert.IsNull(await VerifyWhisperExtractedFilesAsync(archivePath, installRoot));
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

        Assert.IsNull(await VerifyWhisperExtractedFilesAsync(archivePath, installRoot));
    }

    [TestMethod]
    public async Task WhisperRuntimeRejectsReparsePointsWithoutFollowingThem()
    {
        using var temp = new TestTempDirectory();
        var archivePath = Path.Combine(temp.Path, "whisper.zip");
        var installRoot = Path.Combine(temp.Path, "whisper");
        var outsideRoot = Path.Combine(temp.Path, "outside");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            await WriteArchiveEntryAsync(archive, "whisper-cli.exe", "trusted-exe");
        ZipFile.ExtractToDirectory(archivePath, installRoot);
        Directory.CreateDirectory(outsideRoot);

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(installRoot, "redirected"), outsideRoot);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            Assert.Inconclusive("This Windows runner does not permit creating a directory symbolic link: " + ex.Message);
            return;
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            VerifyWhisperExtractedFilesAsync(archivePath, installRoot));
    }

    [TestMethod]
    public async Task WhisperArchiveGateRejectsWrongSizeAndSha256()
    {
        using var temp = new TestTempDirectory();
        var archivePath = Path.Combine(temp.Path, "archive.bin");
        var bytes = Encoding.UTF8.GetBytes("pinned archive test bytes");
        await File.WriteAllBytesAsync(archivePath, bytes);
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.IsTrue(await OpenVerifiedWhisperArchiveAsync(archivePath, bytes.Length, expectedHash));
        Assert.IsFalse(await OpenVerifiedWhisperArchiveAsync(archivePath, bytes.Length + 1, expectedHash));
        Assert.IsFalse(await OpenVerifiedWhisperArchiveAsync(archivePath, bytes.Length, new string('0', 64)));
    }

    [TestMethod]
    public async Task WhisperExtractionVerifiesArchiveBeforeReplacingInstall()
    {
        using var temp = new TestTempDirectory();
        var archivePath = Path.Combine(temp.Path, "whisper.zip");
        var installRoot = Path.Combine(temp.Path, "whisper");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            await WriteArchiveEntryAsync(archive, "bin/whisper-cli.exe", "trusted-exe");
            await WriteArchiveEntryAsync(archive, "bin/ggml.dll", "trusted-dll");
        }

        Directory.CreateDirectory(installRoot);
        var staleFile = Path.Combine(installRoot, "untrusted-old-file.dll");
        await File.WriteAllTextAsync(staleFile, "must be removed after archive verification");

        var archiveBytes = await File.ReadAllBytesAsync(archivePath);
        var expectedHash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();

        // A rejected archive must not delete or partially replace the existing installation.
        Assert.IsNull(await ExtractVerifiedWhisperArchiveAsync(
            archivePath, installRoot, new string('0', 64)));
        Assert.IsTrue(File.Exists(staleFile));

        var executable = await ExtractVerifiedWhisperArchiveAsync(
            archivePath, installRoot, expectedHash);
        Assert.AreEqual(Path.Combine(installRoot, "bin", "whisper-cli.exe"), executable);
        Assert.IsFalse(File.Exists(staleFile));
        Assert.AreEqual("trusted-exe", await File.ReadAllTextAsync(executable!));
        Assert.AreEqual("trusted-dll", await File.ReadAllTextAsync(Path.Combine(installRoot, "bin", "ggml.dll")));
    }

    [TestMethod]
    public async Task WhisperExtractionRejectsArchivePathTraversal()
    {
        using var temp = new TestTempDirectory();
        var archivePath = Path.Combine(temp.Path, "traversal.zip");
        var installRoot = Path.Combine(temp.Path, "whisper");
        var escapedPath = Path.Combine(temp.Path, "escaped.dll");

        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            await WriteArchiveEntryAsync(archive, "whisper-cli.exe", "trusted-exe");
            await WriteArchiveEntryAsync(archive, "../escaped.dll", "outside");
        }

        var archiveBytes = await File.ReadAllBytesAsync(archivePath);
        var expectedHash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            ExtractVerifiedWhisperArchiveAsync(archivePath, installRoot, expectedHash));
        Assert.IsFalse(File.Exists(escapedPath));
    }

    [TestMethod]
    public void WhisperArchiveUsesPinnedSizeAndSha256()
    {
        var type = typeof(LocalVoiceService);
        var size = type.GetField("WhisperArchiveSize", BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue();
        var sha256 = type.GetField("WhisperArchiveSha256", BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue() as string;

        Assert.AreEqual(7_982_101L, size);
        Assert.AreEqual("7d8be46ecd31828e1eb7a2ecdd0d6b314feafd82163038ab6092594b0a063539", sha256);
        Assert.AreEqual(64, sha256?.Length);
    }

    [TestMethod]
    public void InvalidateReadinessClearsTheReadyBitAndBothExecutablePaths()
    {
        var service = new LocalVoiceService();
        var type = typeof(LocalVoiceService);
        type.GetField("_ready", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, 1);
        type.GetField("_whisperExecutable", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, "whisper-cli.exe");
        type.GetField("_piperExecutable", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, "piper.exe");

        var invalidate = type.GetMethod("InvalidateReadiness", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(invalidate);
        invalidate!.Invoke(service, null);

        Assert.IsFalse(service.IsReady);
        Assert.IsNull(type.GetField("_whisperExecutable", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service));
        Assert.IsNull(type.GetField("_piperExecutable", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service));
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
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes.AsMemory());
    }

    private static async Task<bool> OpenVerifiedWhisperArchiveAsync(string archivePath, long expectedSize, string expectedSha256)
    {
        var method = typeof(LocalVoiceService).GetMethod(
            "OpenVerifiedWhisperArchiveAsync",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(string), typeof(long), typeof(string), typeof(CancellationToken) },
            modifiers: null);
        Assert.IsNotNull(method, "The production archive size/hash gate must exist.");
        var result = method!.Invoke(
            null, new object?[] { archivePath, expectedSize, expectedSha256, CancellationToken.None });
        Assert.IsNotNull(result);
        Assert.IsInstanceOfType(result, typeof(Task<FileStream>));
        await using var stream = await ((Task<FileStream?>)result!);
        return stream is not null;
    }

    private static async Task<string?> ExtractVerifiedWhisperArchiveAsync(
        string archivePath, string installRoot, string expectedSha256)
    {
        var info = new FileInfo(archivePath);
        var method = typeof(LocalVoiceService).GetMethod(
            "ExtractVerifiedWhisperArchiveAsync",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[]
            {
                typeof(string), typeof(string), typeof(string), typeof(long), typeof(string), typeof(CancellationToken)
            },
            modifiers: null);
        Assert.IsNotNull(method, "The production verified extraction path must exist.");
        var result = method!.Invoke(
            null,
            new object?[] { archivePath, installRoot, installRoot, info.Length, expectedSha256, CancellationToken.None });
        Assert.IsNotNull(result);
        Assert.IsInstanceOfType(result, typeof(Task<string>));
        return await ((Task<string?>)result!);
    }

    private static async Task<string?> VerifyWhisperExtractedFilesAsync(string archivePath, string installRoot)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var method = typeof(LocalVoiceService).GetMethod(
            "VerifyWhisperExtractedFilesAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "The extracted Whisper file-set verifier must exist.");
        var result = method!.Invoke(null, new object?[] { archive, installRoot, CancellationToken.None });
        Assert.IsNotNull(result);
        Assert.IsInstanceOfType(result, typeof(Task<string>));
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
