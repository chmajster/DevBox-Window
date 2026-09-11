using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class CertificateTrustStoreException : Exception
{
    public CertificateTrustStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed partial class LocalCertificateManager
{
    private readonly string _certificateRoot;

    public LocalCertificateManager(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _certificateRoot = Path.GetFullPath(Path.Combine(rootPath, "config", "ssl", "sites"));
    }

    public LocalCertificate Ensure(string domain)
    {
        var normalizedDomain = NormalizeDomain(domain);
        Directory.CreateDirectory(_certificateRoot);
        var certificatePath = CertificatePath(normalizedDomain);
        var privateKeyPath = PrivateKeyPath(normalizedDomain);
        string? replacedThumbprint = null;

        if (File.Exists(certificatePath) && File.Exists(privateKeyPath))
        {
            try
            {
                using var existing = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
                if (IsCertificateValidForDomain(existing, normalizedDomain, TimeSpan.FromDays(7)))
                    return ToModel(normalizedDomain, certificatePath, privateKeyPath, existing);
                replacedThumbprint = existing.Thumbprint;
            }
            catch (CryptographicException)
            {
            }
        }

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            $"CN={normalizedDomain}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(normalizedDomain);
        request.CertificateExtensions.Add(san.Build());

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = DateTimeOffset.UtcNow.AddYears(2);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);

        var previousCertificate = File.Exists(certificatePath) ? File.ReadAllBytes(certificatePath) : null;
        var previousKey = File.Exists(privateKeyPath) ? File.ReadAllBytes(privateKeyPath) : null;
        var previousWasTrusted = false;
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(replacedThumbprint))
        {
            try { previousWasTrusted = IsTrustedForCurrentUser(normalizedDomain); }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { }
        }

        try
        {
            AtomicWrite(certificatePath, certificate.ExportCertificatePem());
            AtomicWrite(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());

            if (!string.IsNullOrWhiteSpace(replacedThumbprint) &&
                !replacedThumbprint.Equals(certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                RemoveTrustedThumbprint(replacedThumbprint);
        }
        catch
        {
            RestoreBytes(certificatePath, previousCertificate);
            RestoreBytes(privateKeyPath, previousKey);
            if (previousWasTrusted && OperatingSystem.IsWindows())
            {
                try { TrustForCurrentUser(normalizedDomain); }
                catch (Exception) { }
            }
            throw;
        }

        return ToModel(normalizedDomain, certificatePath, privateKeyPath, certificate);
    }

    public bool IsMaterialValid(string domain, TimeSpan? minimumRemainingLifetime = null)
    {
        var normalizedDomain = NormalizeDomain(domain);
        var certificatePath = CertificatePath(normalizedDomain);
        var privateKeyPath = PrivateKeyPath(normalizedDomain);
        if (!File.Exists(certificatePath) || !File.Exists(privateKeyPath))
            return false;
        try
        {
            using var certificate = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
            return IsCertificateValidForDomain(certificate, normalizedDomain, minimumRemainingLifetime ?? TimeSpan.FromMinutes(1));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public bool IsTrustedForCurrentUser(string domain)
    {
        var certificatePath = CertificatePath(NormalizeDomain(domain));
        if (!File.Exists(certificatePath))
            return false;

        using var certificate = LoadPublicCertificate(certificatePath);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0;
    }

    public void TrustForCurrentUser(string domain)
    {
        var certificate = Ensure(domain);
        using var publicCertificate = LoadPublicCertificate(certificate.CertificatePath);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (store.Certificates.Find(X509FindType.FindByThumbprint, publicCertificate.Thumbprint, validOnly: false).Count == 0)
            store.Add(publicCertificate);
    }

    public void UntrustForCurrentUser(string domain)
    {
        var certificatePath = CertificatePath(NormalizeDomain(domain));
        if (!File.Exists(certificatePath))
            return;

        using var certificate = LoadPublicCertificate(certificatePath);
        RemoveTrustedThumbprint(certificate.Thumbprint);
    }

    public void Delete(string domain)
    {
        var normalizedDomain = NormalizeDomain(domain);
        var certificatePath = CertificatePath(normalizedDomain);
        var privateKeyPath = PrivateKeyPath(normalizedDomain);
        string? thumbprint = null;

        if (File.Exists(certificatePath))
        {
            try
            {
                using var certificate = LoadPublicCertificate(certificatePath);
                thumbprint = certificate.Thumbprint;
            }
            catch (CryptographicException)
            {
                // Corrupt PEM: no trustworthy thumbprint can be recovered. The local
                // material itself is unusable, so it may be removed below.
            }
        }

        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            try
            {
                RemoveTrustedThumbprint(thumbprint);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Keep cert/key files intact when the trust store cannot be updated.
                // Callers can retry later and still have the leaf thumbprint available.
                throw new CertificateTrustStoreException(
                    $"Could not remove certificate trust for '{normalizedDomain}'. TLS files were preserved.",
                    ex);
            }
        }

        DeleteIfExists(certificatePath);
        DeleteIfExists(privateKeyPath);
    }

    private static void RemoveTrustedThumbprint(string thumbprint)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var match in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
            store.Remove(match);
    }

    private string CertificatePath(string domain) => Path.Combine(_certificateRoot, $"{domain}.crt.pem");
    private string PrivateKeyPath(string domain) => Path.Combine(_certificateRoot, $"{domain}.key.pem");

    private static bool IsCertificateValidForDomain(X509Certificate2 certificate, string domain, TimeSpan minimumRemainingLifetime)
    {
        var now = DateTime.UtcNow;
        var dnsName = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);
        return certificate.NotBefore.ToUniversalTime() <= now.AddMinutes(5) &&
               certificate.NotAfter.ToUniversalTime() > now.Add(minimumRemainingLifetime) &&
               dnsName.Equals(domain, StringComparison.OrdinalIgnoreCase);
    }

    private static LocalCertificate ToModel(string domain, string certificatePath, string privateKeyPath, X509Certificate2 certificate) =>
        new(
            domain,
            certificatePath,
            privateKeyPath,
            certificate.Thumbprint,
            certificate.NotBefore.ToUniversalTime(),
            certificate.NotAfter.ToUniversalTime());

    internal static X509Certificate2 LoadPublicCertificate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return X509Certificate2.CreateFromPem(File.ReadAllText(path));
    }

    internal static string NormalizeDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (!normalized.EndsWith(".test", StringComparison.OrdinalIgnoreCase) || !DomainRegex().IsMatch(normalized))
            throw new ArgumentException("Local certificate domains must be valid .test names.", nameof(domain));
        return normalized;
    }

    private static void AtomicWrite(string path, string content)
    {
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content);
            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            DeleteIfExists(tempPath);
        }
    }

    private static void RestoreBytes(string path, byte[]? content)
    {
        if (content is null)
        {
            DeleteIfExists(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.rollback";
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
            DeleteIfExists(temp);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+test$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainRegex();
}
