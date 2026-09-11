from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one exact match, found {count}")
    write(path, text.replace(old, new, 1))


def regex_once(path, pattern, replacement):
    text = read(path)
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"{path}: regex did not match exactly once: {pattern[:80]}")
    write(path, updated)


# 1. Reparse-point-aware path containment.
write("src/DevBox.Core/Services/PathSafety.cs", r'''namespace DevBox.Core.Services;

internal static class PathSafety
{
    public static string EnsureUnderRootWithoutReparsePoints(string rootPath, string candidatePath, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(message);

        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        var current = fullRoot;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"{message} Reparse-point path segment is not allowed: {current}");
        }

        return fullCandidate;
    }
}
''')

replace_once(
    "src/DevBox.Core/Services/SiteManager.cs",
    '''        var fullWwwRoot = _wwwRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;\n        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        if (!fullPath.StartsWith(fullWwwRoot, StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException("Site document root must be inside the DevBox www directory.");\n        return fullPath;''',
    '''        return PathSafety.EnsureUnderRootWithoutReparsePoints(\n            _wwwRoot,\n            path,\n            "Site document root must be inside the DevBox www directory and cannot traverse a reparse point.");''')

replace_once(
    "src/DevBox.Core/Services/ProjectWorkspaceService.cs",
    '''        var projectRoot = Path.Combine(_wwwRoot, NormalizeProjectDirectoryName(request.Name));\n        var projectRootExisted = Directory.Exists(projectRoot);''',
    '''        var projectRoot = Path.Combine(_wwwRoot, NormalizeProjectDirectoryName(request.Name));\n        _ = PathSafety.EnsureUnderRootWithoutReparsePoints(\n            _wwwRoot, projectRoot, "Project destination must remain inside DevBox www and cannot traverse a reparse point.");\n        var projectRootExisted = Directory.Exists(projectRoot);''')

replace_once(
    "src/DevBox.Core/Services/ProjectWorkspaceService.cs",
    '''        var projectRootExisted = Directory.Exists(projectRoot);\n        if (request.CopyIntoDevBox && projectRootExisted && Directory.EnumerateFileSystemEntries(projectRoot).Any())''',
    '''        _ = PathSafety.EnsureUnderRootWithoutReparsePoints(\n            _wwwRoot, projectRoot, "Project import destination must remain inside DevBox www and cannot traverse a reparse point.");\n        var projectRootExisted = Directory.Exists(projectRoot);\n        if (request.CopyIntoDevBox && projectRootExisted && Directory.EnumerateFileSystemEntries(projectRoot).Any())''')

replace_once(
    "src/DevBox.Core/Services/ProjectWorkspaceService.cs",
    '''        var fullWww = _wwwRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;\n        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        if (!fullPath.StartsWith(fullWww, StringComparison.OrdinalIgnoreCase))\n        {\n            throw new InvalidOperationException("Existing projects can only be registered in-place when they are already inside the DevBox www directory. Enable CopyIntoDevBox for external projects.");\n        }\n        return fullPath;''',
    '''        return PathSafety.EnsureUnderRootWithoutReparsePoints(\n            _wwwRoot,\n            path,\n            "Existing projects can only be registered in-place when they are already inside DevBox www and the path does not traverse a reparse point. Enable CopyIntoDevBox for external projects.");''')

replace_once(
    "src/DevBox.Core/Services/WordPressToolkitService.cs",
    '''        var root = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        var www = Path.GetFullPath(Path.Combine(_rootPath, "www")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;\n        if (!Directory.Exists(root))\n            throw new DirectoryNotFoundException($"Project directory was not found: {root}");\n        if (!root.StartsWith(www, StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException("WordPress Toolkit is restricted to projects in the DevBox www directory.");\n        return root;''',
    '''        var root = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        if (!Directory.Exists(root))\n            throw new DirectoryNotFoundException($"Project directory was not found: {root}");\n        return PathSafety.EnsureUnderRootWithoutReparsePoints(\n            Path.Combine(_rootPath, "www"),\n            root,\n            "WordPress Toolkit is restricted to projects in DevBox www and cannot traverse a reparse point.");''')

# 2. TLS leaf operations: avoid reacquiring the same domain lock during rollback.
replace_once(
    "src/DevBox.Core/Services/LocalCertificateManager.cs",
    '''                try { TrustForCurrentUser(normalizedDomain); }\n                catch (Exception) { }''',
    '''                try { TrustForCurrentUserUnlocked(normalizedDomain); }\n                catch (Exception) { }''')

