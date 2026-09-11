from pathlib import Path
import re

ROOT = Path('.')


def read(path):
    return (ROOT / path).read_text(encoding='utf-8')


def write(path, text):
    p = ROOT / path
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding='utf-8', newline='\n')


def replace_once(path, old, new):
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{path}: expected one match, found {count}')
    write(path, text.replace(old, new, 1))


# Shared cross-process lock helper.
write('src/DevBox.Core/Services/CrossProcessFileLock.cs', r'''namespace DevBox.Core.Services;

internal static class CrossProcessFileLock
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static FileStream Acquire(string path, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            try
            {
                return new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"Timed out waiting for DevBox lock '{fullPath}'. Another DevBox process may still be using this resource.", ex);
            }
        }
    }

    public static async Task<FileStream> AcquireAsync(
        string path,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"Timed out waiting for DevBox lock '{fullPath}'. Another DevBox process may still be using this resource.", ex);
            }
        }
    }
}
''')

# SecureSecretStore: serialize Load/Save across GUI/CLI processes.
replace_once('src/DevBox.Core/Services/SecureSecretStore.cs',
'''    private readonly string _storePath;\n    private readonly object _sync;\n''',
'''    private readonly string _storePath;\n    private readonly string _processLockPath;\n    private readonly object _sync;\n''')
replace_once('src/DevBox.Core/Services/SecureSecretStore.cs',
'''        _storePath = Path.Combine(Path.GetFullPath(rootPath), "config", "secrets.dpapi.json");\n        _sync = StoreLocks.GetOrAdd(_storePath, static _ => new object());\n''',
'''        _storePath = Path.Combine(Path.GetFullPath(rootPath), "config", "secrets.dpapi.json");\n        _processLockPath = _storePath + ".lock";\n        _sync = StoreLocks.GetOrAdd(_storePath, static _ => new object());\n''')
replace_once('src/DevBox.Core/Services/SecureSecretStore.cs',
'''    public IReadOnlyList<string> ListKeys()\n    {\n        lock (_sync)\n            return Load().Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();\n    }\n''',
'''    public IReadOnlyList<string> ListKeys()\n    {\n        lock (_sync)\n        {\n            using var processLock = CrossProcessFileLock.Acquire(_processLockPath);\n            return Load().Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();\n        }\n    }\n''')
replace_once('src/DevBox.Core/Services/SecureSecretStore.cs',
'''            lock (_sync)\n            {\n                var values = Load();\n                values[key.Trim().ToLowerInvariant()] = Convert.ToBase64String(protectedBytes);\n                Save(values);\n            }\n''',
'''            lock (_sync)\n            {\n                using var processLock = CrossProcessFileLock.Acquire(_processLockPath);\n                var values = Load();\n                values[key.Trim().ToLowerInvariant()] = Convert.ToBase64String(protectedBytes);\n                Save(values);\n            }\n''')
replace_once('src/DevBox.Core/Services/SecureSecretStore.cs',
'''        lock (_sync)\n        {\n            var values = Load();\n            if (!values.TryGetValue(key.Trim().ToLowerInvariant(), out var encoded))\n''',
'''        lock (_sync)\n        {\n            using var processLock = CrossProcessFileLock.Acquire(_processLockPath);\n            var values = Load();\n            if (!values.TryGetValue(key.Trim().ToLowerInvariant(), out var encoded))\n''')
replace_once('src/DevBox.Core/Services/SecureSecretStore.cs',
'''        lock (_sync)\n        {\n            var values = Load();\n            var removed = values.Remove(key.Trim().ToLowerInvariant());\n''',
'''        lock (_sync)\n        {\n            using var processLock = CrossProcessFileLock.Acquire(_processLockPath);\n            var values = Load();\n            var removed = values.Remove(key.Trim().ToLowerInvariant());\n''')

# ProcessManager: cross-process service ownership lock around Start/Stop critical sections.
replace_once('src/DevBox.Core/Services/ProcessManager.cs',
'''        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);\n        try\n        {\n            if (_processes.TryGetValue(definition.Key, out var existing) && !existing.Process.HasExited)\n''',
'''        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);\n        try\n        {\n            using var processLock = await CrossProcessFileLock.AcquireAsync(ProcessLockPath(definition), cancellationToken).ConfigureAwait(false);\n            if (_processes.TryGetValue(definition.Key, out var existing) && !existing.Process.HasExited)\n''')
replace_once('src/DevBox.Core/Services/ProcessManager.cs',
'''        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);\n        try\n        {\n            ManagedProcess? managed = null;\n''',
'''        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);\n        try\n        {\n            using var processLock = await CrossProcessFileLock.AcquireAsync(ProcessLockPath(definition), cancellationToken).ConfigureAwait(false);\n            ManagedProcess? managed = null;\n''')
replace_once('src/DevBox.Core/Services/ProcessManager.cs',
'''    private static string PidMarkerPath(ServiceDefinition definition) =>\n        Path.Combine(definition.WorkingDirectory, "tmp", "services", $"{definition.Key}.pid");\n''',
'''    private static string PidMarkerPath(ServiceDefinition definition) =>\n        Path.Combine(definition.WorkingDirectory, "tmp", "services", $"{definition.Key}.pid");\n\n    private static string ProcessLockPath(ServiceDefinition definition) =>\n        Path.Combine(definition.WorkingDirectory, "tmp", "services", $"{definition.Key}.lock");\n''')

