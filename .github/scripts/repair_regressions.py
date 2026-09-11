from pathlib import Path
import re


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:100]!r}")
    write(path, text.replace(old, new, 1))


def regex_once(path: str, pattern: str, replacement: str) -> None:
    text = read(path)
    updated, count = re.subn(pattern, lambda _: replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"{path}: regex expected one match, got {count}: {pattern}")
    write(path, updated)


# Environment apply commit barrier and transactional Site/TLS synchronization.
path = "src/DevBox.Core/Services/EnvironmentLockService.cs"
replace_once(
    path,
    """        var manifest = ReadManifestObject(root);\n        UpdateManifestFromLock(manifest, desired);\n""",
    """        if (warnings.Count > 0)\n        {\n            warnings.Add(\"Project metadata was left unchanged because one or more environment prerequisites failed.\");\n            return new EnvironmentApplyResult(desired, applied, warnings);\n        }\n\n        var manifest = ReadManifestObject(root);\n        UpdateManifestFromLock(manifest, desired);\n""",
)

sync_site_replacement = r'''    private void SynchronizeSite(string root, EnvironmentLockFile desired)
    {
        var detection = _workspace.Detect(root);
        var documentRoot = detection.Kind is ProjectKind.Laravel or ProjectKind.Symfony && Directory.Exists(Path.Combine(root, "public"))
            ? Path.Combine(root, "public")
            : root;
        var phpVersion = desired.Runtimes.TryGetValue("php", out var php) ? php : null;
        var previous = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(desired.ProjectName, StringComparison.OrdinalIgnoreCase));
        var tlsRollback = new TlsRollbackStateService(_rootPath);
        var desiredTlsState = tlsRollback.Capture(desired.Domain);
        TlsRollbackState? previousTlsState = previous is not null && !previous.Domain.Equals(desired.Domain, StringComparison.OrdinalIgnoreCase)
            ? tlsRollback.Capture(previous.Domain)
            : null;
        var created = false;

        try
        {
            if (previous is null)
            {
                _ = _sites.Create(desired.ProjectName, desired.Domain, documentRoot);
                created = true;
            }

            if (desired.Https)
            {
                if (OperatingSystem.IsWindows())
                {
                    using var authority = new LocalCertificateAuthorityService(_rootPath);
                    using var certificate = authority.IssueSiteCertificate(desired.Domain);
                }
                else
                {
                    _ = new LocalCertificateManager(_rootPath).Ensure(desired.Domain);
                }
            }
            else
            {
                new LocalCertificateManager(_rootPath).Delete(desired.Domain);
            }

            var updated = new SiteDefinition(desired.ProjectName, desired.Domain, documentRoot, "php", phpVersion, desired.Https);
            _ = _sites.Update(updated);

            if (previous is not null && !previous.Domain.Equals(desired.Domain, StringComparison.OrdinalIgnoreCase))
            {
                var oldDomainStillUsed = _sites.GetSites().Any(item =>
                    !item.Name.Equals(previous.Name, StringComparison.OrdinalIgnoreCase) &&
                    item.Domain.Equals(previous.Domain, StringComparison.OrdinalIgnoreCase));
                if (!oldDomainStillUsed)
                    new LocalCertificateManager(_rootPath).Delete(previous.Domain);
            }
        }
        catch
        {
            try
            {
                if (created)
                    _sites.Delete(desired.ProjectName);
                else if (previous is not null)
                    _ = _sites.Update(previous);
            }
            catch (Exception)
            {
            }

            try { tlsRollback.Restore(desiredTlsState); }
            catch (Exception) { }
            if (previousTlsState is not null)
            {
                try { tlsRollback.Restore(previousTlsState); }
                catch (Exception) { }
            }
            throw;
        }
    }

    private static void UpdateManifestFromLock'''
regex_once(
    path,
    r"    private void SynchronizeSite\(string root, EnvironmentLockFile desired\)\n    \{.*?\n    \}\n\n    private static void UpdateManifestFromLock",
    sync_site_replacement,
)

