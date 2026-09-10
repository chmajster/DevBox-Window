using System.Security.Cryptography.X509Certificates;

namespace DevBox.Core.Services;

public sealed partial class LocalCertificateAuthorityService : IDisposable
{
    public X509Certificate2 EnsureRootTrusted() => EnsureAuthority(trustCurrentUser: true);

    public void RemoveAuthority()
    {
        EnsureWindows();
        if (Exists)
            _ = RemoveTrustCurrentUser();

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

    private static void DeleteAuthorityFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