# Database initialization: hold a cross-process lock for the complete init/delete-on-failure sequence.
replace_once('src/DevBox.Core/Services/DatabaseRuntimeService.cs',
'''        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);\n        try\n        {\n            if (IsInitialized(instance.Engine, version))\n''',
'''        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);\n        try\n        {\n            using var initializationLock = await CrossProcessFileLock.AcquireAsync(\n                InitializationLockPath(instance.Engine, version),\n                cancellationToken,\n                TimeSpan.FromSeconds(30)).ConfigureAwait(false);\n            if (IsInitialized(instance.Engine, version))\n''')
replace_once('src/DevBox.Core/Services/DatabaseRuntimeService.cs',
'''    private FileStream AcquireRegistrationLock()\n    {\n        var lockPath = _registrationsPath + ".lock";\n        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);\n        var deadline = DateTime.UtcNow.AddSeconds(10);\n        while (true)\n        {\n            try\n            {\n                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);\n            }\n            catch (IOException) when (DateTime.UtcNow < deadline)\n            {\n                Thread.Sleep(25);\n            }\n        }\n    }\n''',
'''    private FileStream AcquireRegistrationLock() =>\n        CrossProcessFileLock.Acquire(_registrationsPath + ".lock", TimeSpan.FromSeconds(10));\n\n    private string InitializationLockPath(DatabaseEngineKind engine, string version) =>\n        Path.Combine(_rootPath, "tmp", "locks", $"database-init-{SafeServiceSegment(NormalizeEngine(engine))}-{SafeServiceSegment(version)}.lock");\n''')

