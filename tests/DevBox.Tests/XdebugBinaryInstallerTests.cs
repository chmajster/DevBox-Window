using System.Security.Cryptography;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class XdebugBinaryInstallerTests
{
    [Fact]
    public void InstallFromFile_CopiesValidPeAndRecordsChecksum()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var source = Path.Combine(root, "downloaded-xdebug.dll");
            var bytes = CreatePeStub();
            File.WriteAllBytes(source, bytes);
            var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

            var result = new XdebugBinaryInstaller(root).InstallFromFile(source, expected);

            Assert.True(File.Exists(result.BinaryPath));
            Assert.Equal(expected, result.Sha256);
            Assert.Equal("downloaded-xdebug.dll", result.SourceFileName);
            Assert.Equal(bytes.LongLength, result.SizeBytes);
            Assert.True(File.Exists(Path.Combine(root, "runtime", "php", "current", "ext", "php_xdebug.devbox.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void InstallFromFile_RejectsWrongChecksumWithoutReplacingExistingBinary()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var extensionDirectory = Path.Combine(root, "runtime", "php", "current", "ext");
            Directory.CreateDirectory(extensionDirectory);
            var destination = Path.Combine(extensionDirectory, "php_xdebug.dll");
            File.WriteAllText(destination, "existing");

            var source = Path.Combine(root, "xdebug.dll");
            File.WriteAllBytes(source, CreatePeStub());

            Assert.Throws<InvalidDataException>(() =>
                new XdebugBinaryInstaller(root).InstallFromFile(source, new string('0', 64)));
            Assert.Equal("existing", File.ReadAllText(destination));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void InstallFromFile_RejectsNonPeDll()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var source = Path.Combine(root, "xdebug.dll");
            File.WriteAllBytes(source, new byte[2048]);

            Assert.Throws<InvalidDataException>(() => new XdebugBinaryInstaller(root).InstallFromFile(source));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static byte[] CreatePeStub()
    {
        var bytes = new byte[4096];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(128).CopyTo(bytes, 0x3C);
        bytes[128] = (byte)'P';
        bytes[129] = (byte)'E';
        bytes[130] = 0;
        bytes[131] = 0;
        return bytes;
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
