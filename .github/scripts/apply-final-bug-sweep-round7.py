from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:180]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1) Environment profiles loaded from user JSON could dereference null collections,
# null database pins, null action entries or null action arguments and crash with NRE.
replace_once(
    "src/DevBox.Core/Services/EnvironmentProfileService.cs",
    '''        if (profile.Kind == ProjectKind.Unknown)\n            throw new InvalidDataException("Environment profile must use a supported project kind.");\n\n        foreach (var pair in profile.Runtimes)\n        {\n            if (!SafeKeyRegex().IsMatch(pair.Key) || !SafeVersionRegex().IsMatch(pair.Value))\n                throw new InvalidDataException($"Environment profile contains an invalid runtime pin: {pair.Key}={pair.Value}.");\n        }\n\n        var engine = profile.Database.Engine?.Trim().ToLowerInvariant();\n''',
    '''        if (profile.Kind == ProjectKind.Unknown)\n            throw new InvalidDataException("Environment profile must use a supported project kind.");\n        if (profile.Runtimes is null || profile.Database is null || profile.Addons is null || profile.Services is null || profile.Actions is null)\n            throw new InvalidDataException("Environment profile contains a null collection or database definition.");\n\n        foreach (var pair in profile.Runtimes)\n        {\n            if (!SafeKeyRegex().IsMatch(pair.Key ?? string.Empty) || !SafeVersionRegex().IsMatch(pair.Value ?? string.Empty))\n                throw new InvalidDataException($"Environment profile contains an invalid runtime pin: {pair.Key}={pair.Value}.");\n        }\n\n        var engine = profile.Database.Engine?.Trim().ToLowerInvariant();\n''')
replace_once(
    "src/DevBox.Core/Services/EnvironmentProfileService.cs",
    '''        if (profile.Addons.Any(value => !SafeKeyRegex().IsMatch(value)) || profile.Services.Any(value => !SafeKeyRegex().IsMatch(value)))\n            throw new InvalidDataException("Environment profile contains an invalid addon or service key.");\n\n        foreach (var action in profile.Actions)\n        {\n            if (!SafeKeyRegex().IsMatch(action.Key) || string.IsNullOrWhiteSpace(action.DisplayName))\n                throw new InvalidDataException("Environment profile contains an invalid project action.");\n            if (action.TimeoutSeconds is < 1 or > 3600)\n                throw new InvalidDataException($"Action '{action.Key}' timeout must be between 1 and 3600 seconds.");\n        }\n''',
    '''        if (profile.Addons.Any(value => !SafeKeyRegex().IsMatch(value ?? string.Empty)) || profile.Services.Any(value => !SafeKeyRegex().IsMatch(value ?? string.Empty)))\n            throw new InvalidDataException("Environment profile contains an invalid addon or service key.");\n\n        foreach (var action in profile.Actions)\n        {\n            if (action is null || !SafeKeyRegex().IsMatch(action.Key ?? string.Empty) || string.IsNullOrWhiteSpace(action.DisplayName))\n                throw new InvalidDataException("Environment profile contains an invalid project action.");\n            if (string.IsNullOrWhiteSpace(action.Executable) || action.Arguments is null ||\n                action.Arguments.Any(value => value is null || value.Length > 2048 || value.Contains('\\0')))\n                throw new InvalidDataException($"Action '{action.Key}' contains an invalid executable or arguments.");\n            if (action.TimeoutSeconds is < 1 or > 3600)\n                throw new InvalidDataException($"Action '{action.Key}' timeout must be between 1 and 3600 seconds.");\n        }\n''')