# Clearer WPF state when environment apply is only partial.
path = "src/DevBox.App/ViewModels/EnvironmentCenterViewModel.cs"
replace_once(
    path,
    """    private static string DescribeApply(EnvironmentApplyResult result) =>\n        $\"Applied environment: {result.Applied.Count} action(s), {result.Warnings.Count} warning(s).\";\n""",
    """    private static string DescribeApply(EnvironmentApplyResult result) =>\n        result.Warnings.Count == 0\n            ? $\"Applied environment: {result.Applied.Count} action(s).\"\n            : $\"Environment apply incomplete: {result.Applied.Count} action(s), {result.Warnings.Count} warning(s). Project metadata was not committed.\";\n""",
)

# Project create/import rollback.
path = "src/DevBox.Core/Services/ProjectWorkspaceService.cs"
create_replacement = r'''    public SiteDefinition Create(ProjectCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == ProjectKind.Node)
            throw new InvalidOperationException("Node-only projects are not served by the current PHP/Nginx Site model. Import the project first and run its Node service separately.");

        var projectRoot = Path.Combine(_wwwRoot, NormalizeProjectDirectoryName(request.Name));
        var projectRootExisted = Directory.Exists(projectRoot);
        if (projectRootExisted && Directory.EnumerateFileSystemEntries(projectRoot).Any())
            throw new InvalidOperationException($"Project destination is not empty: {projectRoot}");

        var tlsRollback = new TlsRollbackStateService(_rootPath);
        var tlsState = tlsRollback.Capture(request.Domain);
        var siteCreated = false;
        try
        {
            Directory.CreateDirectory(projectRoot);
            var documentRoot = UsesPublicDocumentRoot(request.Kind)
                ? Path.Combine(projectRoot, "public")
                : projectRoot;
            Directory.CreateDirectory(documentRoot);

            var site = _siteManager.Create(request.Name, request.Domain, documentRoot);
            siteCreated = true;
            if (!string.IsNullOrWhiteSpace(request.PhpVersion))
                site = _siteManager.SetPhpVersion(site.Name, request.PhpVersion);

            if (request.Https)
            {
                _certificateManager.Ensure(site.Domain);
                site = _siteManager.SetHttps(site.Name, true);
            }

            EnsurePlaceholderEntryPoint(projectRoot, documentRoot, request.Kind);
            SaveManifest(projectRoot, new DevBoxProjectManifest(
                DevBoxProjectManifest.CurrentSchemaVersion,
                site.Name,
                site.Domain,
                request.Kind,
                site.PhpVersion,
                request.NodeVersion,
                NormalizeDatabaseEngine(request.DatabaseEngine),
                string.IsNullOrWhiteSpace(request.DatabaseName) ? site.Name.Replace('-', '_') : request.DatabaseName.Trim(),
                site.HttpsEnabled,
                (request.Addons ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray()));

            return site;
        }
        catch
        {
            if (siteCreated)
            {
                try { _siteManager.Delete(request.Name); }
                catch (Exception) { }
            }
            try { tlsRollback.Restore(tlsState); }
            catch (Exception) { }
            RollbackOwnedProjectDirectory(projectRoot, projectRootExisted);
            throw;
        }
    }

'''
regex_once(
    path,
    r"    public SiteDefinition Create\(ProjectCreateRequest request\)\n    \{.*?\n    \}\n\n(?=    public SiteDefinition Import)",
    create_replacement,
)