# EnvironmentLock: validate actual TLS material and make Site + manifest + actions one transaction.
replace_once('src/DevBox.Core/Services/EnvironmentLockService.cs',
'''        if (desired.Https)\n        {\n            var certificate = Path.Combine(_rootPath, "config", "ssl", "sites", $"{desired.Domain}.crt.pem");\n            var key = Path.Combine(_rootPath, "config", "ssl", "sites", $"{desired.Domain}.key.pem");\n            if (!File.Exists(certificate) || !File.Exists(key))\n                drift.Add($"HTTPS certificate files are missing for {desired.Domain}.");\n        }\n''',
'''        if (desired.Https && !new LocalCertificateManager(_rootPath).IsMaterialValid(desired.Domain))\n            drift.Add($"HTTPS certificate material is missing, invalid, expired, mismatched, or issued for another domain: {desired.Domain}.");\n''')
old_apply = '''        var manifest = ReadManifestObject(root);\n        UpdateManifestFromLock(manifest, desired);\n        AtomicWrite(Path.Combine(root, ProjectWorkspaceService.ManifestFileName), manifest.ToJsonString(JsonOptions));\n        applied.Add("Synchronized devbox.json with devbox.lock.json.");\n\n        _actions.SetActions(root, desired.Actions);\n        applied.Add($"Synchronized {desired.Actions.Count} project action(s).");\n\n        try\n        {\n            SynchronizeSite(root, desired);\n            applied.Add($"Synchronized Site {desired.Domain}.");\n        }\n        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)\n        {\n            warnings.Add($"Site/TLS: {ex.Message}");\n        }\n\n        return new EnvironmentApplyResult(desired, applied, warnings);\n'''
new_apply = '''        var manifestPath = Path.Combine(root, ProjectWorkspaceService.ManifestFileName);\n        var previousManifestBytes = File.ReadAllBytes(manifestPath);\n        var previousActions = _actions.GetActions(root);\n        var previousSite = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(desired.ProjectName, StringComparison.OrdinalIgnoreCase));\n        var tlsRollback = new TlsRollbackStateService(_rootPath);\n        var desiredTlsState = tlsRollback.Capture(desired.Domain);\n        TlsRollbackState? previousTlsState = previousSite is not null && !previousSite.Domain.Equals(desired.Domain, StringComparison.OrdinalIgnoreCase)\n            ? tlsRollback.Capture(previousSite.Domain)\n            : null;\n\n        try\n        {\n            SynchronizeSite(root, desired);\n            applied.Add($"Synchronized Site {desired.Domain}.");\n\n            var manifest = ReadManifestObject(root);\n            UpdateManifestFromLock(manifest, desired);\n            AtomicWrite(manifestPath, manifest.ToJsonString(JsonOptions));\n            applied.Add("Synchronized devbox.json with devbox.lock.json.");\n\n            _actions.SetActions(root, desired.Actions);\n            applied.Add($"Synchronized {desired.Actions.Count} project action(s).");\n            return new EnvironmentApplyResult(desired, applied, warnings);\n        }\n        catch (Exception original)\n        {\n            var rollbackActions = new List<Action>\n            {\n                () => RestoreBytes(manifestPath, previousManifestBytes),\n                () => _actions.SetActions(root, previousActions),\n                () => RestoreSite(previousSite, desired.ProjectName),\n                () => tlsRollback.Restore(desiredTlsState)\n            };\n            if (previousTlsState is not null)\n                rollbackActions.Add(() => tlsRollback.Restore(previousTlsState));\n            RollbackExecutor.RethrowAfterRollback(original, rollbackActions.ToArray());\n            throw new InvalidOperationException("Environment rollback executor returned unexpectedly.");\n        }\n'''
replace_once('src/DevBox.Core/Services/EnvironmentLockService.cs', old_apply, new_apply)
old_sync = '''    private void SynchronizeSite(string root, EnvironmentLockFile desired)\n    {\n        var detection = _workspace.Detect(root);\n        var documentRoot = detection.Kind is ProjectKind.Laravel or ProjectKind.Symfony && Directory.Exists(Path.Combine(root, "public"))\n            ? Path.Combine(root, "public")\n            : root;\n        var phpVersion = desired.Runtimes.TryGetValue("php", out var php) ? php : null;\n        var previous = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(desired.ProjectName, StringComparison.OrdinalIgnoreCase));\n        var tlsRollback = new TlsRollbackStateService(_rootPath);\n        var desiredTlsState = tlsRollback.Capture(desired.Domain);\n        TlsRollbackState? previousTlsState = previous is not null && !previous.Domain.Equals(desired.Domain, StringComparison.OrdinalIgnoreCase)\n            ? tlsRollback.Capture(previous.Domain)\n            : null;\n        var created = false;\n\n        try\n        {\n            if (previous is null)\n            {\n                _ = _sites.Create(desired.ProjectName, desired.Domain, documentRoot);\n                created = true;\n            }\n\n            if (desired.Https)\n            {\n                if (OperatingSystem.IsWindows())\n                {\n                    using var authority = new LocalCertificateAuthorityService(_rootPath);\n                    using var certificate = authority.IssueSiteCertificate(desired.Domain);\n                }\n                else\n                {\n                    _ = new LocalCertificateManager(_rootPath).Ensure(desired.Domain);\n                }\n            }\n            else\n            {\n                new LocalCertificateManager(_rootPath).Delete(desired.Domain);\n            }\n\n            var updated = new SiteDefinition(desired.ProjectName, desired.Domain, documentRoot, "php", phpVersion, desired.Https);\n            _ = _sites.Update(updated);\n\n            if (previous is not null && !previous.Domain.Equals(desired.Domain, StringComparison.OrdinalIgnoreCase))\n            {\n                var oldDomainStillUsed = _sites.GetSites().Any(item =>\n                    !item.Name.Equals(previous.Name, StringComparison.OrdinalIgnoreCase) &&\n                    item.Domain.Equals(previous.Domain, StringComparison.OrdinalIgnoreCase));\n                if (!oldDomainStillUsed)\n                    new LocalCertificateManager(_rootPath).Delete(previous.Domain);\n            }\n        }\n        catch\n        {\n            try\n            {\n                if (created)\n                    _sites.Delete(desired.ProjectName);\n                else if (previous is not null)\n                    _ = _sites.Update(previous);\n            }\n            catch (Exception)\n            {\n            }\n\n            try { tlsRollback.Restore(desiredTlsState); }\n            catch (Exception) { }\n            if (previousTlsState is not null)\n            {\n                try { tlsRollback.Restore(previousTlsState); }\n                catch (Exception) { }\n            }\n            throw;\n        }\n    }\n'''
new_sync = '''    private void SynchronizeSite(string root, EnvironmentLockFile desired)\n    {\n        var detection = _workspace.Detect(root);\n        var documentRoot = detection.Kind is ProjectKind.Laravel or ProjectKind.Symfony && Directory.Exists(Path.Combine(root, "public"))\n            ? Path.Combine(root, "public")\n            : root;\n        var phpVersion = desired.Runtimes.TryGetValue("php", out var php) ? php : null;\n        var previous = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(desired.ProjectName, StringComparison.OrdinalIgnoreCase));\n\n        if (previous is null)\n            _ = _sites.Create(desired.ProjectName, desired.Domain, documentRoot);\n\n        if (desired.Https)\n        {\n            if (OperatingSystem.IsWindows())\n            {\n                using var authority = new LocalCertificateAuthorityService(_rootPath);\n                using var certificate = authority.IssueSiteCertificate(desired.Domain);\n            }\n            else\n            {\n                _ = new LocalCertificateManager(_rootPath).Ensure(desired.Domain);\n            }\n        }\n        else\n        {\n            new LocalCertificateManager(_rootPath).Delete(desired.Domain);\n        }\n\n        var updated = new SiteDefinition(desired.ProjectName, desired.Domain, documentRoot, "php", phpVersion, desired.Https);\n        _ = _sites.Update(updated);\n\n        if (previous is not null && !previous.Domain.Equals(desired.Domain, StringComparison.OrdinalIgnoreCase))\n        {\n            var oldDomainStillUsed = _sites.GetSites().Any(item =>\n                !item.Name.Equals(previous.Name, StringComparison.OrdinalIgnoreCase) &&\n                item.Domain.Equals(previous.Domain, StringComparison.OrdinalIgnoreCase));\n            if (!oldDomainStillUsed)\n                new LocalCertificateManager(_rootPath).Delete(previous.Domain);\n        }\n    }\n\n    private void RestoreSite(SiteDefinition? previous, string projectName)\n    {\n        var current = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));\n        if (previous is null)\n        {\n            if (current is not null)\n                _sites.Delete(projectName);\n            return;\n        }\n\n        if (current is null)\n            _ = _sites.Create(previous.Name, previous.Domain, previous.DocumentRoot);\n        _ = _sites.Update(previous);\n    }\n\n    private static void RestoreBytes(string path, byte[] content)\n    {\n        Directory.CreateDirectory(Path.GetDirectoryName(path)!);\n        var temp = path + $".{Guid.NewGuid():N}.rollback";\n        try\n        {\n            File.WriteAllBytes(temp, content);\n            if (File.Exists(path))\n                File.Replace(temp, path, null);\n            else\n                File.Move(temp, path);\n        }\n        finally\n        {\n            if (File.Exists(temp))\n                File.Delete(temp);\n        }\n    }\n'''
replace_once('src/DevBox.Core/Services/EnvironmentLockService.cs', old_sync, new_sync)