# 2) Project action JSON had the same null-entry/null-argument crash path.
replace_once(
    "src/DevBox.Core/Services/ProjectActionService.cs",
    '''            var actions = actionsNode.Deserialize<List<ProjectActionDefinition>>(JsonOptions) ?? new List<ProjectActionDefinition>();\n            foreach (var action in actions)\n                ValidateAction(action);\n            return actions.Where(item => item.Enabled).ToArray();\n''',
    '''            var actions = actionsNode.Deserialize<List<ProjectActionDefinition?>>(JsonOptions) ?? new List<ProjectActionDefinition?>();\n            foreach (var action in actions)\n                ValidateAction(action);\n            return actions.Select(item => item!).Where(item => item.Enabled).ToArray();\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectActionService.cs",
    '''    private static void ValidateAction(ProjectActionDefinition action)\n    {\n        ArgumentNullException.ThrowIfNull(action);\n        if (string.IsNullOrWhiteSpace(action.Key) || action.Key.Length > 64 || action.Key.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_' and not '.'))\n            throw new InvalidDataException("Project action key contains unsupported characters.");\n        if (string.IsNullOrWhiteSpace(action.DisplayName) || action.DisplayName.Length > 120)\n            throw new InvalidDataException($"Project action '{action.Key}' display name is invalid.");\n        if (!AllowedTools.Contains(action.Executable))\n            throw new InvalidDataException($"Project action '{action.Key}' uses unsupported tool '{action.Executable}'. Allowed tools: {string.Join(", ", AllowedTools.OrderBy(value => value))}.");\n        if (action.Arguments.Count > 64 || action.Arguments.Any(value => value.Length > 2048 || value.Contains('\\0')))\n            throw new InvalidDataException($"Project action '{action.Key}' contains invalid arguments.");\n''',
    '''    private static void ValidateAction(ProjectActionDefinition? action)\n    {\n        if (action is null)\n            throw new InvalidDataException("Project actions cannot contain null entries.");\n        if (string.IsNullOrWhiteSpace(action.Key) || action.Key.Length > 64 || action.Key.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_' and not '.'))\n            throw new InvalidDataException("Project action key contains unsupported characters.");\n        if (string.IsNullOrWhiteSpace(action.DisplayName) || action.DisplayName.Length > 120)\n            throw new InvalidDataException($"Project action '{action.Key}' display name is invalid.");\n        if (!AllowedTools.Contains(action.Executable ?? string.Empty))\n            throw new InvalidDataException($"Project action '{action.Key}' uses unsupported tool '{action.Executable}'. Allowed tools: {string.Join(", ", AllowedTools.OrderBy(value => value))}.");\n        if (action.Arguments is null || action.Arguments.Count > 64 || action.Arguments.Any(value => value is null || value.Length > 2048 || value.Contains('\\0')))\n            throw new InvalidDataException($"Project action '{action.Key}' contains invalid arguments.");\n''')

# 3) Managed-service manifests could crash on null JSON fields and could steal the
# PID namespace or dynamic port range used by database/PHP processes.
replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''    private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)\n    {\n        "nginx", "php", "mysql"\n    };\n''',
    '''    private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)\n    {\n        "nginx", "php", "mysql"\n    };\n    private static readonly string[] ReservedKeyPrefixes = ["db-", "php-pool-"];\n''')
replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''            var manifests = JsonSerializer.Deserialize<List<ManagedServiceManifest>>(File.ReadAllText(_manifestPath), JsonOptions)\n                ?? new List<ManagedServiceManifest>();\n            ValidateAll(manifests);\n            return manifests.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();\n''',
    '''            var manifests = JsonSerializer.Deserialize<List<ManagedServiceManifest?>>(File.ReadAllText(_manifestPath), JsonOptions)\n                ?? new List<ManagedServiceManifest?>();\n            if (manifests.Any(item => item is null))\n                throw new InvalidDataException("config/services.json contains a null managed-service entry.");\n            var materialized = manifests.Select(item => item!).ToArray();\n            ValidateAll(materialized);\n            return materialized.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();\n''')
replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''        if (!SafeKeyRegex().IsMatch(manifest.Key) || ReservedKeys.Contains(manifest.Key))\n        {\n            throw new InvalidDataException($"Managed service key '{manifest.Key}' is invalid or reserved.");\n        }\n''',
    '''        if (!SafeKeyRegex().IsMatch(manifest.Key ?? string.Empty) || ReservedKeys.Contains(manifest.Key) ||\n            ReservedKeyPrefixes.Any(prefix => manifest.Key?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true))\n        {\n            throw new InvalidDataException($"Managed service key '{manifest.Key}' is invalid or reserved.");\n        }\n''')
replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''        if (manifest.Port is < 1 or > 65535 || IsReservedCorePort(manifest.Port))\n        {\n            throw new InvalidDataException($"Managed service port {manifest.Port} is invalid or reserved by a core DevBox service.");\n        }\n''',
    '''        if (manifest.Port is < 1 or > 65535 || IsReservedCorePort(manifest.Port))\n        {\n            throw new InvalidDataException($"Managed service port {manifest.Port} is invalid or reserved by a core DevBox service.");\n        }\n''')
replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''        if (manifest.Arguments.Count > 64 || manifest.Arguments.Any(argument => argument.Contains('\\0')))\n        {\n            throw new InvalidDataException("Managed service arguments are invalid.");\n        }\n        if (manifest.StopArguments is { Count: > 64 } || manifest.StopArguments?.Any(argument => argument.Contains('\\0')) == true)\n''',
    '''        if (string.IsNullOrWhiteSpace(manifest.ExecutableRelativePath) || string.IsNullOrWhiteSpace(manifest.WorkingDirectoryRelativePath) ||\n            string.IsNullOrWhiteSpace(manifest.Version) || manifest.Arguments is null || manifest.Arguments.Count > 64 ||\n            manifest.Arguments.Any(argument => argument is null || argument.Contains('\\0')))\n        {\n            throw new InvalidDataException("Managed service executable, working directory, version or arguments are invalid.");\n        }\n        if (manifest.StopArguments is { Count: > 64 } || manifest.StopArguments?.Any(argument => argument is null || argument.Contains('\\0')) == true)\n''')
replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''        if (port is 80 or 443 or 3306 or 3316 or 5432 or 9084)\n            return true;\n''',
    '''        if (port is 80 or 443 or 3306 or 3316 or 5432 or 9084 || port is >= 20000 and <= 49999)\n            return true;\n''')

# 4) TaskChanged subscribers run outside the task operation and must not be able to
# fail Enqueue/ExecuteAsync. Isolate subscriber exceptions and trace them.
replace_once(
    "src/DevBox.Core/Services/PlatformTaskCenter.cs",
    '''using System.Collections.Concurrent;\nusing System.Text.Json;\n''',
    '''using System.Collections.Concurrent;\nusing System.Diagnostics;\nusing System.Text.Json;\n''')
replace_once(
    "src/DevBox.Core/Services/PlatformTaskCenter.cs",
    '''    private void Publish(PlatformTaskSnapshot snapshot, bool persist)\n    {\n        TaskChanged?.Invoke(this, snapshot);\n        if (persist)\n            PersistHistory();\n    }\n''',
    '''    private void Publish(PlatformTaskSnapshot snapshot, bool persist)\n    {\n        if (TaskChanged is not null)\n        {\n            foreach (EventHandler<PlatformTaskSnapshot> handler in TaskChanged.GetInvocationList())\n            {\n                try\n                {\n                    handler(this, snapshot);\n                }\n                catch (Exception ex)\n                {\n                    Trace.TraceError($"PlatformTaskCenter TaskChanged subscriber failed: {ex}");\n                }\n            }\n        }\n        if (persist)\n            PersistHistory();\n    }\n''')

# 5) Default backup/export names had only second precision and overwrite semantics,
# so concurrent/repeated operations in the same second could silently replace data.
replace_once(
    "src/DevBox.Core/Services/DatabaseManager.cs",
    '''        var fileName = $"{databaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.sql";\n''',
    '''        var fileName = $"{databaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sql";\n''')
replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''            ? Path.Combine(backupRoot, $"{databaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}{extension}")\n''',
    '''            ? Path.Combine(backupRoot, $"{databaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}")\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''            ? Path.Combine(_exportRoot, $"{SafeFileName(projectName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.devbox-project.zip")\n''',
    '''            ? Path.Combine(_exportRoot, $"{SafeFileName(projectName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.devbox-project.zip")\n''')
replace_once(
    "src/DevBox.Core/Services/RemoteEnvironmentService.cs",
    '''            ? Path.Combine(_shareRoot, $"{SafeFileName(name)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.devbox-env.json")\n''',
    '''            ? Path.Combine(_shareRoot, $"{SafeFileName(name)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.devbox-env.json")\n''')

# 6) Database runtime registration JSON accepted duplicate engine/version identities,
# null entries and semantic invalid values that later collide on the same service key.
replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''            var values = JsonSerializer.Deserialize<List<DatabaseRuntimeRegistration>>(File.ReadAllText(_registrationsPath), JsonOptions) ?? [];\n            foreach (var item in values)\n            {\n                _ = ParseEngine(item.Engine);\n                ValidateVersion(item.Version);\n                if (item.Port is < 1 or > 65535)\n                    throw new InvalidDataException("Database runtime registration contains an invalid port.");\n            }\n            var duplicatePort = values.GroupBy(item => item.Port).FirstOrDefault(group => group.Count() > 1);\n''',
    '''            var values = JsonSerializer.Deserialize<List<DatabaseRuntimeRegistration?>>(File.ReadAllText(_registrationsPath), JsonOptions) ?? [];\n            if (values.Any(item => item is null))\n                throw new InvalidDataException("Database runtime registrations contain a null entry.");\n            var materialized = values.Select(item => item!).ToArray();\n            foreach (var item in materialized)\n            {\n                try\n                {\n                    _ = ParseEngine(item.Engine);\n                    ValidateVersion(item.Version);\n                }\n                catch (ArgumentException ex)\n                {\n                    throw new InvalidDataException("Database runtime registration contains an invalid engine or version.", ex);\n                }\n                if (item.Port is < 1 or > 65535)\n                    throw new InvalidDataException("Database runtime registration contains an invalid port.");\n            }\n            var duplicateIdentity = materialized\n                .GroupBy(item => $"{item.Engine}|{item.Version}", StringComparer.OrdinalIgnoreCase)\n                .FirstOrDefault(group => group.Count() > 1);\n            if (duplicateIdentity is not null)\n                throw new InvalidDataException($"Database runtime registrations contain duplicate runtime '{duplicateIdentity.Key}'.");\n            var duplicatePort = materialized.GroupBy(item => item.Port).FirstOrDefault(group => group.Count() > 1);\n''')
replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''            return values;\n        }\n        catch (JsonException ex)\n''',
    '''            return materialized;\n        }\n        catch (JsonException ex)\n''')

# Regression tests for malformed state, namespace collisions, event isolation and
# auto-generated file-name uniqueness.
tests = Path("tests/DevBox.Tests/FinalBugSweepRound7Tests.cs")
tests.write_text(r'''using System.Reflection;
using System.Text.Json;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound7Tests
{
    [Fact]
    public void EnvironmentProfile_NullCollectionsAreControlledInvalidData()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "environment-profiles.json"), """
            [{"Key":"broken","DisplayName":"Broken","Kind":"EmptyPhp","Runtimes":null,"Database":null,"Addons":null,"Services":null,"Actions":null}]
            """);
            Assert.Throws<InvalidDataException>(() => new EnvironmentProfileService(root).GetProfiles());
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void ProjectActions_NullEntryAndArgumentsAreControlledInvalidData()
    {
        var root = NewRoot();
        try
        {
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            _ = workspace.Create(new ProjectCreateRequest("app", "app.test", ProjectKind.EmptyPhp, Https: false, DatabaseEngine: "none", Addons: Array.Empty<string>()));
            var project = Path.Combine(root, "www", "app");
            var service = new ProjectActionService(root, workspace);

            File.WriteAllText(Path.Combine(project, ProjectWorkspaceService.ManifestFileName), """
            {"Name":"app","Domain":"app.test","Kind":"EmptyPhp","Actions":[null]}
            """);
            Assert.Throws<InvalidDataException>(() => service.GetActions(project));

            File.WriteAllText(Path.Combine(project, ProjectWorkspaceService.ManifestFileName), """
            {"Name":"app","Domain":"app.test","Kind":"EmptyPhp","Actions":[{"Key":"bad","DisplayName":"Bad","Executable":"php","Arguments":null}]}
            """);
            Assert.Throws<InvalidDataException>(() => service.GetActions(project));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void ManagedServices_RejectNullStateReservedPidPrefixesAndPhpPoolPorts()
    {
        var root = NewRoot();
        try
        {
            var config = Path.Combine(root, "config", "services.json");
            File.WriteAllText(config, "[null]");
            Assert.Throws<InvalidDataException>(() => new ManagedServiceCatalog(root).GetManifests());

            File.Delete(config);
            var catalog = new ManagedServiceCatalog(root);
            var pidCollision = new ManagedServiceManifest(1, "php-pool-8.5.10", "Collision", "runtime/x/tool.exe", [], ".", 15000, "1");
            Assert.Throws<InvalidDataException>(() => catalog.Upsert(pidCollision));

            var dbPidCollision = new ManagedServiceManifest(1, "db-mysql-8.4.11", "Collision", "runtime/x/tool.exe", [], ".", 15001, "1");
            Assert.Throws<InvalidDataException>(() => catalog.Upsert(dbPidCollision));

            var portCollision = new ManagedServiceManifest(1, "custom", "Collision", "runtime/x/tool.exe", [], ".", PhpRuntimePoolManager.GetPort("8.5.10"), "1");
            Assert.Throws<InvalidDataException>(() => catalog.Upsert(portCollision));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task TaskCenter_ThrowingSubscriberCannotFailTaskExecution()
    {
        var root = NewRoot();
        try
        {
            using var center = new PlatformTaskCenter(root);
            center.TaskChanged += (_, _) => throw new InvalidOperationException("subscriber failure");
            var id = center.Enqueue("fixture", (_, _) => Task.CompletedTask);
            var result = await center.WaitAsync(id);
            Assert.Equal(PlatformTaskState.Completed, result.State);
            Assert.Equal(100, result.Progress);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task AutoGeneratedExportsAreUniqueWithinSameSecond()
    {
        var root = NewRoot();
        try
        {
            var remote = new RemoteEnvironmentService(root);
            var env1 = remote.ExportProfile("php-minimal");
            var env2 = remote.ExportProfile("php-minimal");
            Assert.NotEqual(env1, env2);
            Assert.True(File.Exists(env1));
            Assert.True(File.Exists(env2));

            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            _ = workspace.Create(new ProjectCreateRequest("app", "app.test", ProjectKind.EmptyPhp, Https: false, DatabaseEngine: "none", Addons: Array.Empty<string>()));
            var transfer = new ProjectTransferService(root);
            var first = await transfer.ExportAsync(Path.Combine(root, "www", "app"));
            var second = await transfer.ExportAsync(Path.Combine(root, "www", "app"));
            Assert.NotEqual(first.ArchivePath, second.ArchivePath);
            Assert.True(File.Exists(first.ArchivePath));
            Assert.True(File.Exists(second.ArchivePath));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void DatabaseManager_DefaultBackupPathIsUniqueWithinSameSecond()
    {
        var root = NewRoot();
        try
        {
            var manager = new DatabaseManager(root);
            var method = typeof(DatabaseManager).GetMethod("ResolveBackupPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var first = (string)method.Invoke(manager, ["app", null])!;
            var second = (string)method.Invoke(manager, ["app", null])!;
            Assert.NotEqual(first, second);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void DatabaseRuntimeRegistrations_RejectDuplicateIdentityAndNullEntries()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "config", "database-runtimes.json");
            File.WriteAllText(path, "[null]");
            using (var service = new DatabaseRuntimeService(root))
                Assert.Throws<InvalidDataException>(() => service.GetInstances());

            File.WriteAllText(path, """
            [
              {"Engine":"mysql","Version":"8.4.11","Port":3401},
              {"Engine":"MYSQL","Version":"8.4.11","Port":3402}
            ]
            """);
            using (var service = new DatabaseRuntimeService(root))
                Assert.Throws<InvalidDataException>(() => service.GetInstances());
        }
        finally { TryDelete(root); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-final-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "www"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        return root;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
''', encoding="utf-8")