replace_once(
    "src/DevBox.Core/Services/LocalCertificateManager.cs",
    '''    public void TrustForCurrentUser(string domain)\n    {\n        var normalizedDomain = NormalizeDomain(domain);\n        using var domainLock = CrossProcessFileLock.Acquire(GetDomainLockPath(_rootPath, normalizedDomain));\n        var certificate = EnsureCore(normalizedDomain);\n        using var publicCertificate = LoadPublicCertificate(certificate.CertificatePath);\n        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);\n        store.Open(OpenFlags.ReadWrite);\n        if (store.Certificates.Find(X509FindType.FindByThumbprint, publicCertificate.Thumbprint, validOnly: false).Count == 0)\n            store.Add(publicCertificate);\n    }''',
    '''    public void TrustForCurrentUser(string domain)\n    {\n        var normalizedDomain = NormalizeDomain(domain);\n        using var domainLock = CrossProcessFileLock.Acquire(GetDomainLockPath(_rootPath, normalizedDomain));\n        _ = EnsureCore(normalizedDomain);\n        TrustForCurrentUserUnlocked(normalizedDomain);\n    }\n\n    internal void TrustForCurrentUserUnlocked(string normalizedDomain)\n    {\n        var certificatePath = CertificatePath(normalizedDomain);\n        if (!File.Exists(certificatePath))\n            throw new FileNotFoundException("Local TLS certificate was not found.", certificatePath);\n        using var publicCertificate = LoadPublicCertificate(certificatePath);\n        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);\n        store.Open(OpenFlags.ReadWrite);\n        if (store.Certificates.Find(X509FindType.FindByThumbprint, publicCertificate.Thumbprint, validOnly: false).Count == 0)\n            store.Add(publicCertificate);\n    }''')

replace_once(
    "src/DevBox.Core/Services/TlsRollbackStateService.cs",
    '''    public void Restore(TlsRollbackState state)\n    {\n        ArgumentNullException.ThrowIfNull(state);''',
    '''    public void Restore(TlsRollbackState state)\n    {\n        ArgumentNullException.ThrowIfNull(state);\n        var normalizedDomain = LocalCertificateManager.NormalizeDomain(state.Domain);\n        using var domainLock = CrossProcessFileLock.Acquire(LocalCertificateManager.GetDomainLockPath(_rootPath, normalizedDomain));\n        RestoreUnderLock(state);\n    }\n\n    internal void RestoreUnderLock(TlsRollbackState state)\n    {\n        ArgumentNullException.ThrowIfNull(state);''')

text = read("src/DevBox.Core/Services/TlsRollbackStateService.cs")
text = text.replace("_certificates.UntrustForCurrentUser(normalizedDomain)", "_certificates.UntrustForCurrentUserUnlocked(normalizedDomain)")
write("src/DevBox.Core/Services/TlsRollbackStateService.cs", text)

# 3. Local CA: global serialization, transactional creation and rotation.
replace_once(
    "src/DevBox.Core/Services/LocalCertificateAuthorityService.cs",
    '''    public X509Certificate2 EnsureAuthority(bool trustCurrentUser = true)\n    {\n        EnsureWindows();\n        Directory.CreateDirectory(_caDirectory);\n        if (!Exists)\n            CreateAuthority();\n\n        var certificate = LoadAuthority();\n        if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30))\n            throw new InvalidOperationException("The DevBox local CA is close to expiry. Remove it and create a new authority before issuing more certificates.");\n        if (trustCurrentUser)\n            TrustCurrentUser(certificate);\n        return certificate;\n    }''',
    '''    public X509Certificate2 EnsureAuthority(bool trustCurrentUser = true)\n    {\n        EnsureWindows();\n        using var authorityLock = AcquireAuthorityLock();\n        return EnsureAuthorityUnderLock(trustCurrentUser);\n    }\n\n    private X509Certificate2 EnsureAuthorityUnderLock(bool trustCurrentUser)\n    {\n        Directory.CreateDirectory(_caDirectory);\n        if (!Exists)\n            CreateAuthority();\n\n        var certificate = LoadAuthority();\n        if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30))\n            throw new InvalidOperationException("The DevBox local CA is close to expiry. Remove it and create a new authority before issuing more certificates.");\n        if (trustCurrentUser)\n            TrustCurrentUser(certificate);\n        return certificate;\n    }''')

replace_once(
    "src/DevBox.Core/Services/LocalCertificateAuthorityService.cs",
    '''            rollbackState = rollbackService.Capture(normalizedDomain);\n            using var authority = EnsureAuthority(trustAuthority);''',
    '''            rollbackState = rollbackService.Capture(normalizedDomain);\n            using var authorityLock = AcquireAuthorityLock();\n            using var authority = EnsureAuthorityUnderLock(trustAuthority);''')

replace_once(
    "src/DevBox.Core/Services/LocalCertificateAuthorityService.cs",
    '''    public void TrustCurrentUser()\n    {\n        EnsureWindows();\n        using var certificate = EnsureAuthority(trustCurrentUser: false);\n        TrustCurrentUser(certificate);\n    }''',
    '''    public void TrustCurrentUser()\n    {\n        EnsureWindows();\n        using var authorityLock = AcquireAuthorityLock();\n        using var certificate = EnsureAuthorityUnderLock(trustCurrentUser: false);\n        TrustCurrentUser(certificate);\n    }''')

