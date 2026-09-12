using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevBox.Core.Services;

public sealed partial class SecureSecretStore
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly ConcurrentDictionary<string, object> StoreLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _storePath;
    private readonly string _processLockPath;
    private readonly object _sync;

    public SecureSecretStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _storePath = Path.Combine(Path.GetFullPath(rootPath), "config", "secrets.dpapi.json");
        _processLockPath = _storePath + ".lock";
        _sync = StoreLocks.GetOrAdd(_storePath, static _ => new object());
    }

    public IReadOnlyList<string> ListKeys()
    {
        lock (_sync)
        {
            using var processLock = CrossProcessFileLock.Acquire(_processLockPath);
            return Load().Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public void Set(string key, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? throw new ArgumentNullException(nameof(value)));
        try { SetBytes(key, bytes); }
        finally { CryptographicZero(bytes); }
    }

    public string? Get(string key)
    {
        var bytes = GetBytes(key);
        if (bytes is null)
            return null;
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicZero(bytes); }
    }

    public void SetBytes(string key, ReadOnlySpan<byte> value)
    {
        EnsureWindows();
        ValidateKey(key);
        var plaintext = value.ToArray();
        byte[] protectedBytes;
        try { protectedBytes = Protect(plaintext); }
        finally { CryptographicZero(plaintext); }
        try
        {
            lock (_sync)
            {
                using var processLock = CrossProcessFileLock.Acquire(_processLockPath);
                var values = Load();
                values[key.Trim().ToLowerInvariant()] = Convert.ToBase64String(protectedBytes);
                Save(values);
            }
        }
        finally
        {
            CryptographicZero(protectedBytes);
        }
    }

    public byte[]? GetBytes(string key)
    {
        EnsureWindows();
        ValidateKey(key);
        lock (_sync)
        {
            using var processLock = CrossProcessFileLock.Acquire(_processLockPath);
            var values = Load();
            if (!values.TryGetValue(key.Trim().ToLowerInvariant(), out var encoded))
                return null;
            try
            {
                var protectedBytes = Convert.FromBase64String(encoded);
                try
                {
                    return Unprotect(protectedBytes);
                }
                finally
                {
                    CryptographicZero(protectedBytes);
                }
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"Stored secret '{key}' contains invalid protected data.", ex);
            }
        }
    }

    public bool Delete(string key)
    {
        EnsureWindows();
        ValidateKey(key);
        lock (_sync)
        {
            using var processLock = CrossProcessFileLock.Acquire(_processLockPath);
            var values = Load();
            var removed = values.Remove(key.Trim().ToLowerInvariant());
            if (removed)
                Save(values);
            return removed;
        }
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_storePath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(_storePath))
                ?? new Dictionary<string, string?>();
            if (values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
                throw new InvalidDataException("config/secrets.dpapi.json contains an invalid secret entry.");
            var duplicateKey = values.Keys.GroupBy(key => key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateKey is not null)
                throw new InvalidDataException($"config/secrets.dpapi.json contains duplicate secret key '{duplicateKey.Key}'.");
            return values.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("config/secrets.dpapi.json contains invalid JSON.", ex);
        }
    }

    private void Save(Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        var temp = _storePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(_storePath))
                File.Replace(temp, _storePath, null);
            else
                File.Move(temp, _storePath);
            TryHideStore();
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private void TryHideStore()
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(_storePath))
                File.SetAttributes(_storePath, File.GetAttributes(_storePath) | FileAttributes.Hidden);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static byte[] Protect(byte[] input)
    {
        var inputBlob = ToBlob(input);
        var outputBlob = new DataBlob();
        try
        {
            if (!CryptProtectData(ref inputBlob, "DevBox local secret", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI failed to protect DevBox secret data.");
            return FromBlob(outputBlob);
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero)
                Marshal.FreeHGlobal(inputBlob.Data);
            if (outputBlob.Data != IntPtr.Zero)
                _ = LocalFree(outputBlob.Data);
        }
    }

    private static byte[] Unprotect(byte[] input)
    {
        var inputBlob = ToBlob(input);
        var outputBlob = new DataBlob();
        try
        {
            if (!CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI failed to unprotect DevBox secret data.");
            return FromBlob(outputBlob);
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero)
                Marshal.FreeHGlobal(inputBlob.Data);
            if (outputBlob.Data != IntPtr.Zero)
                _ = LocalFree(outputBlob.Data);
        }
    }

    private static DataBlob ToBlob(byte[] value)
    {
        if (value.Length == 0)
            return new DataBlob(0, IntPtr.Zero);
        var pointer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, pointer, value.Length);
        return new DataBlob(value.Length, pointer);
    }

    private static byte[] FromBlob(DataBlob blob)
    {
        if (blob.Size <= 0 || blob.Data == IntPtr.Zero)
            return Array.Empty<byte>();
        var result = new byte[blob.Size];
        Marshal.Copy(blob.Data, result, 0, result.Length);
        return result;
    }

    private static void CryptographicZero(byte[] value) => System.Security.Cryptography.CryptographicOperations.ZeroMemory(value);

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DevBox SecureSecretStore requires Windows DPAPI.");
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!SecretKeyRegex().IsMatch(key))
            throw new ArgumentException("Secret key must contain only letters, digits, dot, dash, underscore or colon and be at most 128 characters.", nameof(key));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public DataBlob(int size, IntPtr data)
        {
            Size = size;
            Data = data;
        }

        public int Size;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DataBlob pDataOut);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        ref DataBlob pDataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyRegex();
}