# Local cert Ensure must verify domain/SAN and key, not only expiry.
replace_once('src/DevBox.Core/Services/LocalCertificateManager.cs',
'''                using var existing = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);\n                if (existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(7))\n                    return ToModel(normalizedDomain, certificatePath, privateKeyPath, existing);\n                replacedThumbprint = existing.Thumbprint;\n''',
'''                using var existing = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);\n                if (IsCertificateValidForDomain(existing, normalizedDomain, TimeSpan.FromDays(7)))\n                    return ToModel(normalizedDomain, certificatePath, privateKeyPath, existing);\n                replacedThumbprint = existing.Thumbprint;\n''')
replace_once('src/DevBox.Core/Services/LocalCertificateManager.cs',
'''            var now = DateTime.UtcNow;\n            var minimum = minimumRemainingLifetime ?? TimeSpan.FromMinutes(1);\n            var dnsName = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);\n            return certificate.NotBefore.ToUniversalTime() <= now.AddMinutes(5) &&\n                   certificate.NotAfter.ToUniversalTime() > now.Add(minimum) &&\n                   dnsName.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase);\n''',
'''            return IsCertificateValidForDomain(certificate, normalizedDomain, minimumRemainingLifetime ?? TimeSpan.FromMinutes(1));\n''')
replace_once('src/DevBox.Core/Services/LocalCertificateManager.cs',
'''    private static LocalCertificate ToModel(string domain, string certificatePath, string privateKeyPath, X509Certificate2 certificate) =>\n''',
'''    private static bool IsCertificateValidForDomain(X509Certificate2 certificate, string domain, TimeSpan minimumRemainingLifetime)\n    {\n        var now = DateTime.UtcNow;\n        var dnsName = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);\n        return certificate.NotBefore.ToUniversalTime() <= now.AddMinutes(5) &&\n               certificate.NotAfter.ToUniversalTime() > now.Add(minimumRemainingLifetime) &&\n               dnsName.Equals(domain, StringComparison.OrdinalIgnoreCase);\n    }\n\n    private static LocalCertificate ToModel(string domain, string certificatePath, string privateKeyPath, X509Certificate2 certificate) =>\n''')

# TLS rollback: execute every compensation and surface aggregate failures.
start = read('src/DevBox.Core/Services/TlsRollbackStateService.cs')
method_start = start.index('    public void Restore(TlsRollbackState state)')
method_end = start.index('    private static void TrustLeaf', method_start)
new_restore = r'''    public void Restore(TlsRollbackState state)
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
            Attempt(() => _certificates.UntrustForCurrentUser(normalizedDomain));

        Attempt(() => RestoreBytes(certificatePath, state.Certificate));
        Attempt(() => RestoreBytes(privateKeyPath, state.PrivateKey));

        if (OperatingSystem.IsWindows())
        {
            if (state.Certificate is not null && state.LeafTrusted)
                Attempt(() => TrustLeaf(certificatePath));
            else if (state.Certificate is not null)
                Attempt(() => _certificates.UntrustForCurrentUser(normalizedDomain));

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

'''
write('src/DevBox.Core/Services/TlsRollbackStateService.cs', start[:method_start] + new_restore + start[method_end:])

# ProjectWorkspace rollback: preserve pre-existing empty root metadata and aggregate rollback failures.
p = 'src/DevBox.Core/Services/ProjectWorkspaceService.cs'
text = read(p)
old = '''        catch\n        {\n            if (siteCreated)\n            {\n                try { _siteManager.Delete(request.Name); }\n                catch (Exception) { }\n            }\n            try { tlsRollback.Restore(tlsState); }\n            catch (Exception) { }\n            RollbackOwnedProjectDirectory(projectRoot, projectRootExisted);\n            throw;\n        }\n'''
new = '''        catch (Exception original)\n        {\n            var rollbackActions = new List<Action>();\n            if (siteCreated)\n                rollbackActions.Add(() => _siteManager.Delete(request.Name));\n            rollbackActions.Add(() => tlsRollback.Restore(tlsState));\n            rollbackActions.Add(() => RollbackOwnedProjectDirectory(projectRoot, projectRootExisted));\n            RollbackExecutor.RethrowAfterRollback(original, rollbackActions.ToArray());\n            throw new InvalidOperationException("Project create rollback executor returned unexpectedly.");\n        }\n'''
if text.count(old) != 1:
    raise RuntimeError(f'{p}: create catch match count {text.count(old)}')
text = text.replace(old, new, 1)
old2 = '''        catch\n        {\n            if (siteCreated)\n            {\n                try { _siteManager.Delete(request.Name); }\n                catch (Exception) { }\n            }\n            try { tlsRollback.Restore(tlsState); }\n            catch (Exception) { }\n\n            if (request.CopyIntoDevBox)\n                RollbackOwnedProjectDirectory(projectRoot, projectRootExisted);\n            else\n                RestoreManifest(manifestPath, previousManifest);\n            throw;\n        }\n'''
new2 = '''        catch (Exception original)\n        {\n            var rollbackActions = new List<Action>();\n            if (siteCreated)\n                rollbackActions.Add(() => _siteManager.Delete(request.Name));\n            rollbackActions.Add(() => tlsRollback.Restore(tlsState));\n            rollbackActions.Add(request.CopyIntoDevBox\n                ? () => RollbackOwnedProjectDirectory(projectRoot, projectRootExisted)\n                : () => RestoreManifest(manifestPath, previousManifest));\n            RollbackExecutor.RethrowAfterRollback(original, rollbackActions.ToArray());\n            throw new InvalidOperationException("Project import rollback executor returned unexpectedly.");\n        }\n'''
if text.count(old2) != 1:
    raise RuntimeError(f'{p}: import catch match count {text.count(old2)}')