replace_once(
    "src/DevBox.Core/Services/LocalCertificateAuthorityService.cs",
    '''    public void RotateAuthority()\n    {\n        EnsureWindows();\n        RemoveAuthority();\n        using var replacement = EnsureAuthority(trustCurrentUser: true);\n    }''',
    '''    public void RotateAuthority()\n    {\n        EnsureWindows();\n        using var authorityLock = AcquireAuthorityLock();\n        var previousPfx = CaptureAuthorityFile(_caPfxPath);\n        var previousPem = CaptureAuthorityFile(_caPemPath);\n        var previousPassword = _secrets.Get(PasswordSecretKey);\n        using var previousAuthority = LoadAvailableAuthority();\n        var previousTrusted = previousAuthority is not null && ProbeTrustCurrentUser(previousAuthority);\n\n        try\n        {\n            RemoveAuthorityUnderLock();\n            using var replacement = EnsureAuthorityUnderLock(trustCurrentUser: true);\n        }\n        catch (Exception original)\n        {\n            var rollbackErrors = new List<Exception>();\n            void Attempt(Action action)\n            {\n                try { action(); }\n                catch (Exception ex) { rollbackErrors.Add(ex); }\n            }\n\n            Attempt(() => RestoreAuthorityFile(_caPfxPath, previousPfx));\n            Attempt(() => RestoreAuthorityFile(_caPemPath, previousPem));\n            Attempt(() =>\n            {\n                if (previousPassword is null)\n                    _secrets.Delete(PasswordSecretKey);\n                else\n                    _secrets.Set(PasswordSecretKey, previousPassword);\n            });\n            if (previousTrusted && previousAuthority is not null)\n                Attempt(() => RestoreAuthorityTrustCurrentUser(previousAuthority));\n\n            if (rollbackErrors.Count > 0)\n                throw new AggregateException(\n                    "Local CA rotation failed and one or more rollback operations also failed.",\n                    new[] { original }.Concat(rollbackErrors));\n            throw;\n        }\n        finally\n        {\n            previousPassword = string.Empty;\n        }\n    }''')

replace_once(
    "src/DevBox.Core/Services/LocalCertificateAuthorityService.cs",
    '''    private void CreateAuthority()\n    {\n        var passwordBytes = RandomNumberGenerator.GetBytes(32);\n        var password = Convert.ToBase64String(passwordBytes);\n        CryptographicOperations.ZeroMemory(passwordBytes);\n        try\n        {\n            using var key = RSA.Create(4096);\n            var request = new CertificateRequest(\n                new X500DistinguishedName("CN=DevBox Local Development CA, O=DevBox"),\n                key,\n                HashAlgorithmName.SHA256,\n                RSASignaturePadding.Pkcs1);\n            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));\n            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));\n            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));\n            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));\n            var pfx = certificate.Export(X509ContentType.Pfx, password);\n            try\n            {\n                File.WriteAllBytes(_caPfxPath, pfx);\n            }\n            finally\n            {\n                CryptographicOperations.ZeroMemory(pfx);\n            }\n            AtomicWrite(_caPemPath, certificate.ExportCertificatePem());\n            _secrets.Set(PasswordSecretKey, password);\n            TryHide(_caPfxPath);\n        }\n        finally\n        {\n            password = string.Empty;\n        }\n    }''',
    '''    private void CreateAuthority()\n    {\n        var previousPfx = CaptureAuthorityFile(_caPfxPath);\n        var previousPem = CaptureAuthorityFile(_caPemPath);\n        var previousPassword = _secrets.Get(PasswordSecretKey);\n        var passwordBytes = RandomNumberGenerator.GetBytes(32);\n        var password = Convert.ToBase64String(passwordBytes);\n        CryptographicOperations.ZeroMemory(passwordBytes);\n        try\n        {\n            using var key = RSA.Create(4096);\n            var request = new CertificateRequest(\n                new X500DistinguishedName("CN=DevBox Local Development CA, O=DevBox"),\n                key,\n                HashAlgorithmName.SHA256,\n                RSASignaturePadding.Pkcs1);\n            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));\n            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));\n            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));\n            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));\n            var pfx = certificate.Export(X509ContentType.Pfx, password);\n            try\n            {\n                AtomicWriteBytes(_caPfxPath, pfx);\n                AtomicWrite(_caPemPath, certificate.ExportCertificatePem());\n                _secrets.Set(PasswordSecretKey, password);\n                TryHide(_caPfxPath);\n            }\n            catch (Exception original)\n            {\n                var rollbackErrors = new List<Exception>();\n                void Attempt(Action action)\n                {\n                    try { action(); }\n                    catch (Exception ex) { rollbackErrors.Add(ex); }\n                }\n                Attempt(() => RestoreAuthorityFile(_caPfxPath, previousPfx));\n                Attempt(() => RestoreAuthorityFile(_caPemPath, previousPem));\n                Attempt(() =>\n                {\n                    if (previousPassword is null)\n                        _secrets.Delete(PasswordSecretKey);\n                    else\n                        _secrets.Set(PasswordSecretKey, previousPassword);\n                });\n                if (rollbackErrors.Count > 0)\n                    throw new AggregateException(\n                        "Local CA creation failed and rollback was incomplete.",\n                        new[] { original }.Concat(rollbackErrors));\n                throw;\n            }\n            finally\n            {\n                CryptographicOperations.ZeroMemory(pfx);\n            }\n        }\n        finally\n        {\n            password = string.Empty;\n            previousPassword = string.Empty;\n        }\n    }''')

replace_once(
    "src/DevBox.Core/Services/LocalCertificateAuthorityService.cs",
    '''    private static void AtomicWrite(string path, string content)\n    {''',
    '''    internal FileStream AcquireAuthorityLock() =>\n        CrossProcessFileLock.Acquire(Path.Combine(_rootPath, "tmp", "locks", "local-ca.lock"), TimeSpan.FromSeconds(30));\n\n    private static void AtomicWriteBytes(string path, byte[] content)\n    {\n        var temp = path + $".{Guid.NewGuid():N}.tmp";\n        try\n        {\n            File.WriteAllBytes(temp, content);\n            if (File.Exists(path))\n                File.Replace(temp, path, null);\n            else\n                File.Move(temp, path);\n        }\n        finally\n        {\n            if (File.Exists(temp))\n                File.Delete(temp);\n        }\n    }\n\n    private static void AtomicWrite(string path, string content)\n    {''')

