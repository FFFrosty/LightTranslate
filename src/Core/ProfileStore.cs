using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace CherryTranslate.Core;

/// <summary>
/// Persists the independent translator profile using Windows user-scoped DPAPI.
/// The profile is intentionally kept outside Cherry Studio's data directory.
/// </summary>
public sealed class ProfileStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string _profilePath;

    /// <summary>
    /// Creates a store at %LOCALAPPDATA%\LightTranslate\profile.bin.  A path
    /// can be supplied by tests or a host that needs an isolated profile.
    /// </summary>
    public ProfileStore(string? profilePath = null)
    {
        _profilePath = string.IsNullOrWhiteSpace(profilePath)
            ? GetDefaultProfilePath()
            : Path.GetFullPath(profilePath);
    }

    public TranslationProfile? Load()
    {
        if (!File.Exists(_profilePath))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_profilePath);
            if (protectedBytes.Length == 0)
            {
                return null;
            }

            var plainBytes = Unprotect(protectedBytes);
            try
            {
                return JsonSerializer.Deserialize<TranslationProfile>(plainBytes, JsonOptions);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        catch (JsonException)
        {
            // A partially written or old profile is treated as absent.  The
            // caller can import a fresh profile without seeing secret data.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    public void Save(TranslationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var json = JsonSerializer.SerializeToUtf8Bytes(profile, JsonOptions);
        byte[] protectedBytes;
        try
        {
            protectedBytes = Protect(json);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }

        var directory = Path.GetDirectoryName(_profilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("翻译配置路径无效。");
        }

        Directory.CreateDirectory(directory);
        var tempPath = _profilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tempPath, protectedBytes);
            File.Move(tempPath, _profilePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            TryDelete(tempPath);
        }
    }

    private static string GetDefaultProfilePath()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("无法确定本地应用数据目录。");
        }

        return Path.Combine(localApplicationData, "LightTranslate", "profile.bin");
    }

    private static byte[] Protect(byte[] plainBytes)
    {
        var input = CreateBlob(plainBytes);
        var output = new DataBlob();
        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return CopyBlob(output);
        }
        finally
        {
            FreeBlob(ref input, useLocalFree: false);
            FreeBlob(ref output, useLocalFree: true);
        }
    }

    private static byte[] Unprotect(byte[] protectedBytes)
    {
        var input = CreateBlob(protectedBytes);
        var output = new DataBlob();
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return CopyBlob(output);
        }
        finally
        {
            FreeBlob(ref input, useLocalFree: false);
            FreeBlob(ref output, useLocalFree: true);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob
        {
            cbData = bytes.Length,
            pbData = pointer
        };
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        if (blob.cbData <= 0 || blob.pbData == IntPtr.Zero)
        {
            return Array.Empty<byte>();
        }

        var bytes = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void FreeBlob(ref DataBlob blob, bool useLocalFree)
    {
        if (blob.pbData == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (blob.cbData > 0)
            {
                // Zero the original unmanaged allocation before releasing it;
                // copying it into a second array would leave the source bytes
                // intact until the allocator reuses the block.
                var zeros = new byte[blob.cbData];
                Marshal.Copy(zeros, 0, blob.pbData, zeros.Length);
            }
        }
        finally
        {
            if (useLocalFree)
            {
                LocalFree(blob.pbData);
            }
            else
            {
                Marshal.FreeHGlobal(blob.pbData);
            }

            blob.pbData = IntPtr.Zero;
            blob.cbData = 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The main profile has already been written.  A stale temporary
            // file contains only DPAPI-protected bytes and is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException case above.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