text = text.replace(old2, new2, 1)
old3 = '''    private static void RollbackOwnedProjectDirectory(string projectRoot, bool existedBefore)\n    {\n        try\n        {\n            if (Directory.Exists(projectRoot))\n                Directory.Delete(projectRoot, recursive: true);\n            if (existedBefore)\n                Directory.CreateDirectory(projectRoot);\n        }\n        catch (IOException) { }\n        catch (UnauthorizedAccessException) { }\n    }\n\n    private static void RestoreManifest(string path, byte[]? previous)\n    {\n        try\n        {\n            if (previous is null)\n            {\n                if (File.Exists(path))\n                    File.Delete(path);\n                return;\n            }\n            Directory.CreateDirectory(Path.GetDirectoryName(path)!);\n            File.WriteAllBytes(path, previous);\n        }\n        catch (IOException) { }\n        catch (UnauthorizedAccessException) { }\n    }\n'''
new3 = '''    private static void RollbackOwnedProjectDirectory(string projectRoot, bool existedBefore)\n    {\n        if (!Directory.Exists(projectRoot))\n            return;\n        if (!existedBefore)\n        {\n            Directory.Delete(projectRoot, recursive: true);\n            return;\n        }\n\n        foreach (var file in Directory.EnumerateFiles(projectRoot))\n            File.Delete(file);\n        foreach (var directory in Directory.EnumerateDirectories(projectRoot))\n            Directory.Delete(directory, recursive: true);\n    }\n\n    private static void RestoreManifest(string path, byte[]? previous)\n    {\n        if (previous is null)\n        {\n            if (File.Exists(path))\n                File.Delete(path);\n            return;\n        }\n        Directory.CreateDirectory(Path.GetDirectoryName(path)!);\n        File.WriteAllBytes(path, previous);\n    }\n'''
if text.count(old3) != 1:
    raise RuntimeError(f'{p}: rollback helper match count {text.count(old3)}')
write(p, text.replace(old3, new3, 1))

# ProjectProvisioning rollback after workspace.Create succeeds.
p = 'src/DevBox.Core/Services/ProjectProvisioningService.cs'
text = read(p)
text = text.replace('''    private readonly ProjectWorkspaceService _workspace;\n    private readonly ProjectDatabaseProvisioner _projectDatabases;\n''', '''    private readonly ProjectWorkspaceService _workspace;\n    private readonly SiteManager _sites;\n    private readonly ProjectDatabaseProvisioner _projectDatabases;\n''', 1)
text = text.replace('''        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));\n        ArgumentNullException.ThrowIfNull(databases);\n''', '''        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));\n        _sites = new SiteManager(_rootPath);\n        ArgumentNullException.ThrowIfNull(databases);\n''', 1)
old_body = '''        var site = _workspace.Create(request);\n        actions.Add($"Created Site {site.Domain}.");\n\n        var projectRoot = _workspace.ResolveProjectRoot(site.DocumentRoot);\n        var manifest = _workspace.LoadManifest(projectRoot)\n            ?? throw new InvalidDataException("Project manifest was not created.");\n        manifest = manifest with\n        {\n            Addons = NormalizeKeys(request.Addons),\n            Services = NormalizeKeys(request.Services)\n        };\n        _workspace.SaveManifest(projectRoot, manifest);\n        actions.Add("Saved devbox.json project manifest.");\n\n        if (!string.IsNullOrWhiteSpace(manifest.NodeVersion))\n        {\n            var nodeExe = Path.Combine(_rootPath, "runtime", "node", manifest.NodeVersion, "node.exe");\n            var npmCmd = Path.Combine(_rootPath, "runtime", "node", manifest.NodeVersion, "npm.cmd");\n            if (File.Exists(nodeExe) && File.Exists(npmCmd))\n                actions.Add($"Pinned Node.js {manifest.NodeVersion} is available for project commands.");\n            else\n                warnings.Add($"Node.js {manifest.NodeVersion} is pinned by the profile but is not installed. Install the portable Node LTS runtime from Developer Tools before running npm presets.");\n        }\n\n        if (!manifest.DatabaseEngine.Equals("none", StringComparison.OrdinalIgnoreCase) &&\n            !string.IsNullOrWhiteSpace(manifest.DatabaseName))\n        {\n            if (_projectDatabases.IsAvailable(manifest.DatabaseEngine))\n            {\n                await _projectDatabases.EnsureDatabaseAsync(\n                    manifest.DatabaseEngine,\n                    manifest.DatabaseName,\n                    databaseOptions,\n                    cancellationToken).ConfigureAwait(false);\n                actions.Add($"Ensured {DisplayEngine(manifest.DatabaseEngine)} database {manifest.DatabaseName}.");\n            }\n            else\n            {\n                warnings.Add($"{DisplayEngine(manifest.DatabaseEngine)} database was declared but its native client runtime is not available yet.");\n            }\n        }\n\n        foreach (var serviceKey in manifest.Services)\n        {\n            var template = ResolveManagedServiceTemplate(serviceKey);\n            if (template is null)\n            {\n                warnings.Add($"Managed service '{serviceKey}' has no built-in template and was left declarative only.");\n                continue;\n            }\n\n            var executablePath = Path.GetFullPath(Path.Combine(_rootPath, template.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar)));\n            var enabled = File.Exists(executablePath);\n            _managedServices.Upsert(template with { Enabled = enabled });\n            if (enabled)\n                actions.Add($"Registered managed service {template.DisplayName}.");\n            else\n                warnings.Add($"{template.DisplayName} is required by the profile but its runtime is not installed. The service definition was registered disabled.");\n        }\n\n        return new ProjectProvisioningResult(site, manifest, actions, warnings);\n'''
new_body = '''        var expectedProjectRoot = Path.Combine(_rootPath, "www", request.Name.Trim().ToLowerInvariant());\n        var projectRootExisted = Directory.Exists(expectedProjectRoot);\n        var site = _workspace.Create(request);\n        actions.Add($"Created Site {site.Domain}.");\n\n        var projectRoot = _workspace.ResolveProjectRoot(site.DocumentRoot);\n        try\n        {\n            var manifest = _workspace.LoadManifest(projectRoot)\n                ?? throw new InvalidDataException("Project manifest was not created.");\n            manifest = manifest with\n            {\n                Addons = NormalizeKeys(request.Addons),\n                Services = NormalizeKeys(request.Services)\n            };\n            _workspace.SaveManifest(projectRoot, manifest);\n            actions.Add("Saved devbox.json project manifest.");\n\n            if (!string.IsNullOrWhiteSpace(manifest.NodeVersion))\n            {\n                var nodeExe = Path.Combine(_rootPath, "runtime", "node", manifest.NodeVersion, "node.exe");\n                var npmCmd = Path.Combine(_rootPath, "runtime", "node", manifest.NodeVersion, "npm.cmd");\n                if (File.Exists(nodeExe) && File.Exists(npmCmd))\n                    actions.Add($"Pinned Node.js {manifest.NodeVersion} is available for project commands.");\n                else\n                    warnings.Add($"Node.js {manifest.NodeVersion} is pinned by the profile but is not installed. Install the portable Node LTS runtime from Developer Tools before running npm presets.");\n            }\n\n            if (!manifest.DatabaseEngine.Equals("none", StringComparison.OrdinalIgnoreCase) &&\n                !string.IsNullOrWhiteSpace(manifest.DatabaseName))\n            {\n                if (_projectDatabases.IsAvailable(manifest.DatabaseEngine))\n                {\n                    await _projectDatabases.EnsureDatabaseAsync(\n                        manifest.DatabaseEngine,\n                        manifest.DatabaseName,\n                        databaseOptions,\n                        cancellationToken).ConfigureAwait(false);\n                    actions.Add($"Ensured {DisplayEngine(manifest.DatabaseEngine)} database {manifest.DatabaseName}.");\n                }\n                else\n                {\n                    warnings.Add($"{DisplayEngine(manifest.DatabaseEngine)} database was declared but its native client runtime is not available yet.");\n                }\n            }\n\n            foreach (var serviceKey in manifest.Services)\n            {\n                var template = ResolveManagedServiceTemplate(serviceKey);\n                if (template is null)\n                {\n                    warnings.Add($"Managed service '{serviceKey}' has no built-in template and was left declarative only.");\n                    continue;\n                }\n\n                var executablePath = Path.GetFullPath(Path.Combine(_rootPath, template.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar)));\n                var enabled = File.Exists(executablePath);\n                _managedServices.Upsert(template with { Enabled = enabled });\n                if (enabled)\n                    actions.Add($"Registered managed service {template.DisplayName}.");\n                else\n                    warnings.Add($"{template.DisplayName} is required by the profile but its runtime is not installed. The service definition was registered disabled.");\n            }\n\n            return new ProjectProvisioningResult(site, manifest, actions, warnings);\n        }\n        catch (Exception original)\n        {\n            RollbackExecutor.RethrowAfterRollback(\n                original,\n                () =>\n                {\n                    var current = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(site.Name, StringComparison.OrdinalIgnoreCase));\n                    if (current is not null)\n                        _sites.Delete(current.Name);\n                },\n                () => RollbackProjectDirectory(projectRoot, projectRootExisted));\n            throw new InvalidOperationException("Project provisioning rollback executor returned unexpectedly.");\n        }\n'''
if text.count(old_body) != 1:
    raise RuntimeError(f'{p}: provisioning body match count {text.count(old_body)}')