replace_once(
    "src/DevBox.Core/Services/LocalCertificateAuthorityService.Lifetime.cs",
    '''    public void RemoveAuthority()\n    {\n        EnsureWindows();\n        using var availableAuthority = LoadAvailableAuthority();''',
    '''    public void RemoveAuthority()\n    {\n        EnsureWindows();\n        using var authorityLock = AcquireAuthorityLock();\n        RemoveAuthorityUnderLock();\n    }\n\n    private void RemoveAuthorityUnderLock()\n    {\n        using var availableAuthority = LoadAvailableAuthority();''')

# 4. ADDONS: serialize mutation and keep phpMyAdmin port in sync.
replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''        ArgumentNullException.ThrowIfNull(addon);\n        EnsureInstallPathIsSafe(addon);\n\n        var tempRoot = Path.Combine(_rootPath, "tmp", "addons", addon.Key, Guid.NewGuid().ToString("N"));''',
    '''        ArgumentNullException.ThrowIfNull(addon);\n        EnsureInstallPathIsSafe(addon);\n        using var addonLock = await CrossProcessFileLock.AcquireAsync(AddonLockPath(addon), cancellationToken, TimeSpan.FromSeconds(30)).ConfigureAwait(false);\n\n        var tempRoot = Path.Combine(_rootPath, "tmp", "addons", addon.Key, Guid.NewGuid().ToString("N"));''')

replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''        ArgumentNullException.ThrowIfNull(addon);\n        EnsureInstallPathIsSafe(addon);\n\n        if (!File.Exists(addon.EntryPointPath))''',
    '''        ArgumentNullException.ThrowIfNull(addon);\n        EnsureInstallPathIsSafe(addon);\n        using var addonLock = CrossProcessFileLock.Acquire(AddonLockPath(addon), TimeSpan.FromSeconds(30));\n\n        if (!File.Exists(addon.EntryPointPath))''')

replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''        ArgumentNullException.ThrowIfNull(addon);\n        EnsureInstallPathIsSafe(addon);\n\n        if (Directory.Exists(addon.InstallPath))''',
    '''        ArgumentNullException.ThrowIfNull(addon);\n        EnsureInstallPathIsSafe(addon);\n        using var addonLock = CrossProcessFileLock.Acquire(AddonLockPath(addon), TimeSpan.FromSeconds(30));\n\n        if (Directory.Exists(addon.InstallPath))''')

replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''    internal static bool IsPhpMyAdminConfigUsable(string content)\n    {''',
    '''    internal static bool IsPhpMyAdminConfigUsable(string content, int? expectedPort = null)\n    {''')

replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''        return requiredFragments.All(fragment => content.Contains(fragment, StringComparison.Ordinal));''',
    '''        if (!requiredFragments.All(fragment => content.Contains(fragment, StringComparison.Ordinal)))\n            return false;\n        return expectedPort is null ||\n               content.Contains($"$cfg['Servers'][$i]['port'] = '{expectedPort.Value}';", StringComparison.Ordinal);''')

replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''        var tempDirectory = Path.Combine(addon.InstallPath, "tmp");\n        Directory.CreateDirectory(tempDirectory);\n        var configPath = Path.Combine(addon.InstallPath, "config.inc.php");\n        if (File.Exists(configPath))\n        {\n            var existing = File.ReadAllText(configPath);\n            if (IsPhpMyAdminConfigUsable(existing))\n            {\n                return;\n            }\n        }\n\n        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();\n        var databasePort = ResolvePhpMyAdminPort();''',
    '''        var tempDirectory = Path.Combine(addon.InstallPath, "tmp");\n        Directory.CreateDirectory(tempDirectory);\n        var configPath = Path.Combine(addon.InstallPath, "config.inc.php");\n        var databasePort = ResolvePhpMyAdminPort();\n        if (File.Exists(configPath))\n        {\n            var existing = File.ReadAllText(configPath);\n            if (IsPhpMyAdminConfigUsable(existing, databasePort))\n                return;\n        }\n\n        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();''')

replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''    private static void AtomicWrite(string path, string content)\n    {''',
    '''    private string AddonLockPath(AddonDefinition addon)\n    {\n        var safeKey = new string(addon.Key.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());\n        return Path.Combine(_rootPath, "tmp", "locks", $"addon-{safeKey}.lock");\n    }\n\n    private static void AtomicWrite(string path, string content)\n    {''')

# 5. Serialize JSON registries across GUI/CLI.
replace_once(
    "src/DevBox.Core/Services/EnvironmentProfileService.cs",
    '''        ArgumentNullException.ThrowIfNull(profile);\n        Validate(profile);\n\n        var custom = LoadCustomProfiles().ToList();''',
    '''        ArgumentNullException.ThrowIfNull(profile);\n        Validate(profile);\n        using var mutationLock = CrossProcessFileLock.Acquire(_profilesPath + ".lock", TimeSpan.FromSeconds(15));\n\n        var custom = LoadCustomProfiles().ToList();''')

replace_once(
    "src/DevBox.Core/Services/EnvironmentProfileService.cs",
    '''        ArgumentException.ThrowIfNullOrWhiteSpace(key);\n        var custom = LoadCustomProfiles().ToList();''',
    '''        ArgumentException.ThrowIfNullOrWhiteSpace(key);\n        using var mutationLock = CrossProcessFileLock.Acquire(_profilesPath + ".lock", TimeSpan.FromSeconds(15));\n        var custom = LoadCustomProfiles().ToList();''')

replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''    public void Save(IReadOnlyCollection<ManagedServiceManifest> manifests)\n    {\n        ArgumentNullException.ThrowIfNull(manifests);\n        ValidateAll(manifests);\n        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);\n        AtomicWrite(_manifestPath, JsonSerializer.Serialize(manifests.OrderBy(item => item.Key), JsonOptions));\n    }''',
    '''    public void Save(IReadOnlyCollection<ManagedServiceManifest> manifests)\n    {\n        ArgumentNullException.ThrowIfNull(manifests);\n        ValidateAll(manifests);\n        using var mutationLock = CrossProcessFileLock.Acquire(_manifestPath + ".lock", TimeSpan.FromSeconds(15));\n        SaveUnderLock(manifests);\n    }\n\n    private void SaveUnderLock(IReadOnlyCollection<ManagedServiceManifest> manifests)\n    {\n        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);\n        AtomicWrite(_manifestPath, JsonSerializer.Serialize(manifests.OrderBy(item => item.Key), JsonOptions));\n    }''')

replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''        ArgumentNullException.ThrowIfNull(manifest);\n        Validate(manifest);\n        var manifests = GetManifests().ToList();''',
    '''        ArgumentNullException.ThrowIfNull(manifest);\n        Validate(manifest);\n        using var mutationLock = CrossProcessFileLock.Acquire(_manifestPath + ".lock", TimeSpan.FromSeconds(15));\n        var manifests = GetManifests().ToList();''')

replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''        Save(manifests);\n    }\n\n    public bool Remove(string key)''',
    '''        SaveUnderLock(manifests);\n    }\n\n    public bool Remove(string key)''')

replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''        ArgumentException.ThrowIfNullOrWhiteSpace(key);\n        var manifests = GetManifests().ToList();''',
    '''        ArgumentException.ThrowIfNullOrWhiteSpace(key);\n        using var mutationLock = CrossProcessFileLock.Acquire(_manifestPath + ".lock", TimeSpan.FromSeconds(15));\n        var manifests = GetManifests().ToList();''')

replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''            Save(manifests);\n        }\n        return removed;''',
    '''            SaveUnderLock(manifests);\n        }\n        return removed;''')

replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''        if (manifest.Port is < 1 or > 65535 || manifest.Port is 80 or 3306 or 9084)\n        {\n            throw new InvalidDataException($"Managed service port {manifest.Port} is invalid or reserved by a core DevBox service.");\n        }''',
    '''        if (manifest.Port is < 1 or > 65535 || IsReservedCorePort(manifest.Port))\n        {\n            throw new InvalidDataException($"Managed service port {manifest.Port} is invalid or reserved by a core DevBox service.");\n        }''')

replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''    private string ResolveRelativeFile(string relativePath, string name) => ResolveInsideRoot(relativePath, name);''',
    '''    private bool IsReservedCorePort(int port)\n    {\n        if (port is 80 or 443 or 3306 or 3316 or 5432 or 9084)\n            return true;\n\n        var databaseRegistrations = Path.Combine(_rootPath, "config", "database-runtimes.json");\n        if (!File.Exists(databaseRegistrations))\n            return false;\n        try\n        {\n            using var document = JsonDocument.Parse(File.ReadAllText(databaseRegistrations));\n            if (document.RootElement.ValueKind != JsonValueKind.Array)\n                return false;\n            foreach (var item in document.RootElement.EnumerateArray())\n            {\n                if ((item.TryGetProperty("Port", out var value) || item.TryGetProperty("port", out value)) &&\n                    value.TryGetInt32(out var registeredPort) && registeredPort == port)\n                    return true;\n            }\n            return false;\n        }\n        catch (JsonException ex)\n        {\n            throw new InvalidDataException("config/database-runtimes.json contains invalid JSON.", ex);\n        }\n    }\n\n    private string ResolveRelativeFile(string relativePath, string name) => ResolveInsideRoot(relativePath, name);''')

replace_once(
    "src/DevBox.Core/Services/RuntimePlatformService.cs",
    '''        ArgumentNullException.ThrowIfNull(package);\n        ValidatePackage(package);\n        var custom = LoadCustomCatalog().ToList();''',
    '''        ArgumentNullException.ThrowIfNull(package);\n        ValidatePackage(package);\n        using var mutationLock = CrossProcessFileLock.Acquire(_catalogPath + ".lock", TimeSpan.FromSeconds(15));\n        var custom = LoadCustomCatalog().ToList();''')