import_replacement = r'''    public SiteDefinition Import(ProjectImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = RequireExistingDirectory(request.SourcePath);
        var detection = Detect(source);

        var projectRoot = request.CopyIntoDevBox
            ? Path.Combine(_wwwRoot, NormalizeProjectDirectoryName(request.Name))
            : EnsureUnderWww(source);
        var projectRootExisted = Directory.Exists(projectRoot);
        if (request.CopyIntoDevBox && projectRootExisted && Directory.EnumerateFileSystemEntries(projectRoot).Any())
            throw new InvalidOperationException($"Import destination is not empty: {projectRoot}");

        var manifestPath = Path.Combine(projectRoot, ManifestFileName);
        var previousManifest = !request.CopyIntoDevBox && File.Exists(manifestPath) ? File.ReadAllBytes(manifestPath) : null;
        var tlsRollback = new TlsRollbackStateService(_rootPath);
        var tlsState = tlsRollback.Capture(request.Domain);
        var siteCreated = false;

        try
        {
            if (request.CopyIntoDevBox)
            {
                Directory.CreateDirectory(projectRoot);
                CopyDirectorySafely(source, projectRoot);
            }

            var documentRoot = ResolveDocumentRoot(projectRoot, detection.Kind);
            var site = _siteManager.Create(request.Name, request.Domain, documentRoot);
            siteCreated = true;
            if (!string.IsNullOrWhiteSpace(request.PhpVersion))
                site = _siteManager.SetPhpVersion(site.Name, request.PhpVersion);
            if (request.Https)
            {
                _certificateManager.Ensure(site.Domain);
                site = _siteManager.SetHttps(site.Name, true);
            }

            SaveManifest(projectRoot, new DevBoxProjectManifest(
                DevBoxProjectManifest.CurrentSchemaVersion,
                site.Name,
                site.Domain,
                detection.Kind,
                site.PhpVersion,
                null,
                "mysql",
                site.Name.Replace('-', '_'),
                site.HttpsEnabled,
                Array.Empty<string>()));

            return site;
        }
        catch
        {
            if (siteCreated)
            {
                try { _siteManager.Delete(request.Name); }
                catch (Exception) { }
            }
            try { tlsRollback.Restore(tlsState); }
            catch (Exception) { }

            if (request.CopyIntoDevBox)
                RollbackOwnedProjectDirectory(projectRoot, projectRootExisted);
            else
                RestoreManifest(manifestPath, previousManifest);
            throw;
        }
    }

'''
regex_once(
    path,
    r"    public SiteDefinition Import\(ProjectImportRequest request\)\n    \{.*?\n    \}\n\n(?=    public DevBoxProjectManifest\? LoadManifest)",
    import_replacement,
)

replace_once(
    path,
    "    private static bool UsesPublicDocumentRoot(ProjectKind kind) =>\n",
    r'''    private static void RollbackOwnedProjectDirectory(string projectRoot, bool existedBefore)
    {
        try
        {
            if (Directory.Exists(projectRoot))
                Directory.Delete(projectRoot, recursive: true);
            if (existedBefore)
                Directory.CreateDirectory(projectRoot);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void RestoreManifest(string path, byte[]? previous)
    {
        try
        {
            if (previous is null)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, previous);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool UsesPublicDocumentRoot(ProjectKind kind) =>
''',
)

# Secure secret synchronization and plaintext zeroing.
path = "src/DevBox.Core/Services/SecureSecretStore.cs"
replace_once(path, "using System.ComponentModel;\n", "using System.Collections.Concurrent;\nusing System.ComponentModel;\n")
replace_once(
    path,
    """    private readonly string _storePath;\n    private readonly object _sync = new();\n""",
    """    private static readonly ConcurrentDictionary<string, object> StoreLocks = new(StringComparer.OrdinalIgnoreCase);\n    private readonly string _storePath;\n    private readonly object _sync;\n""",
)
replace_once(
    path,
    """        _storePath = Path.Combine(Path.GetFullPath(rootPath), \"config\", \"secrets.dpapi.json\");\n""",
    """        _storePath = Path.Combine(Path.GetFullPath(rootPath), \"config\", \"secrets.dpapi.json\");\n        _sync = StoreLocks.GetOrAdd(_storePath, static _ => new object());\n""",
)
replace_once(
    path,
    """    public void Set(string key, string value) => SetBytes(key, Encoding.UTF8.GetBytes(value ?? throw new ArgumentNullException(nameof(value))));\n\n    public string? Get(string key)\n    {\n        var bytes = GetBytes(key);\n        return bytes is null ? null : Encoding.UTF8.GetString(bytes);\n    }\n""",
    """    public void Set(string key, string value)\n    {\n        var bytes = Encoding.UTF8.GetBytes(value ?? throw new ArgumentNullException(nameof(value)));\n        try { SetBytes(key, bytes); }\n        finally { CryptographicZero(bytes); }\n    }\n\n    public string? Get(string key)\n    {\n        var bytes = GetBytes(key);\n        if (bytes is null)\n            return null;\n        try { return Encoding.UTF8.GetString(bytes); }\n        finally { CryptographicZero(bytes); }\n    }\n""",
)
replace_once(
    path,
    """        var protectedBytes = Protect(value.ToArray());\n        lock (_sync)\n        {\n            var values = Load();\n            values[key.Trim().ToLowerInvariant()] = Convert.ToBase64String(protectedBytes);\n            Save(values);\n        }\n        CryptographicZero(protectedBytes);\n""",
    """        var plaintext = value.ToArray();\n        byte[] protectedBytes;\n        try { protectedBytes = Protect(plaintext); }\n        finally { CryptographicZero(plaintext); }\n        try\n        {\n            lock (_sync)\n            {\n                var values = Load();\n                values[key.Trim().ToLowerInvariant()] = Convert.ToBase64String(protectedBytes);\n                Save(values);\n            }\n        }\n        finally\n        {\n            CryptographicZero(protectedBytes);\n        }\n""",
)

