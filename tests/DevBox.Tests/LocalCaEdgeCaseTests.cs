using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class LocalCaEdgeCaseTests
{
    [Fact]
    public void RemoveAuthority_AllowsCorruptPemOnlyPartialState()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "devbox-local-ca-edge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config", "ssl", "ca"));
        try
        {
            var pemPath = Path.Combine(root, "config", "ssl", "ca", "devbox-local-ca.crt.pem");
            File.WriteAllText(pemPath, "truncated-corrupt-pem");

            using var authority = new LocalCertificateAuthorityService(root);
            authority.RemoveAuthority();

            Assert.False(File.Exists(pemPath));
            Assert.False(authority.Exists);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