# 6. Database registration ports and autodiscovery race.
replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''        if (registrations.Any(item => item.Port == selectedPort && !(item.Engine.Equals(normalizedEngine, StringComparison.OrdinalIgnoreCase) && item.Version.Equals(version, StringComparison.OrdinalIgnoreCase))))\n            throw new InvalidOperationException($"Port {selectedPort} is already assigned to another DevBox database runtime.");''',
    '''        if (registrations.Any(item => item.Port == selectedPort && !(item.Engine.Equals(normalizedEngine, StringComparison.OrdinalIgnoreCase) && item.Version.Equals(version, StringComparison.OrdinalIgnoreCase))))\n            throw new InvalidOperationException($"Port {selectedPort} is already assigned to another DevBox database runtime.");\n        if (port.HasValue && (existing is null || existing.Port != selectedPort) && IsTcpPortInUse(selectedPort))\n            throw new InvalidOperationException($"Port {selectedPort} is already in use by another process.");''')

replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''        if (registrations.Count > 0)\n            SaveRegistrations(registrations);\n        return registrations;''',
    '''        return registrations;''')

replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''    private int ChooseAvailablePort(DatabaseEngineKind kind, IReadOnlyList<DatabaseRuntimeRegistration> registrations)\n    {''',
    '''    private static bool IsTcpPortInUse(int port) =>\n        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);\n\n    private int ChooseAvailablePort(DatabaseEngineKind kind, IReadOnlyList<DatabaseRuntimeRegistration> registrations)\n    {''')

# 7. Roll back TLS material when higher-level project setup fails after workspace creation.
replace_once(
    "src/DevBox.Core/Services/ProjectProvisioningService.cs",
    '''        var expectedProjectRoot = Path.Combine(_rootPath, "www", request.Name.Trim().ToLowerInvariant());\n        var projectRootExisted = Directory.Exists(expectedProjectRoot);\n        var site = _workspace.Create(request);''',
    '''        var expectedProjectRoot = Path.Combine(_rootPath, "www", request.Name.Trim().ToLowerInvariant());\n        var projectRootExisted = Directory.Exists(expectedProjectRoot);\n        var rollbackDomain = request.Domain ?? $"{request.Name.Trim().ToLowerInvariant()}.test";\n        var tlsRollback = new TlsRollbackStateService(_rootPath);\n        var tlsState = tlsRollback.Capture(rollbackDomain);\n        var site = _workspace.Create(request);''')

replace_once(
    "src/DevBox.Core/Services/ProjectProvisioningService.cs",
    '''            rollbackActions.Add(() => RollbackProjectDirectory(projectRoot, projectRootExisted));\n            RollbackExecutor.RethrowAfterRollback(original, rollbackActions.ToArray());''',
    '''            rollbackActions.Add(() => tlsRollback.Restore(tlsState));\n            rollbackActions.Add(() => RollbackProjectDirectory(projectRoot, projectRootExisted));\n            RollbackExecutor.RethrowAfterRollback(original, rollbackActions.ToArray());''')

replace_once(
    "src/DevBox.Core/Services/WordPressToolkitService.cs",
    '''        var databaseOptions = request.DatabaseOptions ?? ResolveDatabaseOptions(request.DatabaseEngine);\n        var databaseManager = new DatabaseManager(_rootPath);''',
    '''        var databaseOptions = request.DatabaseOptions ?? ResolveDatabaseOptions(request.DatabaseEngine);\n        var tlsRollback = new TlsRollbackStateService(_rootPath);\n        var tlsState = tlsRollback.Capture(request.Domain ?? $"{request.Name.Trim().ToLowerInvariant()}.test");\n        var databaseManager = new DatabaseManager(_rootPath);''')

replace_once(
    "src/DevBox.Core/Services/WordPressToolkitService.cs",
    '''            if (cleanupErrors.Count > 0)\n                throw new AggregateException("WordPress setup failed and cleanup was incomplete.", cleanupErrors);''',
    '''            try\n            {\n                tlsRollback.Restore(tlsState);\n            }\n            catch (Exception ex)\n            {\n                cleanupErrors.Add(ex);\n            }\n            if (cleanupErrors.Count > 0)\n                throw new AggregateException("WordPress setup failed and cleanup was incomplete.", cleanupErrors);''')

replace_once(
    "src/DevBox.Core/Services/GitProjectBootstrapService.cs",
    '''        var domain = NormalizeDomain(request.Domain ?? $"{projectName}.test");\n        Directory.CreateDirectory(_wwwRoot);''',
    '''        var domain = NormalizeDomain(request.Domain ?? $"{projectName}.test");\n        var tlsRollback = new TlsRollbackStateService(_rootPath);\n        var tlsState = tlsRollback.Capture(domain);\n        Directory.CreateDirectory(_wwwRoot);''')

replace_once(
    "src/DevBox.Core/Services/GitProjectBootstrapService.cs",
    '''                TryRollbackDestination(destination, destinationExisted);\n            }\n            throw;''',
    '''                TryRollbackDestination(destination, destinationExisted);\n            }\n            try { tlsRollback.Restore(tlsState); }\n            catch (Exception rollbackError)\n            {\n                throw new AggregateException("Git bootstrap failed and TLS rollback was incomplete.", rollbackError);\n            }\n            throw;''')

# 8. Kill cancelled database client processes.
replace_once(
    "src/DevBox.Core/Services/ProjectDatabaseProvisioner.cs",
    '''        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);\n        var output = await stdout.ConfigureAwait(false);''',
    '''        try\n        {\n            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);\n        }\n        catch (OperationCanceledException)\n        {\n            TryKill(process);\n            throw;\n        }\n        var output = await stdout.ConfigureAwait(false);''')

replace_once(
    "src/DevBox.Core/Services/ProjectDatabaseProvisioner.cs",
    '''    private static void EnsureFile(string path, string message)\n    {''',
    '''    private static void TryKill(Process process)\n    {\n        try\n        {\n            if (!process.HasExited)\n                process.Kill(entireProcessTree: true);\n        }\n        catch (InvalidOperationException) { }\n        catch (Win32Exception) { }\n    }\n\n    private static void EnsureFile(string path, string message)\n    {''')

# 9. Self-updater: trusted signature plus same publisher identity as the running DevBox binary.
regex_once(
    "src/DevBox.Core/Services/ApplicationSelfUpdateService.cs",
    r'''    internal static void VerifyAuthenticodeSignature\(string path\)\n    \{.*?\n    \}\n\n    \[StructLayout\(LayoutKind\.Sequential, CharSet = CharSet\.Unicode\)\]\n    private struct WinTrustFileInfo''',
    r'''    internal static void VerifyAuthenticodeSignature(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Authenticode verification requires Windows.");
        VerifyWinTrust(path);

        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
            throw new InvalidDataException("DevBox could not determine the running executable for publisher verification.");
        VerifyWinTrust(currentPath);

        using var downloadedSigner = GetSignerCertificate(path);
        using var currentSigner = GetSignerCertificate(currentPath);
        if (!downloadedSigner.SubjectName.RawData.AsSpan().SequenceEqual(currentSigner.SubjectName.RawData))
            throw new InvalidDataException(
                $"Downloaded installer publisher '{downloadedSigner.Subject}' does not match the running DevBox publisher '{currentSigner.Subject}'.");
    }

    private static X509Certificate2 GetSignerCertificate(string path)
    {
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return new X509Certificate2(certificate);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException($"Unable to read the Authenticode signer certificate from '{Path.GetFileName(path)}'.", ex);
        }
    }

    private static void VerifyWinTrust(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Signed file was not found for Authenticode verification.", path);

        var filePathPtr = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePathPtr
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);
        var trustData = new WinTrustData
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
            UiChoice = 2,
            RevocationChecks = 1,
            UnionChoice = 1,
            FileInfo = fileInfoPtr,
            StateAction = 1,
            ProviderFlags = 0
        };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            var status = WinVerifyTrust(IntPtr.Zero, action, ref trustData);
            if (status != 0)
                throw new InvalidDataException($"DevBox file does not have a valid trusted Authenticode signature (0x{status:X8}).");
        }
        finally
        {
            trustData.StateAction = 2;
            _ = WinVerifyTrust(IntPtr.Zero, action, ref trustData);
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeCoTaskMem(filePathPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo''')

# 10. Runtime copies honor cancellation.
replace_once(
    "src/DevBox.Core/Services/RuntimeManager.cs",
    '''            CopyDirectory(sourcePath, stagingPath);''',
    '''            CopyDirectory(sourcePath, stagingPath, cancellationToken);''')
replace_once(
    "src/DevBox.Core/Services/RuntimeManager.cs",
    '''        CopyDirectory(sourcePath, stagingPath);''',
    '''        CopyDirectory(sourcePath, stagingPath, cancellationToken);''')
replace_once(
    "src/DevBox.Core/Services/RuntimeManager.cs",
    '''    private static void CopyDirectory(string sourcePath, string destinationPath)\n    {\n        Directory.CreateDirectory(destinationPath);\n        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))\n        {\n            var relative = Path.GetRelativePath(sourcePath, directory);\n            Directory.CreateDirectory(Path.Combine(destinationPath, relative));\n        }\n\n        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))\n        {\n            var relative = Path.GetRelativePath(sourcePath, file);\n            var target = Path.Combine(destinationPath, relative);\n            Directory.CreateDirectory(Path.GetDirectoryName(target)!);\n            File.Copy(file, target, overwrite: true);\n        }\n    }''',
    '''    private static void CopyDirectory(string sourcePath, string destinationPath, CancellationToken cancellationToken)\n    {\n        cancellationToken.ThrowIfCancellationRequested();\n        Directory.CreateDirectory(destinationPath);\n        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))\n        {\n            cancellationToken.ThrowIfCancellationRequested();\n            var relative = Path.GetRelativePath(sourcePath, directory);\n            Directory.CreateDirectory(Path.Combine(destinationPath, relative));\n        }\n\n        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))\n        {\n            cancellationToken.ThrowIfCancellationRequested();\n            var relative = Path.GetRelativePath(sourcePath, file);\n            var target = Path.Combine(destinationPath, relative);\n            Directory.CreateDirectory(Path.GetDirectoryName(target)!);\n            File.Copy(file, target, overwrite: true);\n        }\n    }''')

replace_once(
    "src/DevBox.Core/Services/RuntimePlatformService.cs",
    '''            CopyDirectory(source, staging);''',
    '''            CopyDirectory(source, staging, cancellationToken);''')
replace_once(
    "src/DevBox.Core/Services/RuntimePlatformService.cs",
    '''    private static void CopyDirectory(string source, string destination)\n    {\n        Directory.CreateDirectory(destination);\n        foreach (var file in Directory.GetFiles(source))\n        {\n            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)\n                throw new InvalidDataException("Runtime package contains a reparse point.");\n            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);\n        }\n        foreach (var directory in Directory.GetDirectories(source))\n        {\n            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)\n                throw new InvalidDataException("Runtime package contains a reparse point.");\n            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));\n        }\n    }''',
    '''    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)\n    {\n        cancellationToken.ThrowIfCancellationRequested();\n        Directory.CreateDirectory(destination);\n        foreach (var file in Directory.GetFiles(source))\n        {\n            cancellationToken.ThrowIfCancellationRequested();\n            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)\n                throw new InvalidDataException("Runtime package contains a reparse point.");\n            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);\n        }\n        foreach (var directory in Directory.GetDirectories(source))\n        {\n            cancellationToken.ThrowIfCancellationRequested();\n            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)\n                throw new InvalidDataException("Runtime package contains a reparse point.");\n            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken);\n        }\n    }''')

# 11. Regression tests.
write("tests/DevBox.Tests/PostMergeAuditRound4Tests.cs", r'''using System.Net;
using System.Net.Sockets;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class PostMergeAuditRound4Tests
{
    [Fact]
    public void PhpMyAdminConfig_RejectsStaleDatabasePort()
    {
        const string config = "<?php\n$cfg['blowfish_secret'] = 'x';\n$cfg['Servers'][$i]['auth_type'] = 'cookie';\n$cfg['Servers'][$i]['host'] = '127.0.0.1';\n$cfg['Servers'][$i]['port'] = '3306';\n$cfg['Servers'][$i]['AllowNoPassword'] = true;\n$cfg['TempDir'] = 'tmp';\n";
        Assert.True(AddonInstaller.IsPhpMyAdminConfigUsable(config, 3306));
        Assert.False(AddonInstaller.IsPhpMyAdminConfigUsable(config, 3316));
    }

    [Fact]
    public void ManagedServiceCatalog_RejectsHttpsAndDatabasePorts()
    {
        var root = NewRoot();
        try
        {
            var catalog = new ManagedServiceCatalog(root);
            foreach (var port in new[] { 443, 3316, 5432 })
            {
                var manifest = new ManagedServiceManifest(
                    ManagedServiceManifest.CurrentSchemaVersion,
                    $"custom-{port}",
                    "Custom",
                    "runtime/custom/tool.exe",
                    Array.Empty<string>(),
                    ".",
                    port,
                    "1");
                Assert.Throws<InvalidDataException>(() => catalog.Upsert(manifest));
            }
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void DatabaseRuntime_RegisterRejectsExplicitPortUsedByAnotherProcess()
    {
        var root = NewRoot();
        TcpListener? listener = null;
        try
        {
            var executable = Path.Combine(root, "runtime", "mysql", "8.4.11", "bin", "mysqld.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "fixture");

            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            using var databases = new DatabaseRuntimeService(root);
            Assert.Throws<InvalidOperationException>(() => databases.Register("mysql", "8.4.11", port));
        }
        finally
        {
            listener?.Stop();
            TryDelete(root);
        }
    }

    [Fact]
    public void EnvironmentProfiles_ConcurrentWritersDoNotLoseUpdates()
    {
        var root = NewRoot();
        try
        {
            var first = Profile("round4-first");
            var second = Profile("round4-second");
            Parallel.Invoke(
                () => new EnvironmentProfileService(root).SaveCustomProfile(first),
                () => new EnvironmentProfileService(root).SaveCustomProfile(second));

            var keys = new EnvironmentProfileService(root).GetProfiles().Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains(first.Key, keys);
            Assert.Contains(second.Key, keys);
        }
        finally { TryDelete(root); }
    }

    private static EnvironmentProfile Profile(string key) => new()
    {
        Key = key,
        DisplayName = key,
        Kind = ProjectKind.EmptyPhp,
        Database = new EnvironmentDatabasePin("none", null, null),
        Description = "round 4 regression fixture"
    };

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
''')

# 12. Changelog.
replace_once(
    "CHANGELOG.md",
    '''### Fixed\n\n''',
    '''### Fixed\n\n- Local TLS and Local CA mutations now avoid re-entrant lock deadlocks, serialize CA lifecycle operations, and roll back failed CA creation/rotation transactionally.\n- ADDON install/repair/uninstall operations and mutable environment/runtime/service catalogs are serialized across GUI and CLI processes.\n- phpMyAdmin Repair now refreshes a stale MySQL/MariaDB port instead of accepting an otherwise complete obsolete configuration.\n- Project provisioning, WordPress setup and Git bootstrap restore pre-existing TLS material and trust state when a later setup stage fails.\n- Explicit database ports are rejected when another process already owns the listener, and database autodiscovery no longer persists registrations outside the registration lock.\n- Managed services reject HTTPS, standard database and currently registered database ports reserved by DevBox core services.\n- Cancelled project database client operations terminate their native child process instead of leaving it running in the background.\n- Self-update verifies that a trusted installer is signed by the same publisher identity as the currently running signed DevBox executable and enables certificate revocation checks.\n- Project/Site path validation rejects junctions and other reparse points that could escape the DevBox `www` tree.\n- Runtime activation/import copy loops now observe cancellation between files and directories.\n\n''')

print("Audit round 4 patch applied successfully.")
