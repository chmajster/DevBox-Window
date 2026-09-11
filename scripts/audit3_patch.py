from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding='utf-8')


def write(path, text):
    (ROOT / path).write_text(text, encoding='utf-8', newline='')


def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{path}: expected exactly one literal match, got {count}')
    write(path, text.replace(old, new, 1))


def regex_once(path, pattern, replacement, flags=re.S):
    text = read(path)
    updated, count = re.subn(pattern, replacement, text, count=1, flags=flags)
    if count != 1:
        raise RuntimeError(f'{path}: expected exactly one regex match for {pattern!r}, got {count}')
    write(path, updated)


# 1. Do not commit a new environment lock when prerequisites failed.
replace_once(
    'src/DevBox.Core/Services/EnvironmentLockService.cs',
    '''        var result = await ApplyDesiredStateAsync(root, desired, cancellationToken).ConfigureAwait(false);\n        AtomicWrite(Path.Combine(root, LockFileName), JsonSerializer.Serialize(desired, JsonOptions));\n        return result;''',
    '''        var result = await ApplyDesiredStateAsync(root, desired, cancellationToken).ConfigureAwait(false);\n        if (result.Warnings.Count == 0)\n            AtomicWrite(Path.Combine(root, LockFileName), JsonSerializer.Serialize(desired, JsonOptions));\n        return result;''')

# 2. Windows on ARM64 can run x64 runtimes under emulation. Prefer native packages, then any, then x64 fallback.
regex_once(
    'src/DevBox.Core/Services/RuntimePlatformService.cs',
    r'''    public RuntimePackageEntry GetPackage\(string key, string version\)\n    \{.*?\n    \}\n\n    public IReadOnlyList<RuntimeVersionStatus> GetStatuses''',
    '''    public RuntimePackageEntry GetPackage(string key, string version)\n    {\n        ArgumentException.ThrowIfNullOrWhiteSpace(key);\n        ArgumentException.ThrowIfNullOrWhiteSpace(version);\n        var architecture = RuntimeInformation.ProcessArchitecture;\n        var architectureName = CurrentArchitecture();\n        return GetCatalog()\n                   .Where(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&\n                                  item.Version.Equals(version, StringComparison.OrdinalIgnoreCase) &&\n                                  IsPackageArchitectureCompatible(item.Architecture, architecture))\n                   .OrderByDescending(item => item.Architecture.Equals(architectureName, StringComparison.OrdinalIgnoreCase))\n                   .ThenByDescending(item => item.Architecture.Equals("any", StringComparison.OrdinalIgnoreCase))\n                   .FirstOrDefault()\n               ?? throw new KeyNotFoundException($"Runtime package '{key}' version '{version}' for {architectureName} was not found in the catalog.");\n    }\n\n    public IReadOnlyList<RuntimeVersionStatus> GetStatuses''')
replace_once(
    'src/DevBox.Core/Services/RuntimePlatformService.cs',
    '''            .Where(item => item.Architecture.Equals("any", StringComparison.OrdinalIgnoreCase) || item.Architecture.Equals(CurrentArchitecture(), StringComparison.OrdinalIgnoreCase))''',
    '''            .Where(item => IsPackageArchitectureCompatible(item.Architecture, RuntimeInformation.ProcessArchitecture))''')
replace_once(
    'src/DevBox.Core/Services/RuntimePlatformService.cs',
    '''    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch''',
    '''    internal static bool IsPackageArchitectureCompatible(string packageArchitecture, Architecture architecture)\n    {\n        if (packageArchitecture.Equals("any", StringComparison.OrdinalIgnoreCase))\n            return true;\n        var current = architecture switch\n        {\n            Architecture.X64 => "x64",\n            Architecture.Arm64 => "arm64",\n            Architecture.X86 => "x86",\n            _ => architecture.ToString().ToLowerInvariant()\n        };\n        if (packageArchitecture.Equals(current, StringComparison.OrdinalIgnoreCase))\n            return true;\n        return OperatingSystem.IsWindows() && architecture == Architecture.Arm64 &&\n               packageArchitecture.Equals("x64", StringComparison.OrdinalIgnoreCase);\n    }\n\n    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch''')

# 3. Serialize all Site mutations across GUI/CLI processes.
site_path = 'src/DevBox.Core/Services/SiteManager.cs'
for old, new in [
    ('''    public SiteDefinition Create(string name, string? domain = null, string? documentRoot = null)\n    {\n        var normalizedName = NormalizeName(name);''',
     '''    public SiteDefinition Create(string name, string? domain = null, string? documentRoot = null)\n    {\n        using var mutationLock = AcquireMutationLock();\n        var normalizedName = NormalizeName(name);'''),
    ('''    public SiteDefinition RegisterExisting(string name, string domain, string documentRoot)\n    {\n        var normalizedName = NormalizeName(name);''',
     '''    public SiteDefinition RegisterExisting(string name, string domain, string documentRoot)\n    {\n        using var mutationLock = AcquireMutationLock();\n        var normalizedName = NormalizeName(name);'''),
    ('''    public SiteDefinition Update(SiteDefinition site)\n    {\n        ArgumentNullException.ThrowIfNull(site);''',
     '''    public SiteDefinition Update(SiteDefinition site)\n    {\n        ArgumentNullException.ThrowIfNull(site);\n        using var mutationLock = AcquireMutationLock();'''),
    ('''    public SiteDefinition SetHttps(string name, bool enabled)\n    {\n        var normalizedName = NormalizeName(name);''',
     '''    public SiteDefinition SetHttps(string name, bool enabled)\n    {\n        using var mutationLock = AcquireMutationLock();\n        var normalizedName = NormalizeName(name);'''),
    ('''    public SiteDefinition SetPhpVersion(string name, string? version)\n    {\n        var normalizedName = NormalizeName(name);''',
     '''    public SiteDefinition SetPhpVersion(string name, string? version)\n    {\n        using var mutationLock = AcquireMutationLock();\n        var normalizedName = NormalizeName(name);'''),
    ('''    public void Delete(string name, bool deleteDocumentRoot = false)\n    {\n        var normalizedName = NormalizeName(name);''',
     '''    public void Delete(string name, bool deleteDocumentRoot = false)\n    {\n        using var mutationLock = AcquireMutationLock();\n        var normalizedName = NormalizeName(name);''')
]:
    replace_once(site_path, old, new)
replace_once(
    site_path,
    '''    private void SaveSites(IReadOnlyCollection<SiteDefinition> sites)''',
    '''    private FileStream AcquireMutationLock() =>\n        CrossProcessFileLock.Acquire(_sitesMetadataPath + ".lock", TimeSpan.FromSeconds(15));\n\n    private void SaveSites(IReadOnlyCollection<SiteDefinition> sites)''')

