using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Layal.Services;

/// <summary>Encrypts a single provider secret with Windows DPAPI for the current Windows user.</summary>
public sealed class DpapiSecretStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private readonly string _path;
    private readonly string _description;

    public DpapiSecretStore(string path, string description = "Layal provider secret")
    {
        _path = path;
        _description = string.IsNullOrWhiteSpace(description) ? "Layal protected data" : description;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public void Save(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("Secret cannot be empty.", nameof(secret));
        var plain = Encoding.UTF8.GetBytes(secret);
        var input = Allocate(plain);
        var tempPath = _path + ".tmp";
        byte[]? encrypted = null;
        try
        {
            if (!CryptProtectData(ref input, _description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                encrypted = new byte[output.cbData];
                Marshal.Copy(output.pbData, encrypted, 0, output.cbData);
                File.WriteAllText(tempPath, Convert.ToBase64String(encrypted));
                if (File.Exists(_path)) File.Replace(tempPath, _path, null);
                else File.Move(tempPath, _path);
            }
            finally { ClearAndLocalFree(output); }
        }
        finally
        {
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
            // A failed write/replace must not leave a stale temporary credential file.
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            CryptographicOperations.ZeroMemory(plain);
            Free(input);
        }
    }

    public string? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var encrypted = Convert.FromBase64String(File.ReadAllText(_path));
            var input = Allocate(encrypted);
            try
            {
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                    return null;
                try
                {
                    var plain = new byte[output.cbData];
                    Marshal.Copy(output.pbData, plain, 0, output.cbData);
                    try { return Encoding.UTF8.GetString(plain); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                finally { ClearAndLocalFree(output); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
                Free(input);
            }
        }
        catch { return null; }
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    public bool Exists => File.Exists(_path);

    /// <summary>
    /// Copies an existing but unreadable/corrupt store next to the original so the next successful
    /// Save cannot silently destroy data that might still be recoverable (for example after a
    /// transient file lock or a Windows profile change). Returns the copy path, or null on failure.
    /// </summary>
    public string? PreserveUnreadableCopy()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
            var copy = _path + ".unreadable-" + timestamp + "-" + Guid.NewGuid().ToString("N");
            File.Copy(_path, copy, overwrite: false);
            return copy;
        }
        catch { return null; }
    }

    /// <summary>Restores a previously preserved unreadable store if a replacement save must be rolled back.</summary>
    public void RestoreUnreadableCopy(string copyPath)
    {
        if (string.IsNullOrWhiteSpace(copyPath))
            throw new ArgumentException("A preserved-copy path is required.", nameof(copyPath));

        var fullCopyPath = Path.GetFullPath(copyPath);
        var expectedPrefix = Path.GetFullPath(_path) + ".unreadable-";
        if (!fullCopyPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullCopyPath))
            throw new ArgumentException("The restore source must be an existing unreadable backup for this secret store.", nameof(copyPath));

        var tempPath = _path + ".restore.tmp";
        try
        {
            File.Copy(fullCopyPath, tempPath, overwrite: true);
            if (File.Exists(_path)) File.Replace(tempPath, _path, null);
            else File.Move(tempPath, _path);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return new DataBlob { cbData = bytes.Length, pbData = ptr };
    }

    private static void Free(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            // Clear the unmanaged plaintext/input buffer before freeing it.
            for (var i = 0; i < blob.cbData; i++) Marshal.WriteByte(blob.pbData, i, 0);
            Marshal.FreeHGlobal(blob.pbData);
        }
    }

    private static void ClearAndLocalFree(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            for (var i = 0; i < blob.cbData; i++) Marshal.WriteByte(blob.pbData, i, 0);
            LocalFree(blob.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