# Database runtime registration serialization, atomic backups and PostgreSQL target DB creation.
path = "src/DevBox.Core/Services/DatabaseRuntimeService.cs"
replace_once(
    path,
    "        var registrations = LoadRegistrations().ToList();\n",
    "        using var registrationLock = AcquireRegistrationLock();\n        var registrations = LoadRegistrations().ToList();\n",
)
replace_once(
    path,
    "    private DatabaseRuntimeRegistration GetRegistration(string engine, string version)\n",
    r'''    private FileStream AcquireRegistrationLock()
    {
        var lockPath = _registrationsPath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    private DatabaseRuntimeRegistration GetRegistration(string engine, string version)
''',
)

backup_replacement = r'''    public async Task<DatabaseBackupResult> BackupAsync(
        string engine,
        string version,
        string databaseName,
        DatabaseConnectionOptions? options = null,
        string? destinationPath = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateDatabaseName(databaseName);
        var registration = GetRegistration(engine, version);
        var kind = ParseEngine(registration.Engine);
        options = NormalizeOptions(options ?? DefaultOptions(kind, registration.Port), registration.Port);
        var backupRoot = Path.Combine(_rootPath, "backups", "databases", registration.Engine);
        Directory.CreateDirectory(backupRoot);
        var extension = kind == DatabaseEngineKind.PostgreSql ? ".dump" : ".sql";
        var destination = string.IsNullOrWhiteSpace(destinationPath)
            ? Path.Combine(backupRoot, $"{databaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}{extension}")
            : Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporaryDestination = destination + $".{Guid.NewGuid():N}.tmp";

        try
        {
            if (kind == DatabaseEngineKind.PostgreSql)
            {
                var executable = Path.Combine(RuntimePath(registration.Engine, version), "bin", "pg_dump.exe");
                EnsureExecutable(executable);
                var result = await RunProcessAsync(
                    executable,
                    ["-h", options.Host, "-p", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", options.User, "-Fc", "-f", temporaryDestination, databaseName],
                    _rootPath,
                    PasswordEnvironment(options, kind),
                    null,
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result, "PostgreSQL backup");
            }
            else
            {
                var runtime = RuntimePath(registration.Engine, version);
                var executable = FirstExisting(Path.Combine(runtime, "bin", "mysqldump.exe"), Path.Combine(runtime, "bin", "mariadb-dump.exe"))
                    ?? throw new FileNotFoundException($"{DisplayEngine(kind)} dump client was not found.");
                var defaults = CreateMySqlDefaultsFile(options);
                try
                {
                    var result = await RunProcessAsync(
                        executable,
                        [$"--defaults-extra-file={defaults}", "--single-transaction", "--routines", "--events", "--triggers", databaseName],
                        runtime,
                        null,
                        temporaryDestination,
                        cancellationToken).ConfigureAwait(false);
                    EnsureSuccess(result, $"{DisplayEngine(kind)} backup");
                }
                finally
                {
                    TryDeleteFile(defaults);
                }
            }

            var temporaryInfo = new FileInfo(temporaryDestination);
            if (!temporaryInfo.Exists)
                throw new InvalidDataException("Database backup command completed without creating the backup file.");
            File.Move(temporaryDestination, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryDestination);
        }

        var info = new FileInfo(destination);
        return new DatabaseBackupResult(registration.Engine, databaseName, destination, info.Length, DateTimeOffset.UtcNow);
    }

'''
regex_once(
    path,
    r"    public async Task<DatabaseBackupResult> BackupAsync\(.*?\n    \}\n\n(?=    public async Task RestoreAsync)",
    backup_replacement,
)