# 4. Serialize per-domain TLS mutations, including Local CA issuance.
cert_path = 'src/DevBox.Core/Services/LocalCertificateManager.cs'
replace_once(cert_path, '    private readonly string _certificateRoot;\n', '    private readonly string _rootPath;\n    private readonly string _certificateRoot;\n')
replace_once(cert_path, '''        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);\n        _certificateRoot = Path.GetFullPath(Path.Combine(rootPath, "config", "ssl", "sites"));''',
                       '''        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);\n        _rootPath = Path.GetFullPath(rootPath);\n        _certificateRoot = Path.Combine(_rootPath, "config", "ssl", "sites");''')
regex_once(
    cert_path,
    r'''    public LocalCertificate Ensure\(string domain\)\n    \{(.*?)\n    \}\n\n    public bool IsMaterialValid''',
    '''    public LocalCertificate Ensure(string domain)\n    {\n        var normalizedDomain = NormalizeDomain(domain);\n        using var domainLock = CrossProcessFileLock.Acquire(GetDomainLockPath(_rootPath, normalizedDomain));\n        return EnsureCore(normalizedDomain);\n    }\n\n    private LocalCertificate EnsureCore(string normalizedDomain)\n    {\1\n    }\n\n    public bool IsMaterialValid''')
# Remove duplicate normalization introduced inside the moved body.
replace_once(cert_path, '''    private LocalCertificate EnsureCore(string normalizedDomain)\n    {\n        var normalizedDomain = NormalizeDomain(domain);''',
                       '''    private LocalCertificate EnsureCore(string normalizedDomain)\n    {''')
regex_once(
    cert_path,
    r'''    public void TrustForCurrentUser\(string domain\)\n    \{.*?\n    \}\n\n    public void UntrustForCurrentUser\(string domain\)\n    \{.*?\n    \}\n\n    public void Delete\(string domain\)\n    \{(.*?)\n    \}\n\n    private static void RemoveTrustedThumbprint''',
    '''    public void TrustForCurrentUser(string domain)\n    {\n        var normalizedDomain = NormalizeDomain(domain);\n        using var domainLock = CrossProcessFileLock.Acquire(GetDomainLockPath(_rootPath, normalizedDomain));\n        var certificate = EnsureCore(normalizedDomain);\n        using var publicCertificate = LoadPublicCertificate(certificate.CertificatePath);\n        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);\n        store.Open(OpenFlags.ReadWrite);\n        if (store.Certificates.Find(X509FindType.FindByThumbprint, publicCertificate.Thumbprint, validOnly: false).Count == 0)\n            store.Add(publicCertificate);\n    }\n\n    public void UntrustForCurrentUser(string domain)\n    {\n        var normalizedDomain = NormalizeDomain(domain);\n        using var domainLock = CrossProcessFileLock.Acquire(GetDomainLockPath(_rootPath, normalizedDomain));\n        UntrustForCurrentUserUnlocked(normalizedDomain);\n    }\n\n    internal void UntrustForCurrentUserUnlocked(string normalizedDomain)\n    {\n        var certificatePath = CertificatePath(normalizedDomain);\n        if (!File.Exists(certificatePath))\n            return;\n        using var certificate = LoadPublicCertificate(certificatePath);\n        RemoveTrustedThumbprint(certificate.Thumbprint);\n    }\n\n    public void Delete(string domain)\n    {\n        var normalizedDomain = NormalizeDomain(domain);\n        using var domainLock = CrossProcessFileLock.Acquire(GetDomainLockPath(_rootPath, normalizedDomain));\n\1\n    }\n\n    private static void RemoveTrustedThumbprint''')
# Remove duplicate normalization inside Delete body.
replace_once(cert_path, '''    public void Delete(string domain)\n    {\n        var normalizedDomain = NormalizeDomain(domain);\n        using var domainLock = CrossProcessFileLock.Acquire(GetDomainLockPath(_rootPath, normalizedDomain));\n        var normalizedDomain = NormalizeDomain(domain);''',
                       '''    public void Delete(string domain)\n    {\n        var normalizedDomain = NormalizeDomain(domain);\n        using var domainLock = CrossProcessFileLock.Acquire(GetDomainLockPath(_rootPath, normalizedDomain));''')
replace_once(cert_path, '''    private string CertificatePath(string domain) => Path.Combine(_certificateRoot, $"{domain}.crt.pem");''',
                       '''    internal static string GetDomainLockPath(string rootPath, string normalizedDomain) =>\n        Path.Combine(Path.GetFullPath(rootPath), "tmp", "locks", $"tls-{normalizedDomain}.lock");\n\n    private string CertificatePath(string domain) => Path.Combine(_certificateRoot, $"{domain}.crt.pem");''')

ca_path = 'src/DevBox.Core/Services/LocalCertificateAuthorityService.cs'
regex_once(
    ca_path,
    r'''    public X509Certificate2 IssueSiteCertificate\(string domain, bool trustAuthority = true\)\n    \{.*?\n    \}\n\n    public bool IsTrustedCurrentUser''',
    '''    public X509Certificate2 IssueSiteCertificate(string domain, bool trustAuthority = true)\n    {\n        EnsureWindows();\n        var normalizedDomain = LocalCertificateManager.NormalizeDomain(domain);\n        var rollbackService = new TlsRollbackStateService(_rootPath);\n        TlsRollbackState? rollbackState = null;\n        try\n        {\n            using var domainLock = CrossProcessFileLock.Acquire(LocalCertificateManager.GetDomainLockPath(_rootPath, normalizedDomain));\n            rollbackState = rollbackService.Capture(normalizedDomain);\n            using var authority = EnsureAuthority(trustAuthority);\n            using var key = RSA.Create(2048);\n            var request = new CertificateRequest(\n                new X500DistinguishedName($"CN={normalizedDomain}"),\n                key,\n                HashAlgorithmName.SHA256,\n                RSASignaturePadding.Pkcs1);\n            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));\n            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));\n            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));\n            var san = new SubjectAlternativeNameBuilder();\n            san.AddDnsName(normalizedDomain);\n            request.CertificateExtensions.Add(san.Build());\n            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));\n\n            var serial = RandomNumberGenerator.GetBytes(16);\n            serial[0] &= 0x7f;\n            if (serial.All(value => value == 0))\n                serial[^1] = 1;\n            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);\n            var notAfter = DateTimeOffset.UtcNow.AddDays(397);\n            using var signed = request.Create(authority, notBefore, notAfter, serial);\n            using var certificate = signed.CopyWithPrivateKey(key);\n\n            var sitesDirectory = Path.Combine(_rootPath, "config", "ssl", "sites");\n            Directory.CreateDirectory(sitesDirectory);\n            var certPath = Path.Combine(sitesDirectory, $"{normalizedDomain}.crt.pem");\n            var keyPath = Path.Combine(sitesDirectory, $"{normalizedDomain}.key.pem");\n\n            if (File.Exists(certPath))\n            {\n                try\n                {\n                    new LocalCertificateManager(_rootPath).UntrustForCurrentUserUnlocked(normalizedDomain);\n                }\n                catch (CryptographicException)\n                {\n                }\n            }\n\n            AtomicWrite(certPath, certificate.ExportCertificatePem() + authority.ExportCertificatePem());\n            AtomicWrite(keyPath, key.ExportPkcs8PrivateKeyPem());\n            return new X509Certificate2(certificate.Export(X509ContentType.Cert));\n        }\n        catch\n        {\n            if (rollbackState is not null)\n                rollbackService.Restore(rollbackState);\n            throw;\n        }\n    }\n\n    public bool IsTrustedCurrentUser''')

