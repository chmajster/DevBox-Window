using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DevBox.Core.Services;

internal sealed record TlsRollbackState(
    string Domain,
    byte[]? Certificate,
    byte[]? PrivateKey,
    bool LeafTrusted,
    bool LocalCaExisted,
    bool LocalCaTrusted);

internal sealed class TlsRollbackStateService
{
    private readonly string _rootPath;
    private readonly LocalCertificateManager _certificates;

    public TlsRollbackStateService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _certificates = new LocalCertificateManager(_rootPath);
    }

    public TlsRollbackState Capture(string domain)
    {
        var normalizedDomain = LocalCertificateManager.NormalizeDomain(domain);
        var certificatePath = CertificatePath(normalizedDomain);
        var privateKeyPath = PrivateKeyPath(normalizedDomain);
        var leafTrusted = false;
        var localCaExisted = false;
        var localCaTrusted = false;

        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(certificatePath))
            {
                try
                {
                    leafTrusted = _certificates.IsTrustedForCurrentUser(normalizedDomain);
                }
                catch (CryptographicException)
                {
                    leafTrusted = false;
                }
            }

            using var authority = new LocalCertificateAuthorityService(_rootPath);
            localCaExisted = authority.Exists;
            if (localCaExisted)
                localCaTrusted = authority.IsTrustedCurrentUser();
        }

        return new TlsRollbackState(
            normalizedDomain,
            File.Exists(certificatePath) ? File.ReadAllBytes(certificatePath) : null,
            File.Exists(privateKeyPath) ? File.ReadAllBytes(privateKeyPath) : null,
            leafTrusted,
            localCaExisted,
            localCaTrusted);
    }

    public void Restore(TlsRollbackState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedDomain = LocalCertificateManager.NormalizeDomain(state.Domain);
        using var domainLock = CrossProcessFileLock.Acquire(LocalCertificateManager.GetDomainLockPath(_rootPath, normalizedDomain));
        RestoreUnderLock(state);
    }

    internal void RestoreUnderLock(TlsRollbackState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalizedDomain = LocalCertificateManager.NormalizeDomain(state.Domain);
        var certificatePath = CertificatePath(normalizedDomain);
        var privateKeyPath = PrivateKeyPath(normalizedDomain);
        var errors = new List<Exception>();

        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception ex) { errors.Add(ex); }
        }

        if (OperatingSystem.IsWindows() && File.Exists(certificatePath))
            Attempt(() => _certificates.UntrustForCurrentUserUnlocked(normalizedDomain));

        Attempt(() => RestoreBytes(certificatePath, state.Certificate));
        Attempt(() => RestoreBytes(privateKeyPath, state.PrivateKey));

        if (OperatingSystem.IsWindows())
        {
            if (state.Certificate is not null && state.LeafTrusted)
                Attempt(() => TrustLeaf(certificatePath));
            else if (state.Certificate is not null)
                Attempt(() => _certificates.UntrustForCurrentUserUnlocked(normalizedDomain));

            Attempt(() => RestoreAuthorityState(state));
        }

        if (errors.Count > 0)
            throw new AggregateException($"TLS rollback for '{normalizedDomain}' was incomplete.", errors);
    }

    private void RestoreAuthorityState(TlsRollbackState state)
    {
        using var authority = new LocalCertificateAuthorityService(_rootPath);
        if (!state.LocalCaExisted)
        {
            if (authority.Exists)
                authority.RemoveAuthority();
            return;
        }

        if (!authority.Exists)
            throw new InvalidOperationException("The DevBox Local CA disappeared while restoring TLS transaction state.");

        var currentlyTrusted = authority.IsTrustedCurrentUser();
        if (state.LocalCaTrusted && !currentlyTrusted)
            authority.TrustCurrentUser();
        else if (!state.LocalCaTrusted && currentlyTrusted)
            _ = authority.RemoveTrustCurrentUser();
    }

    private static void TrustLeaf(string certificatePath)
    {
        using var certificate = LocalCertificateManager.LoadPublicCertificate(certificatePath);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count == 0)
            store.Add(certificate);
    }

    private string CertificatePath(string domain) => Path.Combine(_rootPath, "config", "ssl", "sites", $"{domain}.crt.pem");
    private string PrivateKeyPath(string domain) => Path.Combine(_rootPath, "config", "ssl", "sites", $"{domain}.key.pem");

    private static void RestoreBytes(string path, byte[]? content)
    {
        if (content is null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, content);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