text = text.replace(old_body, new_body, 1)
insert = '''\n    private static void RollbackProjectDirectory(string projectRoot, bool existedBefore)\n    {\n        if (!Directory.Exists(projectRoot))\n            return;\n        if (!existedBefore)\n        {\n            Directory.Delete(projectRoot, recursive: true);\n            return;\n        }\n        foreach (var file in Directory.EnumerateFiles(projectRoot))\n            File.Delete(file);\n        foreach (var directory in Directory.EnumerateDirectories(projectRoot))\n            Directory.Delete(directory, recursive: true);\n    }\n'''
marker = '''    private static string DisplayEngine(string engine) =>\n'''
if text.count(marker) != 1:
    raise RuntimeError(f'{p}: marker count {text.count(marker)}')
text = text.replace(marker, insert + '\n' + marker, 1)
write(p, text)

# Git bootstrap: do not remove a directory that existed before the operation.
replace_once('src/DevBox.Core/Services/GitProjectBootstrapService.cs',
'''        var destination = Path.Combine(_wwwRoot, projectName);\n        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())\n''',
'''        var destination = Path.Combine(_wwwRoot, projectName);\n        var destinationExisted = Directory.Exists(destination);\n        if (destinationExisted && Directory.EnumerateFileSystemEntries(destination).Any())\n''')
replace_once('src/DevBox.Core/Services/GitProjectBootstrapService.cs',
'''                TryDeleteDirectory(destination);\n''',
'''                TryRollbackDestination(destination, destinationExisted);\n''')
replace_once('src/DevBox.Core/Services/GitProjectBootstrapService.cs',
'''    private static void TryDeleteDirectory(string path)\n    {\n        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }\n        catch (IOException) { }\n        catch (UnauthorizedAccessException) { }\n    }\n''',
'''    private static void TryRollbackDestination(string path, bool existedBefore)\n    {\n        try\n        {\n            if (!Directory.Exists(path))\n                return;\n            if (!existedBefore)\n            {\n                Directory.Delete(path, recursive: true);\n                return;\n            }\n            foreach (var file in Directory.EnumerateFiles(path))\n                File.Delete(file);\n            foreach (var directory in Directory.EnumerateDirectories(path))\n                Directory.Delete(directory, recursive: true);\n        }\n        catch (IOException) { }\n        catch (UnauthorizedAccessException) { }\n    }\n\n    private static void TryDeleteDirectory(string path)\n    {\n        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }\n        catch (IOException) { }\n        catch (UnauthorizedAccessException) { }\n    }\n''')