# 5. Serialize runtime install/activate/remove operations across processes.
rm_path = 'src/DevBox.Core/Services/RuntimeManager.cs'
regex_once(
    rm_path,
    r'''    public async Task InstallAsync\(RuntimeDefinition definition, CancellationToken cancellationToken = default\)\n    \{(.*?)\n    \}\n\n    public Task ActivateAsync\(string runtimeKey, string version, string executableRelativePath, CancellationToken cancellationToken = default\)\n    \{(.*?)\n    \}\n\n    public Task RemoveAsync\(string runtimeKey, string version, CancellationToken cancellationToken = default\)\n    \{(.*?)\n    \}\n''',
    '''    public async Task InstallAsync(RuntimeDefinition definition, CancellationToken cancellationToken = default)\n    {\n        ThrowIfDisposed();\n        ArgumentNullException.ThrowIfNull(definition);\n        ValidateDefinition(definition);\n        using var runtimeLock = await AcquireRuntimeLockAsync(definition.Key, cancellationToken).ConfigureAwait(false);\n        await InstallUnderLockAsync(definition, cancellationToken).ConfigureAwait(false);\n    }\n\n    private async Task InstallUnderLockAsync(RuntimeDefinition definition, CancellationToken cancellationToken)\n    {\1\n    }\n\n    public async Task ActivateAsync(string runtimeKey, string version, string executableRelativePath, CancellationToken cancellationToken = default)\n    {\n        ThrowIfDisposed();\n        using var runtimeLock = await AcquireRuntimeLockAsync(runtimeKey, cancellationToken).ConfigureAwait(false);\n        await ActivateUnderLockAsync(runtimeKey, version, executableRelativePath, cancellationToken).ConfigureAwait(false);\n    }\n\n    internal Task ActivateUnderLockAsync(string runtimeKey, string version, string executableRelativePath, CancellationToken cancellationToken = default)\n    {\2\n    }\n\n    public async Task RemoveAsync(string runtimeKey, string version, CancellationToken cancellationToken = default)\n    {\n        ThrowIfDisposed();\n        using var runtimeLock = await AcquireRuntimeLockAsync(runtimeKey, cancellationToken).ConfigureAwait(false);\n        await RemoveUnderLockAsync(runtimeKey, version, cancellationToken).ConfigureAwait(false);\n    }\n\n    private Task RemoveUnderLockAsync(string runtimeKey, string version, CancellationToken cancellationToken)\n    {\3\n    }\n''')
# Strip duplicated guards from moved Install body.
replace_once(rm_path, '''    private async Task InstallUnderLockAsync(RuntimeDefinition definition, CancellationToken cancellationToken)\n    {\n        ThrowIfDisposed();\n        ArgumentNullException.ThrowIfNull(definition);\n        ValidateDefinition(definition);''',
                      '''    private async Task InstallUnderLockAsync(RuntimeDefinition definition, CancellationToken cancellationToken)\n    {''')
# Public Install must not re-enter lock through Activate.
replace_once(rm_path, '''            await ActivateAsync(\n                definition.Key,''', '''            await ActivateUnderLockAsync(\n                definition.Key,''')
replace_once(rm_path, '''            await ActivateAsync(definition.Key, definition.Version, definition.ExecutableRelativePath, cancellationToken).ConfigureAwait(false);''',
                      '''            await ActivateUnderLockAsync(definition.Key, definition.Version, definition.ExecutableRelativePath, cancellationToken).ConfigureAwait(false);''')
replace_once(rm_path, '''    private string RuntimeRoot(string runtimeKey) => Path.Combine(_rootPath, "runtime", runtimeKey);''',
                      '''    internal Task<FileStream> AcquireRuntimeLockAsync(string runtimeKey, CancellationToken cancellationToken)\n    {\n        ValidateSegment(runtimeKey, nameof(runtimeKey));\n        return CrossProcessFileLock.AcquireAsync(\n            Path.Combine(_rootPath, "tmp", "locks", $"runtime-{runtimeKey.ToLowerInvariant()}.lock"),\n            cancellationToken,\n            TimeSpan.FromSeconds(30));\n    }\n\n    private string RuntimeRoot(string runtimeKey) => Path.Combine(_rootPath, "runtime", runtimeKey);''')

# Lock local archive import through the same runtime lock and activate without re-entering it.
rp_path = 'src/DevBox.Core/Services/RuntimePlatformService.cs'
replace_once(rp_path, '''            var installRoot = Path.Combine(_rootPath, "runtime", package.Key);\n            var installPath = Path.Combine(installRoot, package.Version);\n            Directory.CreateDirectory(installRoot);\n            if (Directory.Exists(installPath))\n                throw new InvalidOperationException($"Runtime {package.Key} {package.Version} is already installed.");\n            Directory.Move(staging, installPath);\n\n            if (activate)\n                await _runtimeManager.ActivateAsync(package.Key, package.Version, package.ExecutableRelativePath, cancellationToken).ConfigureAwait(false);''',
                      '''            using var runtimeLock = await _runtimeManager.AcquireRuntimeLockAsync(package.Key, cancellationToken).ConfigureAwait(false);\n            var installRoot = Path.Combine(_rootPath, "runtime", package.Key);\n            var installPath = Path.Combine(installRoot, package.Version);\n            Directory.CreateDirectory(installRoot);\n            if (Directory.Exists(installPath))\n                throw new InvalidOperationException($"Runtime {package.Key} {package.Version} is already installed.");\n            Directory.Move(staging, installPath);\n\n            if (activate)\n                await _runtimeManager.ActivateUnderLockAsync(package.Key, package.Version, package.ExecutableRelativePath, cancellationToken).ConfigureAwait(false);''')

# 6. phpMyAdmin follows the registered/running MySQL or MariaDB port.
addon_path = 'src/DevBox.Core/Services/AddonInstaller.cs'
replace_once(addon_path, '''        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();\n        var config = $$"""''',
                       '''        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();\n        var databasePort = ResolvePhpMyAdminPort();\n        var config = $$"""''')
replace_once(addon_path, '''$cfg['Servers'][$i]['port'] = '3306';''', '''$cfg['Servers'][$i]['port'] = '{{databasePort}}';''')
replace_once(addon_path, '''    private void WriteAddonNginxConfig(AddonDefinition addon)''',
                       '''    private int ResolvePhpMyAdminPort()\n    {\n        using var databases = new DatabaseRuntimeService(_rootPath);\n        return SelectPhpMyAdminPort(databases.GetInstances());\n    }\n\n    internal static int SelectPhpMyAdminPort(IEnumerable<DatabaseRuntimeInstance> instances)\n    {\n        var selected = instances\n            .Where(item => item.Engine is DatabaseEngineKind.MySql or DatabaseEngineKind.MariaDb)\n            .OrderByDescending(item => item.State == ServiceState.Running)\n            .ThenBy(item => item.Engine == DatabaseEngineKind.MySql ? 0 : 1)\n            .ThenBy(item => item.Port)\n            .FirstOrDefault();\n        return selected?.Port ?? 3306;\n    }\n\n    private void WriteAddonNginxConfig(AddonDefinition addon)''')

