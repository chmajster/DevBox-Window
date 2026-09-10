using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DevBox.Core.Services;

public sealed partial class LocalCertificateAuthorityService : IDisposable
{
    public X509Certificate2 EnsureRootTrusted() => EnsureAuthority(trustCurrentUser: true);

    public void RemoveAuthority()
    {
        EnsureWindows();
        _ = RemoveTrustForAvailableAuthority();

        // Do not remove the DPAPI password unless both authority files were actually
        // removed. Propagating file deletion failures keeps an existing PFX loadable
        // instead of leaving it behind without its password.
        DeleteAuthorityFile(_caPfxPath);
        DeleteAuthorityFile(_caPemPath);
        if (File.Exists(_caPfxPath) || File.Exists(_caPemPath))
            throw new IOException("The DevBox Local CA could not be removed completely.");

        _secrets.Delete(PasswordSecretKey);
    }

    public void Dispose()
    {
        // The service owns no unmanaged or long-lived disposable resources.
        // IDisposable keeps short-lived UI/CLI usage consistent with other platform services.
    }

    private bool RemoveTrustForAvailableAuthority()
    {
        X509Certificate2? certificate = null;
        try
        {
            if (File.Exists(_caPemPath))
            {
                try
                {
                    certificate = LocalCertificateManager.LoadPublicCertificate(_caPemPath);
                }
                catch (CryptographicException) when (File.Exists(_caPfxPath))
                {
                    certificate = LoadAuthority();
                }
            }
            else if (File.Exists(_caPfxPath))
            {
                certificate = LoadAuthority();
            }

            if (certificate is null)
                return false;

            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false);
            var removed = false;
            foreach (var match in matches)
            {
                store.Remove(match);
                removed = true;
            }
            return removed;
        }
        finally
        {
            certificate?.Dispose();
        }
    }

    private static void DeleteAuthorityFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