# Parameterize installer architecture so release can ship a native ARM64 installer.
replace_once('installer/DevBox.iss',
'''#ifndef OutputDir\n  #define OutputDir "..\\artifacts"\n#endif\n''',
'''#ifndef OutputDir\n  #define OutputDir "..\\artifacts"\n#endif\n#ifndef MyAppRid\n  #define MyAppRid "win-x64"\n#endif\n#ifndef MyArchitecturesAllowed\n  #define MyArchitecturesAllowed "x64compatible"\n#endif\n#ifndef MyArchitecturesInstallMode\n  #define MyArchitecturesInstallMode "x64compatible"\n#endif\n''')
replace_once('installer/DevBox.iss',
'''OutputBaseFilename=DevBox-{#MyAppVersion}-win-x64-setup\n''',
'''OutputBaseFilename=DevBox-{#MyAppVersion}-{#MyAppRid}-setup\n''')
replace_once('installer/DevBox.iss',
'''ArchitecturesAllowed=x64compatible\nArchitecturesInstallIn64BitMode=x64compatible\n''',
'''ArchitecturesAllowed={#MyArchitecturesAllowed}\nArchitecturesInstallIn64BitMode={#MyArchitecturesInstallMode}\n''')

# Release workflow: bump from latest published tag; update dev README line; build/sign both installers.
p = '.github/workflows/release.yml'
text = read(p)
old_resolve = '''          $currentText = $match.Groups['version'].Value\n          $parts = $currentText.Split('.')\n          $major = [int]$parts[0]\n          $minor = [int]$parts[1]\n          $patch = [int]$parts[2]\n'''
new_resolve = '''          $sourceVersion = $match.Groups['version'].Value\n          git fetch origin --tags --force\n          $latestTag = git tag --list 'v*' --sort=-v:refname | Where-Object { $_ -match '^v\\d+\\.\\d+\\.\\d+$' } | Select-Object -First 1\n          $currentText = if ([string]::IsNullOrWhiteSpace($latestTag)) { '0.0.0' } else { $latestTag.Substring(1) }\n          $parts = $currentText.Split('.')\n          $major = [int]$parts[0]\n          $minor = [int]$parts[1]\n          $patch = [int]$parts[2]\n'''
if text.count(old_resolve) != 1:
    raise RuntimeError('release resolve current version block not found')
text = text.replace(old_resolve, new_resolve, 1)
text = text.replace('''          git fetch origin --tags --force\n          $existingTag = git tag --list $releaseTag\n''', '''          $existingTag = git tag --list $releaseTag\n''', 1)
text = text.replace('''          "- Current version: ``$currentText``" >> $env:GITHUB_STEP_SUMMARY\n''', '''          "- Latest published version: ``$currentText``" >> $env:GITHUB_STEP_SUMMARY\n          "- Source development version: ``$sourceVersion``" >> $env:GITHUB_STEP_SUMMARY\n''', 1)
old_readme = """            'Current application version: `\\d+\\.\\d+\\.\\d+`\\.',\n            ('Current application version: `' + $env:DEVBOX_VERSION + '`.'),\n"""
new_readme = """            'Current application version: `\\d+\\.\\d+\\.\\d+`(?: \\(development; latest published release remains `\\d+\\.\\d+\\.\\d+`\\))?\\.',\n            ('Current application version: `' + $env:DEVBOX_VERSION + '`.'),\n"""
if text.count(old_readme) != 1:
    raise RuntimeError('README release regex not found')
text = text.replace(old_readme, new_readme, 1)
old_build = '''      - name: Build installer\n        shell: pwsh\n        run: |\n          $iscc = "${env:ProgramFiles(x86)}\\Inno Setup 6\\ISCC.exe"\n          & $iscc "/DMyAppVersion=$env:DEVBOX_VERSION" "/DPublishDir=$pwd\\publish\\win-x64" "/DOutputDir=$pwd\\artifacts" "installer\\DevBox.iss"\n          if ($LASTEXITCODE -ne 0) {\n            throw "Inno Setup failed with exit code $LASTEXITCODE"\n          }\n'''
new_build = '''      - name: Build installers\n        shell: pwsh\n        run: |\n          $iscc = "${env:ProgramFiles(x86)}\\Inno Setup 6\\ISCC.exe"\n          & $iscc "/DMyAppVersion=$env:DEVBOX_VERSION" "/DMyAppRid=win-x64" "/DMyArchitecturesAllowed=x64compatible" "/DMyArchitecturesInstallMode=x64compatible" "/DPublishDir=$pwd\\publish\\win-x64" "/DOutputDir=$pwd\\artifacts" "installer\\DevBox.iss"\n          if ($LASTEXITCODE -ne 0) { throw "Inno Setup x64 build failed with exit code $LASTEXITCODE" }\n          & $iscc "/DMyAppVersion=$env:DEVBOX_VERSION" "/DMyAppRid=win-arm64" "/DMyArchitecturesAllowed=arm64" "/DMyArchitecturesInstallMode=arm64" "/DPublishDir=$pwd\\publish\\win-arm64" "/DOutputDir=$pwd\\artifacts" "installer\\DevBox.iss"\n          if ($LASTEXITCODE -ne 0) { throw "Inno Setup ARM64 build failed with exit code $LASTEXITCODE" }\n'''
if text.count(old_build) != 1:
    raise RuntimeError('installer build block not found')