# 7. Add database existence/drop primitives so rollback only removes databases created by the current operation.
pdp = 'src/DevBox.Core/Services/ProjectDatabaseProvisioner.cs'
insert = '''    public async Task<bool> DatabaseExistsAsync(\n        string engine,\n        string databaseName,\n        DatabaseConnectionOptions? options = null,\n        CancellationToken cancellationToken = default)\n    {\n        var safeName = DatabaseManager.ValidateDatabaseName(databaseName);\n        switch (engine.ToLowerInvariant())\n        {\n            case "mysql":\n            {\n                var effective = options ?? new DatabaseConnectionOptions();\n                var values = await _mysql.ListDatabasesAsync(effective, cancellationToken).ConfigureAwait(false);\n                return values.Contains(safeName, StringComparer.OrdinalIgnoreCase);\n            }\n            case "mariadb":\n            {\n                var effective = options ?? new DatabaseConnectionOptions(Port: 3316);\n                effective.Validate();\n                var client = ResolveMariaDbClient() ?? throw new FileNotFoundException("MariaDB client runtime is not installed under runtime/mariadb/current/bin.");\n                var output = await RunAsync(client, MariaDbArguments(effective, "--batch", "--skip-column-names", "--execute=SHOW DATABASES;"), MySqlPasswordEnvironment(effective), cancellationToken).ConfigureAwait(false);\n                return output.Split(['\\r', '\\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)\n                    .Contains(safeName, StringComparer.OrdinalIgnoreCase);\n            }\n            case "postgresql":\n            {\n                var effective = options ?? new DatabaseConnectionOptions(Port: 5432, User: "postgres");\n                effective.Validate();\n                var psql = PostgresTool("psql.exe");\n                EnsureFile(psql, "PostgreSQL psql client is not installed under runtime/postgresql/current/bin.");\n                var output = await RunAsync(\n                    psql,\n                    ["--host", effective.Host, "--port", effective.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--username", effective.User, "--dbname", "postgres", "--tuples-only", "--no-align", "--command", $"SELECT 1 FROM pg_database WHERE datname = '{safeName}';"],\n                    PgPasswordEnvironment(effective),\n                    cancellationToken).ConfigureAwait(false);\n                return output.Split(['\\r', '\\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("1", StringComparer.Ordinal);\n            }\n            case "none":\n                return true;\n            default:\n                throw new NotSupportedException($"Database engine '{engine}' is not supported by project provisioning.");\n        }\n    }\n\n    public async Task DropDatabaseAsync(\n        string engine,\n        string databaseName,\n        DatabaseConnectionOptions? options = null,\n        CancellationToken cancellationToken = default)\n    {\n        var safeName = DatabaseManager.ValidateDatabaseName(databaseName);\n        switch (engine.ToLowerInvariant())\n        {\n            case "mysql":\n                await _mysql.DropDatabaseAsync(safeName, options ?? new DatabaseConnectionOptions(), cancellationToken).ConfigureAwait(false);\n                return;\n            case "mariadb":\n            {\n                var effective = options ?? new DatabaseConnectionOptions(Port: 3316);\n                effective.Validate();\n                var client = ResolveMariaDbClient() ?? throw new FileNotFoundException("MariaDB client runtime is not installed under runtime/mariadb/current/bin.");\n                _ = await RunAsync(client, MariaDbArguments(effective, $"--execute=DROP DATABASE IF EXISTS `{safeName}`;"), MySqlPasswordEnvironment(effective), cancellationToken).ConfigureAwait(false);\n                return;\n            }\n            case "postgresql":\n            {\n                var effective = options ?? new DatabaseConnectionOptions(Port: 5432, User: "postgres");\n                effective.Validate();\n                var dropdb = PostgresTool("dropdb.exe");\n                EnsureFile(dropdb, "PostgreSQL dropdb client is not installed under runtime/postgresql/current/bin.");\n                _ = await RunAsync(dropdb, ["--host", effective.Host, "--port", effective.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--username", effective.User, "--if-exists", safeName], PgPasswordEnvironment(effective), cancellationToken).ConfigureAwait(false);\n                return;\n            }\n            case "none":\n                return;\n            default:\n                throw new NotSupportedException($"Database engine '{engine}' is not supported by project provisioning.");\n        }\n    }\n\n'''
replace_once(pdp, '    public async Task EnsureDatabaseAsync(\n', insert + '    public async Task EnsureDatabaseAsync(\n')
replace_once(pdp, '''                await EnsureMariaDbAsync(safeName, options ?? new DatabaseConnectionOptions(), cancellationToken).ConfigureAwait(false);''',
                  '''                await EnsureMariaDbAsync(safeName, options ?? new DatabaseConnectionOptions(Port: 3316), cancellationToken).ConfigureAwait(false);''')
regex_once(
    pdp,
    r'''    private async Task EnsureMariaDbAsync\(string databaseName, DatabaseConnectionOptions options, CancellationToken cancellationToken\)\n    \{.*?\n    \}\n\n    private async Task EnsurePostgreSqlAsync''',
    '''    private async Task EnsureMariaDbAsync(string databaseName, DatabaseConnectionOptions options, CancellationToken cancellationToken)\n    {\n        options.Validate();\n        var client = ResolveMariaDbClient()\n            ?? throw new FileNotFoundException("MariaDB client runtime is not installed under runtime/mariadb/current/bin.");\n        _ = await RunAsync(\n            client,\n            MariaDbArguments(options, $"--execute=CREATE DATABASE IF NOT EXISTS `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"),\n            MySqlPasswordEnvironment(options),\n            cancellationToken).ConfigureAwait(false);\n    }\n\n    private async Task EnsurePostgreSqlAsync''')
replace_once(pdp, '''            var environment = new Dictionary<string, string?> { ["PGPASSFILE"] = pgPassPath };''',
                  '''            var environment = PgPasswordEnvironment(options);''')
# Replace entire pgpass wrapper in PostgreSQL ensure with direct execution.
regex_once(
    pdp,
    r'''        await WithPgPassAsync\(options, async pgPassPath =>\n        \{\n            var environment = PgPasswordEnvironment\(options\);(.*?)\n        \}\)\.ConfigureAwait\(false\);''',
    '''        var environment = PgPasswordEnvironment(options);\1''')