restore_replacement = r'''    public async Task RestoreAsync(
        string engine,
        string version,
        string databaseName,
        string backupPath,
        DatabaseConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateDatabaseName(databaseName);
        var source = Path.GetFullPath(backupPath);
        if (!File.Exists(source))
            throw new FileNotFoundException("Database backup was not found.", source);
        var registration = GetRegistration(engine, version);
        var kind = ParseEngine(registration.Engine);
        options = NormalizeOptions(options ?? DefaultOptions(kind, registration.Port), registration.Port);

        if (kind == DatabaseEngineKind.PostgreSql)
        {
            var runtime = RuntimePath(registration.Engine, version);
            await EnsurePostgreSqlDatabaseAsync(runtime, databaseName, options, cancellationToken).ConfigureAwait(false);
            var executable = Path.Combine(runtime, "bin", "pg_restore.exe");
            EnsureExecutable(executable);
            var result = await RunProcessAsync(
                executable,
                ["-h", options.Host, "-p", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", options.User, "--clean", "--if-exists", "--no-owner", "-d", databaseName, source],
                runtime,
                PasswordEnvironment(options, kind),
                null,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "PostgreSQL restore");
            return;
        }

        var mysqlRuntime = RuntimePath(registration.Engine, version);
        var client = FirstExisting(Path.Combine(mysqlRuntime, "bin", "mysql.exe"), Path.Combine(mysqlRuntime, "bin", "mariadb.exe"))
            ?? throw new FileNotFoundException($"{DisplayEngine(kind)} command client was not found.");
        var defaultsFile = CreateMySqlDefaultsFile(options);
        try
        {
            var createSql = $"CREATE DATABASE IF NOT EXISTS `{databaseName}`";
            var createResult = await RunProcessAsync(client, [$"--defaults-extra-file={defaultsFile}", $"--execute={createSql}"], mysqlRuntime, null, null, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(createResult, $"{DisplayEngine(kind)} target database creation");

            var restoreResult = await RunProcessAsync(client, [$"--defaults-extra-file={defaultsFile}", databaseName], mysqlRuntime, null, null, cancellationToken, source).ConfigureAwait(false);
            EnsureSuccess(restoreResult, $"{DisplayEngine(kind)} restore");
        }
        finally
        {
            TryDeleteFile(defaultsFile);
        }
    }

    private static async Task EnsurePostgreSqlDatabaseAsync(
        string runtime,
        string databaseName,
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var psql = Path.Combine(runtime, "bin", "psql.exe");
        var createdb = Path.Combine(runtime, "bin", "createdb.exe");
        EnsureExecutable(psql);
        EnsureExecutable(createdb);
        var port = options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var environment = PasswordEnvironment(options, DatabaseEngineKind.PostgreSql);
        var exists = await RunProcessAsync(
            psql,
            ["-h", options.Host, "-p", port, "-U", options.User, "-d", "postgres", "-tA", "-c", $"SELECT 1 FROM pg_database WHERE datname = '{databaseName}';"],
            runtime,
            environment,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(exists, "PostgreSQL database existence check");
        if (exists.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("1", StringComparer.Ordinal))
            return;

        var create = await RunProcessAsync(
            createdb,
            ["-h", options.Host, "-p", port, "-U", options.User, databaseName],
            runtime,
            environment,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(create, "PostgreSQL target database creation");
    }

'''
regex_once(
    path,
    r"    public async Task RestoreAsync\(.*?\n    \}\n\n(?=    public void Dispose\(\))",
    restore_replacement,
)

# HTTPS disable cleans obsolete certificate/trust.
path = "src/DevBox.App/ViewModels/FeatureViewModels.cs"
replace_once(
    path,
    """            _siteManager.SetHttps(site.Name, false);\n            await RestartNginxIfRunningAsync();\n""",
    """            _siteManager.SetHttps(site.Name, false);\n            _certificateManager.Delete(site.Domain);\n            await RestartNginxIfRunningAsync();\n""",
)
replace_once(
    path,
    """        ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or FileNotFoundException or Win32Exception or System.Security.Cryptography.CryptographicException;\n""",
    """        ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or FileNotFoundException or Win32Exception or System.Security.Cryptography.CryptographicException or CertificateTrustStoreException;\n""",
)

