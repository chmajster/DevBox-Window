using System.Security.Cryptography;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class ApplicationSelfUpdateServiceTests
{
    [Fact]
    public void ParseChecksum_ReturnsHashForExactInstallerName()
    {
        const string expected = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var content = $"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  other.zip\n{expected} *DevBox-0.3.0-win-x64-setup.exe\n";

        var actual = ApplicationSelfUpdateService.ParseChecksum(content, "DevBox-0.3.0-win-x64-setup.exe");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParseChecksum_RejectsMissingInstaller()
    {
        const string content = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  other.zip\n";

        Assert.Throws<InvalidDataException>(() =>
            ApplicationSelfUpdateService.ParseChecksum(content, "DevBox-0.3.0-win-x64-setup.exe"));
    }

    [Fact]
    public void VerifySha256_AcceptsMatchingFileAndRejectsMismatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"devbox-update-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllText(path, "verified payload");
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

            ApplicationSelfUpdateService.VerifySha256(path, hash);
            Assert.Throws<InvalidDataException>(() =>
                ApplicationSelfUpdateService.VerifySha256(path, new string('0', 64)));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