# Remove legacy temp credential helpers and add environment-based helpers.
regex_once(
    pdp,
    r'''    private async Task WithMariaDbConfigAsync\(.*?\n    private static void EnsureFile''',
    '''    private static IReadOnlyList<string> MariaDbArguments(DatabaseConnectionOptions options, params string[] commandArguments)\n    {\n        var result = new List<string>\n        {\n            $"--host={options.Host}",\n            $"--port={options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}",\n            $"--user={options.User}"\n        };\n        result.AddRange(commandArguments);\n        return result;\n    }\n\n    private static IReadOnlyDictionary<string, string?>? MySqlPasswordEnvironment(DatabaseConnectionOptions options) =>\n        string.IsNullOrEmpty(options.Password) ? null : new Dictionary<string, string?> { ["MYSQL_PWD"] = options.Password };\n\n    private static IReadOnlyDictionary<string, string?>? PgPasswordEnvironment(DatabaseConnectionOptions options) =>\n        string.IsNullOrEmpty(options.Password) ? null : new Dictionary<string, string?> { ["PGPASSWORD"] = options.Password };\n\n    private static void EnsureFile''')

# 8. Provisioning tracks whether it created the database and rolls it back on subsequent failure.
replace_once('src/DevBox.Core/Models/ProjectProvisioningResult.cs',
             '''    IReadOnlyList<string> Actions,\n    IReadOnlyList<string> Warnings);''',
             '''    IReadOnlyList<string> Actions,\n    IReadOnlyList<string> Warnings,\n    bool DatabaseCreated = false);''')
pps = 'src/DevBox.Core/Services/ProjectProvisioningService.cs'
replace_once(pps, '''        var actions = new List<string>();\n        var warnings = new List<string>();''',
                  '''        var actions = new List<string>();\n        var warnings = new List<string>();\n        var databaseCreated = false;\n        string? createdDatabaseEngine = null;\n        string? createdDatabaseName = null;''')
replace_once(pps, '''                    await _projectDatabases.EnsureDatabaseAsync(\n                        manifest.DatabaseEngine,\n                        manifest.DatabaseName,\n                        databaseOptions,\n                        cancellationToken).ConfigureAwait(false);\n                    actions.Add($"Ensured {DisplayEngine(manifest.DatabaseEngine)} database {manifest.DatabaseName}.");''',
                  '''                    var existedBefore = await _projectDatabases.DatabaseExistsAsync(\n                        manifest.DatabaseEngine,\n                        manifest.DatabaseName,\n                        databaseOptions,\n                        cancellationToken).ConfigureAwait(false);\n                    await _projectDatabases.EnsureDatabaseAsync(\n                        manifest.DatabaseEngine,\n                        manifest.DatabaseName,\n                        databaseOptions,\n                        cancellationToken).ConfigureAwait(false);\n                    databaseCreated = !existedBefore;\n                    if (databaseCreated)\n                    {\n                        createdDatabaseEngine = manifest.DatabaseEngine;\n                        createdDatabaseName = manifest.DatabaseName;\n                    }\n                    actions.Add($"Ensured {DisplayEngine(manifest.DatabaseEngine)} database {manifest.DatabaseName}.");''')
replace_once(pps, '''            return new ProjectProvisioningResult(site, manifest, actions, warnings);''',
                  '''            return new ProjectProvisioningResult(site, manifest, actions, warnings, databaseCreated);''')
replace_once(pps, '''            RollbackExecutor.RethrowAfterRollback(\n                original,\n                () =>\n                {''',
                  '''            var rollbackActions = new List<Action>();\n            if (databaseCreated && createdDatabaseEngine is not null && createdDatabaseName is not null)\n            {\n                rollbackActions.Add(() => _projectDatabases.DropDatabaseAsync(\n                    createdDatabaseEngine,\n                    createdDatabaseName,\n                    databaseOptions,\n                    CancellationToken.None).GetAwaiter().GetResult());\n            }\n            rollbackActions.Add(() =>\n                {''')
replace_once(pps, '''                },\n                () => RollbackProjectDirectory(projectRoot, projectRootExisted));\n            throw new InvalidOperationException("Project provisioning rollback executor returned unexpectedly.");''',
                  '''                });\n            rollbackActions.Add(() => RollbackProjectDirectory(projectRoot, projectRootExisted));\n            RollbackExecutor.RethrowAfterRollback(original, rollbackActions.ToArray());\n            throw new InvalidOperationException("Project provisioning rollback executor returned unexpectedly.");''')

# 9. WordPress uses the actual registered DB port, removes a newly-created DB on later WP-CLI failure, and reports update-check errors.
wp = 'src/DevBox.Core/Services/WordPressToolkitService.cs'
replace_once(wp, '''        var databaseOptions = request.DatabaseOptions ?? DefaultDatabaseOptions(request.DatabaseEngine);\n        var provisioning = new ProjectProvisioningService(\n            _rootPath,\n            _workspace,\n            new DatabaseManager(_rootPath),\n            new ManagedServiceCatalog(_rootPath));''',
                '''        var databaseOptions = request.DatabaseOptions ?? ResolveDatabaseOptions(request.DatabaseEngine);\n        var databaseManager = new DatabaseManager(_rootPath);\n        var projectDatabases = new ProjectDatabaseProvisioner(_rootPath, databaseManager);\n        var provisioning = new ProjectProvisioningService(\n            _rootPath,\n            _workspace,\n            databaseManager,\n            new ManagedServiceCatalog(_rootPath));''')
replace_once(wp, '''        catch\n        {\n            try\n            {\n                _sites.Delete(result.Site.Name, deleteDocumentRoot: true);\n            }\n            catch (Exception) { }\n            throw;\n        }''',
                '''        catch\n        {\n            var cleanupErrors = new List<Exception>();\n            try\n            {\n                _sites.Delete(result.Site.Name, deleteDocumentRoot: true);\n            }\n            catch (Exception ex)\n            {\n                cleanupErrors.Add(ex);\n            }\n            if (result.DatabaseCreated)\n            {\n                try\n                {\n                    await projectDatabases.DropDatabaseAsync(request.DatabaseEngine, databaseName, databaseOptions, CancellationToken.None).ConfigureAwait(false);\n                }\n                catch (Exception ex)\n                {\n                    cleanupErrors.Add(ex);\n                }\n            }\n            if (cleanupErrors.Count > 0)\n                throw new AggregateException("WordPress setup failed and cleanup was incomplete.", cleanupErrors);\n            throw;\n        }''')
replace_once(wp, '''        var update = await RunWpCliAsync(php, projectRoot, ["core", "check-update", "--format=csv", "--no-color"], null, cancellationToken).ConfigureAwait(false);\n        if (update.ExitCode == 0 && !string.IsNullOrWhiteSpace(update.StandardOutput))\n            lines.Add("Core update is available.");\n        else\n            lines.Add("No core update reported by WP-CLI.");''',
                '''        var update = await RunWpCliAsync(php, projectRoot, ["core", "check-update", "--format=csv", "--no-color"], null, cancellationToken).ConfigureAwait(false);\n        EnsureSuccess(update, "WordPress update check");\n        if (!string.IsNullOrWhiteSpace(update.StandardOutput))\n            lines.Add("Core update is available.");\n        else\n            lines.Add("No core update reported by WP-CLI.");''')