# Certificate replacement rollback and material validation.
path = "src/DevBox.Core/Services/LocalCertificateManager.cs"
replace_once(
    path,
    """        AtomicWrite(certificatePath, certificate.ExportCertificatePem());\n        AtomicWrite(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());\n\n        if (!string.IsNullOrWhiteSpace(replacedThumbprint) &&\n            !replacedThumbprint.Equals(certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))\n            RemoveTrustedThumbprint(replacedThumbprint);\n\n        return ToModel(normalizedDomain, certificatePath, privateKeyPath, certificate);\n""",
    """        var previousCertificate = File.Exists(certificatePath) ? File.ReadAllBytes(certificatePath) : null;\n        var previousKey = File.Exists(privateKeyPath) ? File.ReadAllBytes(privateKeyPath) : null;\n        var previousWasTrusted = false;\n        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(replacedThumbprint))\n        {\n            try { previousWasTrusted = IsTrustedForCurrentUser(normalizedDomain); }\n            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { }\n        }\n\n        try\n        {\n            AtomicWrite(certificatePath, certificate.ExportCertificatePem());\n            AtomicWrite(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());\n\n            if (!string.IsNullOrWhiteSpace(replacedThumbprint) &&\n                !replacedThumbprint.Equals(certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))\n                RemoveTrustedThumbprint(replacedThumbprint);\n        }\n        catch\n        {\n            RestoreBytes(certificatePath, previousCertificate);\n            RestoreBytes(privateKeyPath, previousKey);\n            if (previousWasTrusted && OperatingSystem.IsWindows())\n            {\n                try { TrustForCurrentUser(normalizedDomain); }\n                catch (Exception) { }\n            }\n            throw;\n        }\n\n        return ToModel(normalizedDomain, certificatePath, privateKeyPath, certificate);\n""",
)
replace_once(
    path,
    "    public bool IsTrustedForCurrentUser(string domain)\n",
    r'''    public bool IsMaterialValid(string domain, TimeSpan? minimumRemainingLifetime = null)
    {
        var normalizedDomain = NormalizeDomain(domain);
        var certificatePath = CertificatePath(normalizedDomain);
        var privateKeyPath = PrivateKeyPath(normalizedDomain);
        if (!File.Exists(certificatePath) || !File.Exists(privateKeyPath))
            return false;
        try
        {
            using var certificate = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
            var now = DateTime.UtcNow;
            var minimum = minimumRemainingLifetime ?? TimeSpan.FromMinutes(1);
            var dnsName = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);
            return certificate.NotBefore.ToUniversalTime() <= now.AddMinutes(5) &&
                   certificate.NotAfter.ToUniversalTime() > now.Add(minimum) &&
                   dnsName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public bool IsTrustedForCurrentUser(string domain)
''',
)
replace_once(
    path,
    "    private static void DeleteIfExists(string path)\n",
    r'''    private static void RestoreBytes(string path, byte[]? content)
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
''',
)

# Health checks validate real certificate material instead of mere file presence.
path = "src/DevBox.Core/Services/ProjectWorkspaceService.cs"
replace_once(
    path,
    """            var certificate = Path.Combine(_rootPath, \"config\", \"ssl\", \"sites\", $\"{site.Domain}.crt.pem\");\n            var key = Path.Combine(_rootPath, \"config\", \"ssl\", \"sites\", $\"{site.Domain}.key.pem\");\n            checks.Add(File.Exists(certificate) && File.Exists(key)\n                ? Healthy(\"certificate\", \"TLS certificate\", certificate)\n                : Error(\"certificate\", \"TLS certificate\", \"HTTPS is enabled but the certificate or private key is missing.\", true));\n""",
    """            var certificate = Path.Combine(_rootPath, \"config\", \"ssl\", \"sites\", $\"{site.Domain}.crt.pem\");\n            checks.Add(_certificateManager.IsMaterialValid(site.Domain)\n                ? Healthy(\"certificate\", \"TLS certificate\", certificate)\n                : Error(\"certificate\", \"TLS certificate\", \"HTTPS is enabled but certificate material is missing, invalid, expired, mismatched, or issued for another domain.\", true));\n""",
)

