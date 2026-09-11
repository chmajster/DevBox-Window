using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DevBox.Core.Services;

public sealed partial class LocalCertificateAuthorityService : IDisposable
{
    public X509Certificate2 EnsureRootTrusted() => EnsureAuthority(trustCurrentUser: true);

    public void RemoveAuthority()
    {
        EnsureWindows();
        using var availableAuthority = LoadAvailableAuthority();
        var wasTrusted = availableAuthority is not null && IsTrustedCurrentUser(availableAuthority);

        try
        {
            if (availableAuthority is not null)
                RemoveTrustCurrentUser(availableAuthority);

            // Do not remove the DPAPI password unless both authority files were actually
            // removed. Propagating file deletion failures keeps an existing PFX loadable
            // instead of leaving it behind without its password.
            DeleteAuthorityFile(_caPfxPath);
            DeleteAuthorityFile(_caPemPath);
            if (File.Exists(_caPfxPath) || File.Exists(_caPemPath))
                throw new IOException("The DevBox Local CA could not be removed completely.");

            _secrets.Delete(PasswordSecretKey);
        }
        catch
        {
            // File deletion can fail after trust was already removed. If the authority
            // existed and was trusted before the transaction, restore that trust so the
            // still-present CA remains usable and existing site certificates keep working.
            if (wasTrusted && availableAuthority is not null)
                TrustCurrentUser(availableAuthority);
            throw;
        }
    }

    public void Dispose()
    {
        // The service owns no unmanaged or long-lived disposable resources.
        // IDisposable keeps short-lived UI/CLI usage consistent with other platform services.
    }

    private X509Certificate2? LoadAvailableAuthority()
    {
        if (File.Exists(_caPemPath))
        {
            try
            {
                return LocalCertificateManager.LoadPublicCertificate(_caPemPath);
            }
            catch (CryptographicException) when (File.Exists(_caPfxPath))
            {
                return LoadAuthority();
            }
        }

        if (File.Exists(_caPfxPath))
            return LoadAuthority();

        return null;
    }

    private static bool IsTrustedCurrentUser(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0;
    }

    private static void RemoveTrustCurrentUser(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var match in store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false))
            store.Remove(match);
    }

    private static void DeleteAuthorityFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
