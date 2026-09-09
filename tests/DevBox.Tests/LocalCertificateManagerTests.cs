using System.Security.Cryptography.X509Certificates;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class LocalCertificateManagerTests
{
    [Fact]
    public void Ensure_GeneratesReusableCertificateAndPrivateKey()
    {
        var root = TemporaryRoot();
        try
        {
            var manager = new LocalCertificateManager(root);

            var first = manager.Ensure("demo.test");
            var second = manager.Ensure("demo.test");

            Assert.Equal(first.Thumbprint, second.Thumbprint);
            Assert.True(File.Exists(first.CertificatePath));
            Assert.True(File.Exists(first.PrivateKeyPath));
            using var certificate = X509Certificate2.CreateFromPemFile(first.CertificatePath, first.PrivateKeyPath);
            Assert.Contains("CN=demo.test", certificate.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.True(certificate.HasPrivateKey);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Ensure_RejectsNonTestDomain()
    {
        var root = TemporaryRoot();
        try
        {
            var manager = new LocalCertificateManager(root);
            Assert.Throws<ArgumentException>(() => manager.Ensure("example.com"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