path = "src/DevBox.Core/Services/AdvancedDiagnosticsService.cs"
replace_once(
    path,
    """                var cert = Path.Combine(_rootPath, \"config\", \"ssl\", \"sites\", $\"{site.Domain}.crt.pem\");\n                var key = Path.Combine(_rootPath, \"config\", \"ssl\", \"sites\", $\"{site.Domain}.key.pem\");\n                if (!File.Exists(cert) || !File.Exists(key))\n                    findings.Add(Error($\"site-tls-{site.Name}\", \"TLS\", \"HTTPS is enabled but certificate material is missing.\", site.Domain, \"Reissue the Site certificate.\"));\n""",
    """                var certificates = new LocalCertificateManager(_rootPath);\n                if (!certificates.IsMaterialValid(site.Domain))\n                    findings.Add(Error($\"site-tls-{site.Name}\", \"TLS\", \"HTTPS is enabled but certificate material is missing, invalid, expired, mismatched, or issued for another domain.\", site.Domain, \"Reissue the Site certificate.\"));\n""",
)

# Failed bootstrap actions fail the bootstrap and activate existing rollback.
path = "src/DevBox.Core/Services/GitProjectBootstrapService.cs"
replace_once(
    path,
    "            return new GitBootstrapResult(projectRoot, detection.Kind, environment, actions);\n",
    """            var failedAction = actions.FirstOrDefault(action => !action.Succeeded);\n            if (failedAction is not null)\n                throw new InvalidOperationException($\"Bootstrap action '{failedAction.Key}' failed with exit code {failedAction.ExitCode}: {TrimOutput(failedAction.StandardError)}\");\n\n            return new GitBootstrapResult(projectRoot, detection.Kind, environment, actions);\n""",
)

# Architecture-specific updater installer selection.
path = "src/DevBox.Core/Services/ApplicationSelfUpdateService.cs"
replace_once(path, "using System.Security.Cryptography;\n", "using System.Runtime.InteropServices;\nusing System.Security.Cryptography;\n")
replace_once(
    path,
    "        var expectedInstallerName = $\"DevBox-{version.ToString(3)}-win-x64-setup.exe\";\n",
    "        var expectedInstallerName = GetExpectedInstallerAssetName(version);\n",
)
replace_once(
    path,
    "    internal static string ParseChecksum(string checksumFile, string fileName)\n",
    r'''    internal static string GetExpectedInstallerAssetName(Version version, Architecture? architecture = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        var effectiveArchitecture = architecture ?? RuntimeInformation.ProcessArchitecture;
        var rid = effectiveArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException($"DevBox self-update does not support {effectiveArchitecture}.")
        };
        return $"DevBox-{version.ToString(3)}-{rid}-setup.exe";
    }

    internal static string ParseChecksum(string checksumFile, string fileName)
''',
)

# Secure erase MySQL temporary client configuration.
path = "src/DevBox.Core/Services/DatabaseManager.cs"
replace_once(
    path,
    """            if (File.Exists(configPath))\n            {\n                try\n                {\n                    File.SetAttributes(configPath, FileAttributes.Normal);\n                    File.Delete(configPath);\n                }\n                catch (IOException)\n                {\n                }\n                catch (UnauthorizedAccessException)\n                {\n                }\n            }\n""",
    "            SecureDeleteClientConfig(configPath);\n",
)
replace_once(
    path,
    "    private static string QuoteOptionValue(string value)\n",
    r'''    private static void SecureDeleteClientConfig(string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            var length = new FileInfo(path).Length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                var zeros = new byte[4096];
                long remaining = length;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(zeros.Length, remaining);
                    stream.Write(zeros, 0, count);
                    remaining -= count;
                }
                stream.Flush(flushToDisk: true);
            }
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string QuoteOptionValue(string value)
''',
)

# Source version is newer than the already-published v0.2.2 release.
path = "src/DevBox.App/DevBox.App.csproj"
text = read(path)
for old, new in [
    ("<Version>0.2.2</Version>", "<Version>0.2.3</Version>"),
    ("<AssemblyVersion>0.2.2.0</AssemblyVersion>", "<AssemblyVersion>0.2.3.0</AssemblyVersion>"),
    ("<FileVersion>0.2.2.0</FileVersion>", "<FileVersion>0.2.3.0</FileVersion>"),
]:
    if old not in text:
        raise RuntimeError(f"{path}: missing {old}")
    text = text.replace(old, new, 1)
