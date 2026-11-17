using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Layal.Services;

/// <summary>
/// Verifies the Arabic and English OCR assets shipped in the application output.
/// The expected identities use the same Git blob SHA-1 and exact byte lengths
/// pinned by tools/Install-LayalOcrModels.ps1 at the reviewed upstream commit.
/// </summary>
public sealed class LocalEmbeddedResourceSetupService
{
    private readonly LocalResourceSetupCoordinator _coordinator;
    private readonly string _tessdataDirectory;

    public LocalEmbeddedResourceSetupService(
        LocalResourceSetupCoordinator? coordinator = null,
        string? tessdataDirectory = null)
    {
        _coordinator = coordinator ?? new LocalResourceSetupCoordinator(new LocalResourceManager());
        _tessdataDirectory = Path.GetFullPath(
            tessdataDirectory ?? Path.Combine(AppContext.BaseDirectory, "tessdata"));
    }

    public async Task EnsureOcrResourcesReadyAsync(CancellationToken cancellationToken = default)
    {
        await _coordinator.EnsureReadyAsync(
            LocalResourceIds.OcrArabic,
            token => VerifyCatalogResourceAsync(LocalResourceIds.OcrArabic, "ara.traineddata", token),
            cancellationToken).ConfigureAwait(false);

        await _coordinator.EnsureReadyAsync(
            LocalResourceIds.OcrEnglish,
            token => VerifyCatalogResourceAsync(LocalResourceIds.OcrEnglish, "eng.traineddata", token),
            cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> VerifyCatalogResourceAsync(
        string resourceId,
        string fileName,
        CancellationToken cancellationToken)
    {
        var definition = LocalResourceCatalog.GetRequired(resourceId);
        if (definition.Kind != LocalResourceKind.EmbeddedData ||
            definition.ExpectedGitBlobSizeBytes is not > 0 ||
            string.IsNullOrWhiteSpace(definition.ExpectedGitBlobSha1))
            throw new InvalidOperationException("The embedded OCR resource is missing its pinned Git identity: " + resourceId);

        return VerifyGitBlobAsync(
            Path.Combine(_tessdataDirectory, fileName),
            definition.ExpectedGitBlobSizeBytes.Value,
            definition.ExpectedGitBlobSha1,
            cancellationToken);
    }

    public static async Task<bool> VerifyGitBlobAsync(
        string path,
        long expectedSizeBytes,
        string expectedGitBlobSha1,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedGitBlobSha1);
        if (expectedSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSizeBytes));
        if (expectedGitBlobSha1.Length != 40 || !IsHex(expectedGitBlobSha1))
            throw new ArgumentException("A Git blob SHA-1 must contain exactly 40 hexadecimal characters.", nameof(expectedGitBlobSha1));

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The embedded resource path has no parent directory.");
        EnsureNoReparsePoints(directory);
        if (!File.Exists(fullPath))
            return false;
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to verify an embedded OCR model through a reparse point.");

        var info = new FileInfo(fullPath);
        if (info.Length != expectedSizeBytes)
            return false;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var header = Encoding.UTF8.GetBytes("blob " + expectedSizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0");
        hash.AppendData(header);

        await using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedSizeBytes)
            return false;

        var buffer = new byte[128 * 1024];
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                hash.AppendData(buffer, 0, count);
            }
        }
        finally
        {
            Array.Clear(buffer, 0, buffer.Length);
        }

        var actual = hash.GetHashAndReset();
        var expected = Convert.FromHexString(expectedGitBlobSha1);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character)) return false;
        }
        return true;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("The embedded resource path has no filesystem root.");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    "Refusing to verify embedded OCR data through a reparse-point directory.");
        }
    }
}
