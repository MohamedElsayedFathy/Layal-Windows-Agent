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
        try
        {
            if (!CryptProtectData(ref input, _description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var encrypted = new byte[output.cbData];
                Marshal.Copy(output.pbData, encrypted, 0, output.cbData);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, Convert.ToBase64String(encrypted));
                if (File.Exists(_path)) File.Replace(temp, _path, null);
                else File.Move(temp, _path);
                CryptographicOperations.ZeroMemory(encrypted);
            }
            finally { ClearAndLocalFree(output); }
        }
        finally
        {
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
