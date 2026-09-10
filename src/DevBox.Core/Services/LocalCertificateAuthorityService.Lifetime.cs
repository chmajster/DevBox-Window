using System.Security.Cryptography.X509Certificates;

namespace DevBox.Core.Services;

public sealed partial class LocalCertificateAuthorityService : IDisposable
{
    public X509Certificate2 EnsureRootTrusted() => EnsureAuthority(trustCurrentUser: true);

    public void Dispose()
    {
        // The service owns no unmanaged or long-lived disposable resources.
        // IDisposable keeps short-lived UI/CLI usage consistent with other platform services.
    }
}
