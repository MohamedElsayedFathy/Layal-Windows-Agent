using System.Reflection;
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
