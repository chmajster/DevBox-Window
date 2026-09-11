using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class RollbackFinalTests
{
    [Fact]
    public void RollbackExecutor_ContinuesAfterFailureAndAggregatesErrors()
    {
        var secondRan = false;
        var thirdRan = false;
        var original = new InvalidOperationException("operation failed");

        var aggregate = Assert.Throws<AggregateException>(() =>
            RollbackExecutor.RethrowAfterRollback(
                original,
                () => throw new IOException("first rollback failed"),
                () => secondRan = true,
                () =>
                {
                    thirdRan = true;
                    throw new UnauthorizedAccessException("third rollback failed");
                }));

        Assert.True(secondRan);
        Assert.True(thirdRan);
        Assert.Equal(3, aggregate.InnerExceptions.Count);
        Assert.Same(original, aggregate.InnerExceptions[0]);
    }

    [Fact]
    public void LocalCa_RemoveAuthorityRemovesPfxOnlyWithoutStoredPassword()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = TemporaryRoot();
        try
        {
            using var authority = new LocalCertificateAuthorityService(root);
            using (authority.EnsureAuthority(trustCurrentUser: false))
            {
            }

            var caRoot = Path.Combine(root, "config", "ssl", "ca");
            var pfxPath = Path.Combine(caRoot, "devbox-local-ca.pfx");
            var pemPath = Path.Combine(caRoot, "devbox-local-ca.crt.pem");
            Assert.True(File.Exists(pfxPath));
            Assert.True(File.Exists(pemPath));

            File.Delete(pemPath);
            new SecureSecretStore(root).Delete("ssl.local-ca.pfx-password");

            authority.RemoveAuthority();

            Assert.False(File.Exists(pfxPath));
            Assert.False(File.Exists(pemPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-final-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "www"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root))
            return;
        try
        {
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