regex_once(wp, r'''    private static DatabaseConnectionOptions DefaultDatabaseOptions\(string engine\) =>.*?;\n''',
           '''    private DatabaseConnectionOptions ResolveDatabaseOptions(string engine)\n    {\n        using var runtimes = new DatabaseRuntimeService(_rootPath);\n        var normalized = engine.Trim().ToLowerInvariant();\n        var candidate = runtimes.GetInstances(normalized)\n            .OrderByDescending(item => item.State == ServiceState.Running)\n            .ThenBy(item => item.Port)\n            .FirstOrDefault();\n        var fallbackPort = normalized == "mariadb" ? 3316 : 3306;\n        return new DatabaseConnectionOptions(Port: candidate?.Port ?? fallbackPort, User: "root", Password: string.Empty);\n    }\n''')

# 10. MySQL/MariaDB runtime clients no longer write plaintext temporary credential files.
dbr = 'src/DevBox.Core/Services/DatabaseRuntimeService.cs'
regex_once(dbr, r'''                var defaults = CreateMySqlDefaultsFile\(options\);\n                try\n                \{\n                    var result = await RunProcessAsync\(\n                        executable,\n                        \[\$"--defaults-extra-file=\{defaults\}", "--single-transaction", "--routines", "--events", "--triggers", databaseName\],\n                        runtime,\n                        null,\n                        temporaryDestination,\n                        cancellationToken\)\.ConfigureAwait\(false\);\n                    EnsureSuccess\(result, \$"\{DisplayEngine\(kind\)\} backup"\);\n                \}\n                finally\n                \{\n                    TryDeleteFile\(defaults\);\n                \}''',
           '''                var arguments = MySqlClientArguments(options, "--single-transaction", "--routines", "--events", "--triggers", databaseName);\n                var result = await RunProcessAsync(\n                    executable,\n                    arguments,\n                    runtime,\n                    MySqlPasswordEnvironment(options),\n                    temporaryDestination,\n                    cancellationToken).ConfigureAwait(false);\n                EnsureSuccess(result, $"{DisplayEngine(kind)} backup");''')
regex_once(dbr, r'''        var defaultsFile = CreateMySqlDefaultsFile\(options\);\n        try\n        \{\n            var createSql = \$"CREATE DATABASE IF NOT EXISTS `\{databaseName\}`";\n            var createResult = await RunProcessAsync\(client, \[\$"--defaults-extra-file=\{defaultsFile\}", \$"--execute=\{createSql\}"\], mysqlRuntime, null, null, cancellationToken\)\.ConfigureAwait\(false\);\n            EnsureSuccess\(createResult, \$"\{DisplayEngine\(kind\)\} target database creation"\);\n\n            var restoreResult = await RunProcessAsync\(client, \[\$"--defaults-extra-file=\{defaultsFile\}", databaseName\], mysqlRuntime, null, null, cancellationToken, source\)\.ConfigureAwait\(false\);\n            EnsureSuccess\(restoreResult, \$"\{DisplayEngine\(kind\)\} restore"\);\n        \}\n        finally\n        \{\n            TryDeleteFile\(defaultsFile\);\n        \}''',
           '''        var createSql = $"CREATE DATABASE IF NOT EXISTS `{databaseName}`";\n        var environment = MySqlPasswordEnvironment(options);\n        var createResult = await RunProcessAsync(client, MySqlClientArguments(options, $"--execute={createSql}"), mysqlRuntime, environment, null, cancellationToken).ConfigureAwait(false);\n        EnsureSuccess(createResult, $"{DisplayEngine(kind)} target database creation");\n\n        var restoreResult = await RunProcessAsync(client, MySqlClientArguments(options, databaseName), mysqlRuntime, environment, null, cancellationToken, source).ConfigureAwait(false);\n        EnsureSuccess(restoreResult, $"{DisplayEngine(kind)} restore");''')
regex_once(dbr, r'''        var defaults = CreateMySqlDefaultsFile\(effective\);\n        try\n        \{\n            _ = await RunProcessAsync\(admin, \[\$"--defaults-extra-file=\{defaults\}", "shutdown"\], runtime, null, null, cancellationToken\)\.ConfigureAwait\(false\);\n        \}\n        finally\n        \{\n            TryDeleteFile\(defaults\);\n        \}''',
           '''        _ = await RunProcessAsync(\n            admin,\n            MySqlClientArguments(effective, "shutdown"),\n            runtime,\n            MySqlPasswordEnvironment(effective),\n            null,\n            cancellationToken).ConfigureAwait(false);''')
regex_once(dbr, r'''    private string CreateMySqlDefaultsFile\(DatabaseConnectionOptions options\)\n    \{.*?\n    \}\n\n    private static string EscapeIniValue\(string value\)\n    \{.*?\n    \}\n''',
           '''    private static IReadOnlyList<string> MySqlClientArguments(DatabaseConnectionOptions options, params string[] commandArguments)\n    {\n        var result = new List<string>\n        {\n            $"--host={options.Host}",\n            $"--port={options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}",\n            $"--user={options.User}"\n        };\n        result.AddRange(commandArguments);\n        return result;\n    }\n\n    private static IReadOnlyDictionary<string, string?>? MySqlPasswordEnvironment(DatabaseConnectionOptions options) =>\n        string.IsNullOrEmpty(options.Password) ? null : new Dictionary<string, string?> { ["MYSQL_PWD"] = options.Password };\n\n''')

# 11. Configuration restore only accepts same-kind backups and validates them before replacing the active config.
replace_once('src/DevBox.Core/Services/ConfigurationFileService.cs',
'''        if (!File.Exists(source))\n            throw new FileNotFoundException("Configuration backup does not exist.", source);\n        var destination = GetPath(normalized);\n        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);\n        File.Copy(source, destination, overwrite: true);''',
'''        if (!File.Exists(source))\n            throw new FileNotFoundException("Configuration backup does not exist.", source);\n        var fileName = Path.GetFileName(source);\n        if (!fileName.StartsWith(normalized + "-", StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException($"Backup '{fileName}' does not belong to configuration '{normalized}'.");\n        var content = File.ReadAllText(source);\n        var validation = ValidateAsync(normalized, content).ConfigureAwait(false).GetAwaiter().GetResult();\n        if (!validation.IsValid)\n            throw new InvalidDataException($"Configuration backup failed validation: {validation.Message}");\n        var destination = GetPath(normalized);\n        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);\n        var temp = destination + $".{Guid.NewGuid():N}.restore.tmp";\n        try\n        {\n            File.WriteAllText(temp, content);\n            if (File.Exists(destination))\n                File.Replace(temp, destination, null);\n            else\n                File.Move(temp, destination);\n        }\n        finally\n        {\n            TryDeleteFile(temp);\n        }''')

