using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DevBox.Core.Services;

public sealed partial class LocalCertificateAuthorityService : IDisposable
{
    internal Func<X509Certificate2, bool>? IsTrustedCurrentUserOverride { get; set; }
    internal Action<X509Certificate2>? RemoveTrustCurrentUserOverride { get; set; }
    internal Action<X509Certificate2>? RestoreTrustCurrentUserOverride { get; set; }
    internal Action<string>? DeleteAuthorityFileOverride { get; set; }

    public X509Certificate2 EnsureRootTrusted() => EnsureAuthority(trustCurrentUser: true);

    public void RemoveAuthority()
    {
        EnsureWindows();
        using var availableAuthority = LoadAvailableAuthority();
        var wasTrusted = availableAuthority is not null && ProbeTrustCurrentUser(availableAuthority);
        var pfxState = CaptureAuthorityFile(_caPfxPath);
        var pemState = CaptureAuthorityFile(_caPemPath);

        try
        {
            if (availableAuthority is not null)
                RemoveAuthorityTrustCurrentUser(availableAuthority);

            DeleteAuthorityFile(_caPfxPath);
            DeleteAuthorityFile(_caPemPath);
            if (File.Exists(_caPfxPath) || File.Exists(_caPemPath))
                throw new IOException("The DevBox Local CA could not be removed completely.");

            _secrets.Delete(PasswordSecretKey);
        }
        catch (Exception original)
        {
            var rollbackErrors = new List<Exception>();

            try
            {
                RestoreAuthorityFile(_caPfxPath, pfxState);
            }
            catch (Exception ex)
            {
                rollbackErrors.Add(ex);
            }

            try
            {
                RestoreAuthorityFile(_caPemPath, pemState);
            }
            catch (Exception ex)
            {
                rollbackErrors.Add(ex);
            }

            if (wasTrusted && availableAuthority is not null)
            {
                try
                {
                    RestoreAuthorityTrustCurrentUser(availableAuthority);
                }
                catch (Exception ex)
                {
                    rollbackErrors.Add(ex);
                }
            }

            if (rollbackErrors.Count > 0)
                throw new AggregateException(
                    "Local CA removal failed and one or more rollback operations also failed.",
                    new[] { original }.Concat(rollbackErrors));

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
            catch (CryptographicException)
            {
                // If the PEM is corrupt, recover from a usable PFX when possible. If the
                // PFX is also unusable (for example because its DPAPI password was never
                // persisted), there is no trustworthy thumbprint to clean from the store;
                // removal may still safely discard the unusable local material.
                return TryLoadPfxAuthority();
            }
        }

        return TryLoadPfxAuthority();
    }

    private X509Certificate2? TryLoadPfxAuthority()
    {
        if (!File.Exists(_caPfxPath))
            return null;

        try
        {
            return LoadAuthority();
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException)
        {
            return null;
        }
    }

    private bool ProbeTrustCurrentUser(X509Certificate2 certificate) =>
        IsTrustedCurrentUserOverride?.Invoke(certificate) ?? IsTrustedCurrentUser(certificate);

    private void RemoveAuthorityTrustCurrentUser(X509Certificate2 certificate)
    {
        if (RemoveTrustCurrentUserOverride is not null)
        {
            RemoveTrustCurrentUserOverride(certificate);
            return;
        }

        RemoveTrustCurrentUser(certificate);
    }

    private void RestoreAuthorityTrustCurrentUser(X509Certificate2 certificate)
    {
        if (RestoreTrustCurrentUserOverride is not null)
        {
            RestoreTrustCurrentUserOverride(certificate);
            return;
        }

        TrustCurrentUser(certificate);
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

    private static byte[]? CaptureAuthorityFile(string path) =>
        File.Exists(path) ? File.ReadAllBytes(path) : null;

    private void DeleteAuthorityFile(string path)
    {
        if (!File.Exists(path))
            return;

        if (DeleteAuthorityFileOverride is not null)
        {
            DeleteAuthorityFileOverride(path);
            return;
        }

        File.Delete(path);
    }

    private static void RestoreAuthorityFile(string path, byte[]? content)
    {
        if (content is null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        if (File.Exists(path))
        {
            try
            {
                if (File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                    return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.rollback.tmp";
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
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