write(path, text)
replace_once("installer/DevBox.iss", '#define MyAppVersion "0.2.2"', '#define MyAppVersion "0.2.3"')
replace_once(
    "README.md",
    "Current application version: `0.2.2`.",
    "Current application version: `0.2.3` (development; latest published release remains `0.2.2`).",
)

# Regression coverage.
tests = r'''using System.Runtime.InteropServices;
using System.Text.Json;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.Tests;

public sealed class PostMergeRegressionTests
{
    [Fact]
    public void DatabaseRuntime_RegisterAcrossInstances_DoesNotLoseRegistrations()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "database-runtimes.json"), "[]");
            const int count = 12;
            for (var i = 0; i < count; i++)
            {
                var runtime = Path.Combine(root, "runtime", "mysql", $"8.4.{i}", "bin");
                Directory.CreateDirectory(runtime);
                File.WriteAllBytes(Path.Combine(runtime, "mysqld.exe"), []);
            }

            Parallel.For(0, count, i =>
            {
                using var service = new DatabaseRuntimeService(root);
                _ = service.Register("mysql", $"8.4.{i}", 3500 + i);
            });

            using var verification = new DatabaseRuntimeService(root);
            var registrations = verification.GetInstances("mysql");
            Assert.Equal(count, registrations.Count);
            Assert.Equal(count, registrations.Select(item => item.Port).Distinct().Count());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task EnvironmentLock_PrerequisiteWarning_DoesNotCommitProjectMetadata()
    {
        var root = NewRoot();
        try
        {
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            _ = workspace.Create(new ProjectCreateRequest("app", "app.test", ProjectKind.Php, null, null, "none", null, false, Array.Empty<string>()));
            var project = Path.Combine(root, "www", "app");

            var lockFile = new EnvironmentLockFile
            {
                ProjectName = "app",
                Domain = "changed.test",
                Database = new EnvironmentDatabasePin("none", null, null),
                Addons = new[] { "definitely-missing-addon" }
            };
            File.WriteAllText(Path.Combine(project, EnvironmentLockService.LockFileName), JsonSerializer.Serialize(lockFile));

            using var service = new EnvironmentLockService(root);
            var result = await service.ApplyLockAsync(project);

            Assert.NotEmpty(result.Warnings);
            var manifest = workspace.LoadManifest(project);
            Assert.NotNull(manifest);
            Assert.Equal("app.test", manifest!.Domain);
            Assert.Equal("app.test", sites.GetSites().Single(item => item.Name == "app").Domain);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void LocalCertificateManager_MismatchedPrivateKey_IsNotHealthy()
    {
        var root = NewRoot();
        try
        {
            var manager = new LocalCertificateManager(root);
            var certificate = manager.Ensure("valid.test");
            Assert.True(manager.IsMaterialValid("valid.test"));

            using var replacement = System.Security.Cryptography.RSA.Create(2048);
            File.WriteAllText(certificate.PrivateKeyPath, replacement.ExportPkcs8PrivateKeyPem());
            Assert.False(manager.IsMaterialValid("valid.test"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SelfUpdater_UsesArchitectureSpecificInstallerAsset()
    {
        var version = new Version(1, 2, 3);
        Assert.Equal("DevBox-1.2.3-win-x64-setup.exe", ApplicationSelfUpdateService.GetExpectedInstallerAssetName(version, Architecture.X64));
        Assert.Equal("DevBox-1.2.3-win-arm64-setup.exe", ApplicationSelfUpdateService.GetExpectedInstallerAssetName(version, Architecture.Arm64));
    }

    [Fact]
    public void SecureSecretStore_ConcurrentInstances_PreserveAllKeys()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = NewRoot();
        try
        {
            const int count = 24;
            Parallel.For(0, count, i => new SecureSecretStore(root).Set($"test.key.{i}", $"value-{i}"));
            var store = new SecureSecretStore(root);
            Assert.Equal(count, store.ListKeys().Count);
            for (var i = 0; i < count; i++)
                Assert.Equal($"value-{i}", store.Get($"test.key.{i}"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-regression-" + Guid.NewGuid().ToString("N"));
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
'''
write("tests/DevBox.Tests/PostMergeRegressionTests.cs", tests)
