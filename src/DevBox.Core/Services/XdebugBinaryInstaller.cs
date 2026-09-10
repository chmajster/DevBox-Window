using System.Security.Cryptography;
using System.Text.Json;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class XdebugBinaryInstaller
{
    private readonly string _extensionDirectory;
    private readonly string _binaryPath;
    private readonly string _metadataPath;

    public XdebugBinaryInstaller(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = Path.GetFullPath(rootPath);
        _extensionDirectory = Path.Combine(root, "runtime", "php", "current", "ext");
        _binaryPath = Path.Combine(_extensionDirectory, "php_xdebug.dll");
        _metadataPath = Path.Combine(_extensionDirectory, "php_xdebug.devbox.json");
    }

    public XdebugBinaryInstallation InstallFromFile(string sourcePath, string? expectedSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Xdebug DLL was not found.", source);
        }
        if (!Path.GetExtension(source).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Xdebug binary must be a Windows DLL file.");
        }

        var info = new FileInfo(source);
        if (info.Length < 1024 || info.Length > 128L * 1024 * 1024)
        {
            throw new InvalidDataException("Xdebug DLL size is outside the accepted range.");
        }
        ValidatePortableExecutable(source);

        var sha256 = ComputeSha256(source);
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var expected = NormalizeSha256(expectedSha256);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(sha256), Convert.FromHexString(expected)))
            {
                throw new InvalidDataException("Xdebug DLL SHA-256 verification failed.");
            }
        }

        Directory.CreateDirectory(_extensionDirectory);
        var tempBinary = Path.Combine(_extensionDirectory, $".php_xdebug.{Guid.NewGuid():N}.tmp");
        var tempMetadata = Path.Combine(_extensionDirectory, $".php_xdebug.devbox.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, tempBinary, overwrite: false);
            if (!ComputeSha256(tempBinary).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Xdebug DLL changed while being copied.");
            }

            var metadata = new
            {
                schemaVersion = 1,
                sourceFileName = Path.GetFileName(source),
                sha256,
                sizeBytes = info.Length
            };
            File.WriteAllText(tempMetadata, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));

            ReplaceFile(tempBinary, _binaryPath);
            ReplaceFile(tempMetadata, _metadataPath);
            return new XdebugBinaryInstallation(_binaryPath, sha256, Path.GetFileName(source), info.Length);
        }
        finally
        {
            TryDelete(tempBinary);
            TryDelete(tempMetadata);
        }
    }

    internal static void ValidatePortableExecutable(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> dosHeader = stackalloc byte[64];
        if (stream.Read(dosHeader) != dosHeader.Length || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            throw new InvalidDataException("Xdebug DLL does not contain a valid PE DOS header.");
        }

        var peOffset = BitConverter.ToInt32(dosHeader[0x3C..0x40]);
        if (peOffset < 64 || peOffset > stream.Length - 4)
        {
            throw new InvalidDataException("Xdebug DLL contains an invalid PE header offset.");
        }

        stream.Position = peOffset;
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != signature.Length ||
            signature[0] != (byte)'P' || signature[1] != (byte)'E' || signature[2] != 0 || signature[3] != 0)
        {
            throw new InvalidDataException("Xdebug DLL does not contain a valid PE signature.");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Expected Xdebug SHA-256 value is invalid.");
        }
        return normalized;
    }

    private static void ReplaceFile(string source, string destination)
    {
        if (File.Exists(destination))
        {
            var backup = destination + $".backup-{Guid.NewGuid():N}";
            try
            {
                File.Replace(source, destination, backup);
            }
            finally
            {
                TryDelete(backup);
            }
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