text = text.replace(old_build, new_build, 1)
old_sign = '''          $installer = "artifacts/DevBox-$env:DEVBOX_VERSION-win-x64-setup.exe"\n          & $signtool.FullName sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /f $env:SIGNING_CERTIFICATE_PATH /p $env:SIGNING_CERTIFICATE_PASSWORD $installer\n          if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed for installer.' }\n'''
new_sign = '''          $installers = @(\n            "artifacts/DevBox-$env:DEVBOX_VERSION-win-x64-setup.exe",\n            "artifacts/DevBox-$env:DEVBOX_VERSION-win-arm64-setup.exe"\n          )\n          foreach ($installer in $installers) {\n            & $signtool.FullName sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com /f $env:SIGNING_CERTIFICATE_PATH /p $env:SIGNING_CERTIFICATE_PASSWORD $installer\n            if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $installer" }\n          }\n'''
if text.count(old_sign) != 1:
    raise RuntimeError('installer signing block not found')
write(p, text.replace(old_sign, new_sign, 1))

# Regression tests.
write('tests/DevBox.Tests/PostMergeAuditRound2Tests.cs', r'''using System.Security.Cryptography;
using System.Text.Json;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class PostMergeAuditRound2Tests
{
    [Fact]
    public async Task CrossProcessFileLock_RejectsSecondOwnerUntilReleased()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "resource.lock");
            using var first = CrossProcessFileLock.Acquire(path, TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<TimeoutException>(() =>
                CrossProcessFileLock.AcquireAsync(path, CancellationToken.None, TimeSpan.FromMilliseconds(100)));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task EnvironmentLock_SiteCollision_RollsBackManifestActionsAndSite()
    {
        var root = NewRoot();
        try
        {
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            _ = workspace.Create(new ProjectCreateRequest("app", "app.test", ProjectKind.Php, Https: false, DatabaseEngine: "none", Addons: Array.Empty<string>()));
            _ = workspace.Create(new ProjectCreateRequest("other", "other.test", ProjectKind.Php, Https: false, DatabaseEngine: "none", Addons: Array.Empty<string>()));
            var project = Path.Combine(root, "www", "app");
            var actions = new ProjectActionService(root, workspace);
            actions.SetActions(project, new[] { new ProjectActionDefinition("before", "Before", "php", new[] { "-v" }) });

            var desired = new EnvironmentLockFile
            {
                ProjectName = "app",
                Domain = "other.test",
                Database = new EnvironmentDatabasePin("none", null, null),
                Actions = new[] { new ProjectActionDefinition("after", "After", "php", new[] { "-m" }) }
            };
            File.WriteAllText(Path.Combine(project, EnvironmentLockService.LockFileName), JsonSerializer.Serialize(desired));

            using var service = new EnvironmentLockService(root);
            await Assert.ThrowsAnyAsync<Exception>(() => service.ApplyLockAsync(project));

            Assert.Equal("app.test", workspace.LoadManifest(project)!.Domain);
            Assert.Equal("app.test", sites.GetSites().Single(item => item.Name == "app").Domain);
            Assert.Equal("before", actions.GetActions(project).Single().Key);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void LocalCertificateManager_Ensure_ReplacesWrongDomainMaterial()
    {
        var root = NewRoot();
        try
        {
            var manager = new LocalCertificateManager(root);
            var foo = manager.Ensure("foo.test");
            var sites = Path.Combine(root, "config", "ssl", "sites");
            File.Copy(foo.CertificatePath, Path.Combine(sites, "bar.test.crt.pem"));
            File.Copy(foo.PrivateKeyPath, Path.Combine(sites, "bar.test.key.pem"));

            Assert.False(manager.IsMaterialValid("bar.test"));
            var bar = manager.Ensure("bar.test");
            Assert.True(manager.IsMaterialValid("bar.test"));
            Assert.NotEqual(foo.Thumbprint, bar.Thumbprint);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void EnvironmentLock_DriftDetectsExistingButInvalidTlsMaterial()
    {
        var root = NewRoot();
        try
        {
            var sites = new SiteManager(root);
            var certs = new LocalCertificateManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), certs);
            _ = workspace.Create(new ProjectCreateRequest("app", "app.test", ProjectKind.Php, Https: false, DatabaseEngine: "none", Addons: Array.Empty<string>()));
            _ = sites.SetHttps("app", true);
            var certificate = certs.Ensure("app.test");
            using var wrongKey = RSA.Create(2048);
            File.WriteAllText(certificate.PrivateKeyPath, wrongKey.ExportPkcs8PrivateKeyPem());

            var project = Path.Combine(root, "www", "app");
            var desired = new EnvironmentLockFile
            {
                ProjectName = "app",
                Domain = "app.test",
                Https = true,
                Database = new EnvironmentDatabasePin("none", null, null)
            };
            File.WriteAllText(Path.Combine(project, EnvironmentLockService.LockFileName), JsonSerializer.Serialize(desired));
            using var service = new EnvironmentLockService(root);
            Assert.Contains(service.GetDrift(project), item => item.Contains("certificate material", StringComparison.OrdinalIgnoreCase));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task ProjectProvisioning_FailureAfterCreate_RollsBackSiteAndPreservesEmptyRoot()
    {
        var root = NewRoot();
        try
        {
            var projectRoot = Path.Combine(root, "www", "app");
            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(Path.Combine(root, "config", "services.json"));
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            var service = new ProjectProvisioningService(root, workspace, new DatabaseManager(root), new ManagedServiceCatalog(root));
            var request = new ProjectCreateRequest(
                "app", "app.test", ProjectKind.Php,
                Https: false,
                DatabaseEngine: "none",
                Addons: Array.Empty<string>(),
                Services: new[] { "mailpit" });

            await Assert.ThrowsAnyAsync<Exception>(() => service.ProvisionAsync(request));
            Assert.DoesNotContain(sites.GetSites(), item => item.Name == "app");
            Assert.True(Directory.Exists(projectRoot));
            Assert.Empty(Directory.EnumerateFileSystemEntries(projectRoot));
        }
        finally { TryDelete(root); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-audit2-" + Guid.NewGuid().ToString("N"));
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

print('Round 2 audit fixes applied.')
