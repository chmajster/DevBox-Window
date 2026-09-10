using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace DevBox.Core.Services;

public sealed partial class LocalCertificateAuthorityService
{
    private const string PasswordSecretKey = "ssl.local-ca.pfx-password";
    private readonly string _rootPath;
    private readonly string _caDirectory;
    private readonly string _caPfxPath;
    private readonly string _caPemPath;
    private readonly SecureSecretStore _secrets;

    public LocalCertificateAuthorityService(string rootPath, SecureSecretStore? secrets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _caDirectory = Path.Combine(_rootPath, "config", "ssl", "ca");
        _caPfxPath = Path.Combine(_caDirectory, "devbox-local-ca.pfx");
        _caPemPath = Path.Combine(_caDirectory, "devbox-local-ca.crt.pem");
        _secrets = secrets ?? new SecureSecretStore(_rootPath);
    }

    public bool Exists => File.Exists(_caPfxPath) && File.Exists(_caPemPath);

    public string CertificatePath => _caPemPath;

    public X509Certificate2 EnsureAuthority(bool trustCurrentUser = true)
    {
        EnsureWindows();
        Directory.CreateDirectory(_caDirectory);
        if (!Exists)
            CreateAuthority();

        var certificate = LoadAuthority();
        if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30))
            throw new InvalidOperationException("The DevBox local CA is close to expiry. Remove it and create a new authority before issuing more certificates.");
        if (trustCurrentUser)
            TrustCurrentUser(certificate);
        return certificate;
    }

    public X509Certificate2 IssueSiteCertificate(string domain, bool trustAuthority = true)
    {
        EnsureWindows();
        var normalizedDomain = LocalCertificateManager.NormalizeDomain(domain);
        var rollbackService = new TlsRollbackStateService(_rootPath);
        var rollbackState = rollbackService.Capture(normalizedDomain);
        try
        {
            using var authority = EnsureAuthority(trustAuthority);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                new X500DistinguishedName($"CN={normalizedDomain}"),
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(normalizedDomain);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7f;
            if (serial.All(value => value == 0))
                serial[^1] = 1;
            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = DateTimeOffset.UtcNow.AddDays(397);
            using var signed = request.Create(authority, notBefore, notAfter, serial);
            using var certificate = signed.CopyWithPrivateKey(key);

            var sitesDirectory = Path.Combine(_rootPath, "config", "ssl", "sites");
            Directory.CreateDirectory(sitesDirectory);
            var certPath = Path.Combine(sitesDirectory, $"{normalizedDomain}.crt.pem");
            var keyPath = Path.Combine(sitesDirectory, $"{normalizedDomain}.key.pem");

            if (File.Exists(certPath))
                new LocalCertificateManager(_rootPath).UntrustForCurrentUser(normalizedDomain);

            AtomicWrite(certPath, certificate.ExportCertificatePem() + authority.ExportCertificatePem());
            AtomicWrite(keyPath, key.ExportPkcs8PrivateKeyPem());
            return new X509Certificate2(certificate.Export(X509ContentType.Cert));
        }
        catch
        {
            rollbackService.Restore(rollbackState);
            throw;
        }
    }

    public bool IsTrustedCurrentUser()
    {
        EnsureWindows();
        if (!Exists)
            return false;
        using var certificate = LoadAuthority(publicOnly: true);
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0;
    }

    public void TrustCurrentUser()
    {
        EnsureWindows();
        using var certificate = EnsureAuthority(trustCurrentUser: false);
        TrustCurrentUser(certificate);
    }

    public bool RemoveTrustCurrentUser()
    {
        EnsureWindows();
        if (!Exists)
            return false;
        using var certificate = LoadAuthority(publicOnly: true);
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

    public void RotateAuthority()
    {
        EnsureWindows();
        if (Exists)
            _ = RemoveTrustCurrentUser();
        TryDeleteFile(_caPfxPath);
        TryDeleteFile(_caPemPath);
        _secrets.Delete(PasswordSecretKey);
        _ = EnsureAuthority(trustCurrentUser: true);
    }

    private void CreateAuthority()
    {
        var passwordBytes = RandomNumberGenerator.GetBytes(32);
        var password = Convert.ToBase64String(passwordBytes);
        CryptographicOperations.ZeroMemory(passwordBytes);
        try
        {
            using var key = RSA.Create(4096);
            var request = new CertificateRequest(
                new X500DistinguishedName("CN=DevBox Local Development CA, O=DevBox"),
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
            var pfx = certificate.Export(X509ContentType.Pfx, password);
            try
            {
                File.WriteAllBytes(_caPfxPath, pfx);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
            AtomicWrite(_caPemPath, certificate.ExportCertificatePem());
            _secrets.Set(PasswordSecretKey, password);
            TryHide(_caPfxPath);
        }
        finally
        {
            password = string.Empty;
        }
    }

    private X509Certificate2 LoadAuthority(bool publicOnly = false)
    {
        if (publicOnly)
            return X509Certificate2.CreateFromPemFile(_caPemPath);
        var password = _secrets.Get(PasswordSecretKey)
            ?? throw new InvalidDataException("The local CA private-key password is missing from the DPAPI secret store.");
        try
        {
            return new X509Certificate2(_caPfxPath, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
        finally
        {
            password = string.Empty;
        }
    }

    private static void TrustCurrentUser(X509Certificate2 certificate)
    {
        using var publicCertificate = new X509Certificate2(certificate.Export(X509ContentType.Cert));
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (store.Certificates.Find(X509FindType.FindByThumbprint, publicCertificate.Thumbprint, validOnly: false).Count == 0)
            store.Add(publicCertificate);
    }

    private static void ValidateDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        if (!TestDomainRegex().IsMatch(domain))
            throw new ArgumentException("Local CA only issues DNS certificates for normalized .test domains.", nameof(domain));
    }

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, content);
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

    private static void TryHide(string path)
    {
        try
        {
            if (File.Exists(path))
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DevBox local certificate authority requires Windows certificate stores and DPAPI.");
    }

    [GeneratedRegex("^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+test$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestDomainRegex();
}