# 12. Task Center defers disposing synchronization primitives until running tasks have actually exited.
tc = 'src/DevBox.Core/Services/PlatformTaskCenter.cs'
regex_once(tc, r'''    public void Dispose\(\)\n    \{\n        if \(_disposed\)\n            return;\n        _disposed = true;\n        foreach \(var entry in _entries.Values\)\n            entry.Cancellation\?\.Cancel\(\);\n        _parallelism.Dispose\(\);\n    \}''',
           '''    public void Dispose()\n    {\n        if (_disposed)\n            return;\n        _disposed = true;\n        var entries = _entries.Values.ToArray();\n        foreach (var entry in entries)\n            entry.Cancellation?.Cancel();\n\n        var executions = entries.Where(entry => entry.Execution is not null).Select(entry => entry.Execution!).ToArray();\n        if (executions.Length == 0)\n        {\n            DisposeSynchronization(entries);\n            return;\n        }\n\n        _ = Task.WhenAll(executions).ContinueWith(\n            _ => DisposeSynchronization(entries),\n            CancellationToken.None,\n            TaskContinuationOptions.ExecuteSynchronously,\n            TaskScheduler.Default);\n    }\n\n    private void DisposeSynchronization(IEnumerable<TaskEntry> entries)\n    {\n        foreach (var entry in entries)\n            entry.Cancellation?.Dispose();\n        _parallelism.Dispose();\n    }''')

# 13. Git bootstrap must fail if environment application is incomplete.
replace_once('src/DevBox.Core/Services/GitProjectBootstrapService.cs',
'''                if (!string.IsNullOrWhiteSpace(request.ProfileKey))\n                    environment = await locks.ApplyProfileAsync(projectRoot, request.ProfileKey, cancellationToken).ConfigureAwait(false);\n                else\n                    _ = locks.Generate(projectRoot);''',
'''                if (!string.IsNullOrWhiteSpace(request.ProfileKey))\n                {\n                    environment = await locks.ApplyProfileAsync(projectRoot, request.ProfileKey, cancellationToken).ConfigureAwait(false);\n                    if (environment.Warnings.Count > 0)\n                        throw new InvalidOperationException("Environment profile application was incomplete: " + string.Join(" | ", environment.Warnings));\n                }\n                else\n                {\n                    _ = locks.Generate(projectRoot);\n                }''')

# 14. Self-updater requires a valid Authenticode signature in addition to SHA-256.
updater = 'src/DevBox.Core/Services/ApplicationSelfUpdateService.cs'
replace_once(updater, 'using System.Runtime.InteropServices;\n', 'using System.Runtime.InteropServices;\nusing System.Security.Cryptography.X509Certificates;\n')
replace_once(updater, '''            VerifySha256(temporaryPath, expectedSha256);\n            File.Move(temporaryPath, installerPath, overwrite: true);''',
                      '''            VerifySha256(temporaryPath, expectedSha256);\n            VerifyAuthenticodeSignature(temporaryPath);\n            File.Move(temporaryPath, installerPath, overwrite: true);''')
replace_once(updater, '''    private static string ValidateGitHubUrl(string? value, string description)''',
'''    internal static void VerifyAuthenticodeSignature(string path)\n    {\n        if (!OperatingSystem.IsWindows())\n            throw new PlatformNotSupportedException("Authenticode verification requires Windows.");\n        if (!File.Exists(path))\n            throw new FileNotFoundException("Downloaded installer was not found for signature verification.", path);\n\n        var filePathPtr = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));\n        var fileInfo = new WinTrustFileInfo\n        {\n            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),\n            FilePath = filePathPtr\n        };\n        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());\n        Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);\n        var trustData = new WinTrustData\n        {\n            StructSize = (uint)Marshal.SizeOf<WinTrustData>(),\n            UiChoice = 2,\n            RevocationChecks = 0,\n            UnionChoice = 1,\n            FileInfo = fileInfoPtr,\n            StateAction = 1,\n            ProviderFlags = 0\n        };\n        try\n        {\n            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");\n            var status = WinVerifyTrust(IntPtr.Zero, action, ref trustData);\n            if (status != 0)\n                throw new InvalidDataException($"Downloaded DevBox installer does not have a valid trusted Authenticode signature (0x{status:X8}).");\n            trustData.StateAction = 2;\n            _ = WinVerifyTrust(IntPtr.Zero, action, ref trustData);\n        }\n        finally\n        {\n            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr);\n            Marshal.FreeHGlobal(fileInfoPtr);\n            Marshal.FreeCoTaskMem(filePathPtr);\n        }\n    }\n\n    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]\n    private struct WinTrustFileInfo\n    {\n        public uint StructSize;\n        public IntPtr FilePath;\n        public IntPtr FileHandle;\n        public IntPtr KnownSubject;\n    }\n\n    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]\n    private struct WinTrustData\n    {\n        public uint StructSize;\n        public IntPtr PolicyCallbackData;\n        public IntPtr SipClientData;\n        public uint UiChoice;\n        public uint RevocationChecks;\n        public uint UnionChoice;\n        public IntPtr FileInfo;\n        public uint StateAction;\n        public IntPtr StateData;\n        public IntPtr UrlReference;\n        public uint ProviderFlags;\n        public uint UiContext;\n        public IntPtr SignatureSettings;\n    }\n\n    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, PreserveSig = true)]\n    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, ref WinTrustData trustData);\n\n    private static string ValidateGitHubUrl(string? value, string description)''')

# 15. Make snapshot/transfer copy cancellation responsive for large files.
snapshot = 'src/DevBox.Core/Services/ProjectSnapshotService.cs'
replace_once(snapshot, '''    public Task<ProjectSnapshotResult> CreateAsync(''', '''    public async Task<ProjectSnapshotResult> CreateAsync(''')
replace_once(snapshot, '''                input.CopyTo(output);''', '''                await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);''')
replace_once(snapshot, '''                    input.CopyTo(output);''', '''                    await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);''')
replace_once(snapshot, '''            writer.Write(JsonSerializer.Serialize(metadata, JsonOptions));''', '''            await writer.WriteAsync(JsonSerializer.Serialize(metadata, JsonOptions)).ConfigureAwait(false);''')
replace_once(snapshot, '''        return Task.FromResult(new ProjectSnapshotResult(destination, projectName, info.Length, DateTimeOffset.UtcNow, included));''',
                       '''        return new ProjectSnapshotResult(destination, projectName, info.Length, DateTimeOffset.UtcNow, included);''')
replace_once(snapshot, '''    public Task<string> RestoreAsync(''', '''    public async Task<string> RestoreAsync(''')
replace_once(snapshot, '''            ExtractSnapshot(source, staging, databaseStaging, cancellationToken);''', '''            await ExtractSnapshotAsync(source, staging, databaseStaging, cancellationToken).ConfigureAwait(false);''')
replace_once(snapshot, '''            return Task.FromResult(destination);''', '''            return destination;''')
replace_once(snapshot, '''    private static void ExtractSnapshot(string archivePath, string projectDestination, string databaseDestination, CancellationToken cancellationToken)''',
                       '''    private static async Task ExtractSnapshotAsync(string archivePath, string projectDestination, string databaseDestination, CancellationToken cancellationToken)''')
replace_once(snapshot, '''            input.CopyTo(output);''', '''            await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);''')

transfer = 'src/DevBox.Core/Services/ProjectTransferService.cs'
replace_once(transfer, '''    public Task<ProjectTransferResult> ExportAsync(''', '''    public async Task<ProjectTransferResult> ExportAsync(''')
replace_once(transfer, '''                AddFile(archive, file, $"project/{relative}");''', '''                await AddFileAsync(archive, file, $"project/{relative}", cancellationToken).ConfigureAwait(false);''')
replace_once(transfer, '''                    AddFile(archive, backup, $"database/{Path.GetFileName(backup)}");''', '''                    await AddFileAsync(archive, backup, $"database/{Path.GetFileName(backup)}", cancellationToken).ConfigureAwait(false);''')
replace_once(transfer, '''            writer.Write(JsonSerializer.Serialize(transferManifest, JsonOptions));''', '''            await writer.WriteAsync(JsonSerializer.Serialize(transferManifest, JsonOptions)).ConfigureAwait(false);''')
replace_once(transfer, '''        return Task.FromResult(new ProjectTransferResult(destination, transferManifest, new FileInfo(destination).Length));''',
                       '''        return new ProjectTransferResult(destination, transferManifest, new FileInfo(destination).Length);''')
replace_once(transfer, '''    public Task<string> ImportAsync(''', '''    public async Task<string> ImportAsync(''')
replace_once(transfer, '''            ExtractSafely(source, tempRoot, cancellationToken);''', '''            await ExtractSafelyAsync(source, tempRoot, cancellationToken).ConfigureAwait(false);''')
replace_once(transfer, '''            return Task.FromResult(destination);''', '''            return destination;''')
replace_once(transfer, '''    private static void ExtractSafely(string archivePath, string destination, CancellationToken cancellationToken)''',
                       '''    private static async Task ExtractSafelyAsync(string archivePath, string destination, CancellationToken cancellationToken)''')
replace_once(transfer, '''            input.CopyTo(output);''', '''            await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);''')
regex_once(transfer, r'''    private static void AddFile\(ZipArchive archive, string sourcePath, string entryName\)\n    \{.*?\n    \}\n''',
           '''    private static async Task AddFileAsync(ZipArchive archive, string sourcePath, string entryName, CancellationToken cancellationToken)\n    {\n        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);\n        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);\n        await using var output = entry.Open();\n        await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);\n    }\n''')

# 16. Releases used by self-update must always be Authenticode signed.
release = '.github/workflows/release.yml'
replace_once(release,
'''          if ([string]::IsNullOrWhiteSpace($env:SIGNING_CERTIFICATE_BASE64)) {\n            Write-Host 'Authenticode certificate is not configured; release artifacts will remain unsigned.'\n            "enabled=false" >> $env:GITHUB_OUTPUT\n            exit 0\n          }''',
'''          if ([string]::IsNullOrWhiteSpace($env:SIGNING_CERTIFICATE_BASE64)) {\n            throw 'WINDOWS_SIGNING_CERTIFICATE_BASE64 is required. DevBox releases must be Authenticode signed because the self-updater verifies installer signatures.'\n          }''')

# 17. Regression tests for the newly hardened behavior.
test_path = ROOT / 'tests/DevBox.Tests/PostMergeAuditRound3Tests.cs'
test_path.write_text(r'''using System.Runtime.InteropServices;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class PostMergeAuditRound3Tests
{
    [Fact]
    public void RuntimeArchitecture_AllowsX64FallbackOnWindowsArm64()
    {
        Assert.True(RuntimePlatformService.IsPackageArchitectureCompatible("arm64", Architecture.Arm64));
        Assert.True(RuntimePlatformService.IsPackageArchitectureCompatible("any", Architecture.Arm64));
        if (OperatingSystem.IsWindows())
            Assert.True(RuntimePlatformService.IsPackageArchitectureCompatible("x64", Architecture.Arm64));
        Assert.False(RuntimePlatformService.IsPackageArchitectureCompatible("arm64", Architecture.X64));
    }

    [Fact]
    public void SiteManager_ConcurrentWritersDoNotLoseSites()
    {
        var root = TestRoot();
        try
        {
            var first = new SiteManager(root);
            var second = new SiteManager(root);
            Parallel.Invoke(
                () => first.Create("alpha"),
                () => second.Create("beta"));

            var names = new SiteManager(root).GetSites().Select(site => site.Name).OrderBy(value => value).ToArray();
            Assert.Equal(new[] { "alpha", "beta" }, names);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ApplyProfile_DoesNotWriteLockWhenPrerequisiteFails()
    {
        var root = TestRoot();
        try
        {
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            _ = workspace.Create(new ProjectCreateRequest(
                "demo",
                null,
                ProjectKind.EmptyPhp,
                null,
                false,
                "none",
                null,
                null,
                Array.Empty<string>(),
                Array.Empty<string>()));

            new EnvironmentProfileService(root).SaveCustomProfile(new EnvironmentProfile
            {
                Key = "missing-runtime",
                DisplayName = "Missing runtime",
                Kind = ProjectKind.EmptyPhp,
                Runtimes = new Dictionary<string, string> { ["not-installed"] = "1.0" },
                Database = new EnvironmentDatabasePin("none", null, null),
                Https = false
            });

            var project = Path.Combine(root, "www", "demo");
            using var service = new EnvironmentLockService(root);
            var result = await service.ApplyProfileAsync(project, "missing-runtime");

            Assert.NotEmpty(result.Warnings);
            Assert.False(File.Exists(Path.Combine(project, EnvironmentLockService.LockFileName)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ConfigurationRestore_RejectsBackupFromAnotherConfigurationKind()
    {
        var root = TestRoot();
        try
        {
            var backupRoot = Path.Combine(root, "backups", "configuration");
            Directory.CreateDirectory(backupRoot);
            var wrong = Path.Combine(backupRoot, "nginx-20260911.conf.bak");
            File.WriteAllText(wrong, "events {}\nhttp {}");
            var service = new ConfigurationFileService(root);
            Assert.Throws<InvalidOperationException>(() => service.RestoreBackup("mysql", wrong));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void PhpMyAdminPort_PrefersRunningRegisteredDatabase()
    {
        var instances = new[]
        {
            new DatabaseRuntimeInstance(DatabaseEngineKind.MySql, "8.4", 3307, "", "", "", true, ServiceState.Stopped, null),
            new DatabaseRuntimeInstance(DatabaseEngineKind.MariaDb, "11", 3316, "", "", "", true, ServiceState.Running, 42)
        };
        Assert.Equal(3316, AddonInstaller.SelectPhpMyAdminPort(instances));
        Assert.Equal(3306, AddonInstaller.SelectPhpMyAdminPort(Array.Empty<DatabaseRuntimeInstance>()));
    }

    private static string TestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DevBoxTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
''', encoding='utf-8')

print('Audit round 3 source transformations completed.')
