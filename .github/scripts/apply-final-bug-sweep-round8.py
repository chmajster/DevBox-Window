from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:180]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1) AsyncRelayCommand/RelayCommand event subscribers must not break command state
# or fault fire-and-forget ICommand execution.
replace_once(
    "src/DevBox.App/ViewModels/RelayCommand.cs",
    '''    public event EventHandler? CanExecuteChanged;\n\n    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);\n}\n''',
    '''    public event EventHandler? CanExecuteChanged;\n\n    public void RaiseCanExecuteChanged()\n    {\n        if (CanExecuteChanged is null)\n            return;\n        foreach (EventHandler handler in CanExecuteChanged.GetInvocationList())\n        {\n            try\n            {\n                handler(this, EventArgs.Empty);\n            }\n            catch (Exception ex)\n            {\n                Trace.TraceError($"RelayCommand CanExecuteChanged subscriber failed: {ex}");\n            }\n        }\n    }\n}\n''')
replace_once(
    "src/DevBox.App/ViewModels/RelayCommand.cs",
    '''        catch (Exception ex)\n        {\n            Trace.TraceError($"Unhandled asynchronous command exception: {ex}");\n            ExecutionFailed?.Invoke(ex);\n        }\n''',
    '''        catch (Exception ex)\n        {\n            Trace.TraceError($"Unhandled asynchronous command exception: {ex}");\n            RaiseExecutionFailed(ex);\n        }\n''')
replace_once(
    "src/DevBox.App/ViewModels/RelayCommand.cs",
    '''    public event EventHandler? CanExecuteChanged;\n    public event Action<Exception>? ExecutionFailed;\n\n    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);\n}\n''',
    '''    public event EventHandler? CanExecuteChanged;\n    public event Action<Exception>? ExecutionFailed;\n\n    public void RaiseCanExecuteChanged()\n    {\n        if (CanExecuteChanged is null)\n            return;\n        foreach (EventHandler handler in CanExecuteChanged.GetInvocationList())\n        {\n            try\n            {\n                handler(this, EventArgs.Empty);\n            }\n            catch (Exception ex)\n            {\n                Trace.TraceError($"AsyncRelayCommand CanExecuteChanged subscriber failed: {ex}");\n            }\n        }\n    }\n\n    private void RaiseExecutionFailed(Exception exception)\n    {\n        if (ExecutionFailed is null)\n            return;\n        foreach (Action<Exception> handler in ExecutionFailed.GetInvocationList())\n        {\n            try\n            {\n                handler(exception);\n            }\n            catch (Exception ex)\n            {\n                Trace.TraceError($"AsyncRelayCommand ExecutionFailed subscriber failed: {ex}");\n            }\n        }\n    }\n}\n''')

# 2) Duplicate project action keys are ambiguous. RunAllAsync resolves by key, so
# duplicates previously executed the first action repeatedly and skipped later definitions.
replace_once(
    "src/DevBox.Core/Services/ProjectActionService.cs",
    '''            var actions = actionsNode.Deserialize<List<ProjectActionDefinition?>>(JsonOptions) ?? new List<ProjectActionDefinition?>();\n            foreach (var action in actions)\n                ValidateAction(action);\n            return actions.Select(item => item!).Where(item => item.Enabled).ToArray();\n''',
    '''            var actions = actionsNode.Deserialize<List<ProjectActionDefinition?>>(JsonOptions) ?? new List<ProjectActionDefinition?>();\n            ValidateActions(actions);\n            return actions.Select(item => item!).Where(item => item.Enabled).ToArray();\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectActionService.cs",
    '''        foreach (var action in actions)\n            ValidateAction(action);\n\n        JsonObject manifest;\n''',
    '''        ValidateActions(actions);\n\n        JsonObject manifest;\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectActionService.cs",
    '''    private static void ValidateAction(ProjectActionDefinition? action)\n    {\n''',
    '''    private static void ValidateActions(IEnumerable<ProjectActionDefinition?> actions)\n    {\n        var materialized = actions.ToArray();\n        foreach (var action in materialized)\n            ValidateAction(action);\n        var duplicate = materialized\n            .Where(action => action is not null)\n            .GroupBy(action => action!.Key, StringComparer.OrdinalIgnoreCase)\n            .FirstOrDefault(group => group.Count() > 1);\n        if (duplicate is not null)\n            throw new InvalidDataException($"Project actions contain duplicate key '{duplicate.Key}'.");\n    }\n\n    private static void ValidateAction(ProjectActionDefinition? action)\n    {\n''')

# 3) User-editable catalogs must reject duplicate identities instead of silently
# accepting state that Save/Remove/Get later interpret inconsistently.
replace_once(
    "src/DevBox.Core/Services/EnvironmentProfileService.cs",
    '''            foreach (var profile in materialized)\n                Validate(profile);\n            return materialized.Select(Normalize).ToArray();\n''',
    '''            foreach (var profile in materialized)\n                Validate(profile);\n            var duplicate = materialized.GroupBy(profile => profile.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);\n            if (duplicate is not null)\n                throw new InvalidDataException($"config/environment-profiles.json contains duplicate profile key '{duplicate.Key}'.");\n            return materialized.Select(Normalize).ToArray();\n''')
replace_once(
    "src/DevBox.Core/Services/EnvironmentProfileService.cs",
    '''        foreach (var action in profile.Actions)\n        {\n            if (action is null || !SafeKeyRegex().IsMatch(action.Key ?? string.Empty) || string.IsNullOrWhiteSpace(action.DisplayName))\n                throw new InvalidDataException("Environment profile contains an invalid project action.");\n            if (string.IsNullOrWhiteSpace(action.Executable) || action.Arguments is null ||\n                action.Arguments.Any(value => value is null || value.Length > 2048 || value.Contains('\\0')))\n                throw new InvalidDataException($"Action '{action.Key}' contains an invalid executable or arguments.");\n            if (action.TimeoutSeconds is < 1 or > 3600)\n                throw new InvalidDataException($"Action '{action.Key}' timeout must be between 1 and 3600 seconds.");\n        }\n''',
    '''        foreach (var action in profile.Actions)\n        {\n            if (action is null || !SafeKeyRegex().IsMatch(action.Key ?? string.Empty) || string.IsNullOrWhiteSpace(action.DisplayName))\n                throw new InvalidDataException("Environment profile contains an invalid project action.");\n            if (string.IsNullOrWhiteSpace(action.Executable) || action.Arguments is null ||\n                action.Arguments.Any(value => value is null || value.Length > 2048 || value.Contains('\\0')))\n                throw new InvalidDataException($"Action '{action.Key}' contains an invalid executable or arguments.");\n            if (action.TimeoutSeconds is < 1 or > 3600)\n                throw new InvalidDataException($"Action '{action.Key}' timeout must be between 1 and 3600 seconds.");\n        }\n        var duplicateAction = profile.Actions\n            .Where(action => action is not null)\n            .GroupBy(action => action!.Key, StringComparer.OrdinalIgnoreCase)\n            .FirstOrDefault(group => group.Count() > 1);\n        if (duplicateAction is not null)\n            throw new InvalidDataException($"Environment profile contains duplicate action key '{duplicateAction.Key}'.");\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectStackProfileService.cs",
    '''            var materialized = profiles.Select(profile => profile!).ToArray();\n            foreach (var profile in materialized) Validate(profile);\n            return materialized;\n''',
    '''            var materialized = profiles.Select(profile => profile!).ToArray();\n            foreach (var profile in materialized) Validate(profile);\n            var duplicate = materialized.GroupBy(profile => profile.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);\n            if (duplicate is not null)\n                throw new InvalidDataException($"config/project-profiles.json contains duplicate profile key '{duplicate.Key}'.");\n            return materialized;\n''')
replace_once(
    "src/DevBox.Core/Services/RuntimePlatformService.cs",
    '''            var materialized = packages.Select(package => package!).ToArray();\n            foreach (var package in materialized)\n                ValidatePackage(package);\n            return materialized;\n''',
    '''            var materialized = packages.Select(package => package!).ToArray();\n            foreach (var package in materialized)\n                ValidatePackage(package);\n            var duplicate = materialized\n                .GroupBy(package => $"{package.Key}|{package.Version}|{package.Architecture}", StringComparer.OrdinalIgnoreCase)\n                .FirstOrDefault(group => group.Count() > 1);\n            if (duplicate is not null)\n                throw new InvalidDataException($"config/runtime-catalog.json contains duplicate runtime identity '{duplicate.Key}'.");\n            return materialized;\n''')

# 4) ADDONS entries with different keys could still share an install directory or
# local domain, causing one addon to overwrite/uninstall or route traffic for another.
replace_once(
    "src/DevBox.Core/Services/AddonCatalog.cs",
    '''            var duplicate = entries\n                .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)\n                .FirstOrDefault(group => group.Count() > 1);\n            if (duplicate is not null)\n            {\n                throw new InvalidDataException($"Addon manifest contains duplicate key '{duplicate.Key}'.");\n            }\n\n            return entries.Select(ToDefinition).ToArray();\n''',
    '''            var duplicate = entries\n                .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)\n                .FirstOrDefault(group => group.Count() > 1);\n            if (duplicate is not null)\n            {\n                throw new InvalidDataException($"Addon manifest contains duplicate key '{duplicate.Key}'.");\n            }\n            var duplicateInstallPath = entries\n                .GroupBy(entry => ResolveRelativePath(entry.InstallRelativePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)\n                .FirstOrDefault(group => group.Count() > 1);\n            if (duplicateInstallPath is not null)\n                throw new InvalidDataException("Addon manifest assigns the same install directory to multiple addons.");\n            var duplicateDomain = entries\n                .GroupBy(entry => new Uri(entry.LocalUrl).Host, StringComparer.OrdinalIgnoreCase)\n                .FirstOrDefault(group => group.Count() > 1);\n            if (duplicateDomain is not null)\n                throw new InvalidDataException($"Addon manifest assigns local domain '{duplicateDomain.Key}' to multiple addons.");\n\n            return entries.Select(ToDefinition).ToArray();\n''')
replace_once(
    "src/DevBox.Core/Services/AddonCatalog.cs",
    '''            if (!File.Exists(_catalogPath))\n            {\n                File.Move(tempPath, _catalogPath);\n            }\n''',
    '''            if (!File.Exists(_catalogPath))\n            {\n                try\n                {\n                    File.Move(tempPath, _catalogPath);\n                }\n                catch (IOException) when (File.Exists(_catalogPath))\n                {\n                    // Another process initialized the default catalog first.\n                }\n            }\n''')

# 5) Marketplace key values of the wrong JSON type previously leaked an
# InvalidOperationException from GetValue<string>() instead of controlled invalid data.
replace_once(
    "src/DevBox.Core/Services/AddonMarketplaceService.cs",
    '''                var keyNode = item.FirstOrDefault(pair => pair.Key.Equals("key", StringComparison.OrdinalIgnoreCase)).Value;\n                var key = keyNode?.GetValue<string>();\n                if (string.IsNullOrWhiteSpace(key))\n                    throw new InvalidDataException("ADDONS catalog entry is missing key.");\n                byKey[key] = item.DeepClone() as JsonObject ?? throw new InvalidDataException("Unable to clone ADDONS catalog entry.");\n''',
    '''                var keyNode = item.FirstOrDefault(pair => pair.Key.Equals("key", StringComparison.OrdinalIgnoreCase)).Value;\n                string? key = null;\n                if (keyNode is JsonValue keyValue && keyValue.TryGetValue<string>(out var parsedKey))\n                    key = parsedKey;\n                if (string.IsNullOrWhiteSpace(key))\n                    throw new InvalidDataException("ADDONS catalog entry has a missing or non-string key.");\n                byKey[key] = item.DeepClone() as JsonObject ?? throw new InvalidDataException("Unable to clone ADDONS catalog entry.");\n''')

# 6) Duplicate task IDs in persisted history silently overwrote earlier entries when
# loaded into the dictionary. Treat this as corrupted history and quarantine it.
replace_once(
    "src/DevBox.Core/Services/PlatformTaskCenter.cs",
    '''            foreach (var item in materialized)\n            {\n                if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 160 ||\n                    !Enum.IsDefined(typeof(PlatformTaskState), item.State) || !double.IsFinite(item.Progress) ||\n                    item.Progress is < 0 or > 100)\n                {\n                    throw new InvalidDataException("Task Center history contains an invalid entry.");\n                }\n            }\n\n            return materialized.Select(item => item.State is PlatformTaskState.Queued or PlatformTaskState.Running\n''',
    '''            foreach (var item in materialized)\n            {\n                if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 160 ||\n                    !Enum.IsDefined(typeof(PlatformTaskState), item.State) || !double.IsFinite(item.Progress) ||\n                    item.Progress is < 0 or > 100)\n                {\n                    throw new InvalidDataException("Task Center history contains an invalid entry.");\n                }\n            }\n            var duplicateId = materialized.GroupBy(item => item.Id).FirstOrDefault(group => group.Count() > 1);\n            if (duplicateId is not null)\n                throw new InvalidDataException($"Task Center history contains duplicate task id '{duplicateId.Key}'.");\n\n            return materialized.Select(item => item.State is PlatformTaskState.Queued or PlatformTaskState.Running\n''')

# 7) Managed-service DB port reservation used only Port/port exact spellings while
# DatabaseRuntimeService deserializes property names case-insensitively. "PORT" could
# therefore bypass the collision check. Invalid DB config shape was also ignored.
replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''            using var document = JsonDocument.Parse(File.ReadAllText(databaseRegistrations));\n            if (document.RootElement.ValueKind != JsonValueKind.Array)\n                return false;\n            foreach (var item in document.RootElement.EnumerateArray())\n            {\n                if ((item.TryGetProperty("Port", out var value) || item.TryGetProperty("port", out value)) &&\n                    value.TryGetInt32(out var registeredPort) && registeredPort == port)\n                    return true;\n            }\n            return false;\n''',
    '''            using var document = JsonDocument.Parse(File.ReadAllText(databaseRegistrations));\n            if (document.RootElement.ValueKind != JsonValueKind.Array)\n                throw new InvalidDataException("config/database-runtimes.json root must be a JSON array.");\n            foreach (var item in document.RootElement.EnumerateArray())\n            {\n                if (item.ValueKind != JsonValueKind.Object)\n                    throw new InvalidDataException("config/database-runtimes.json contains a non-object entry.");\n                JsonElement? portValue = null;\n                foreach (var property in item.EnumerateObject())\n                {\n                    if (property.Name.Equals("port", StringComparison.OrdinalIgnoreCase))\n                    {\n                        portValue = property.Value;\n                        break;\n                    }\n                }\n                if (portValue is null || !portValue.Value.TryGetInt32(out var registeredPort) || registeredPort is < 1 or > 65535)\n                    throw new InvalidDataException("config/database-runtimes.json contains an invalid or missing port.");\n                if (registeredPort == port)\n                    return true;\n            }\n            return false;\n''')

# Tests for this round.
async_tests = Path("tests/DevBox.Tests/AsyncRelayCommandTests.cs")
text = async_tests.read_text(encoding="utf-8")
insert = r'''
    [Fact]
    public async Task ExecuteAsync_ThrowingEventSubscribers_DoNotFaultOrLeaveCommandDisabled()
    {
        var command = new AsyncRelayCommand(() => Task.FromException(new InvalidOperationException("action failed")));
        var canExecuteNotifications = 0;
        command.CanExecuteChanged += (_, _) => throw new InvalidOperationException("can-execute subscriber failed");
        command.CanExecuteChanged += (_, _) => canExecuteNotifications++;
        command.ExecutionFailed += _ => throw new InvalidOperationException("failure subscriber failed");

        await command.ExecuteAsync(null);

        Assert.True(command.CanExecute(null));
        Assert.Equal(2, canExecuteNotifications);
    }

    [Fact]
    public void RelayCommand_ThrowingCanExecuteSubscriber_DoesNotEscape()
    {
        var command = new RelayCommand(() => { });
        var observed = 0;
        command.CanExecuteChanged += (_, _) => throw new InvalidOperationException("subscriber failed");
        command.CanExecuteChanged += (_, _) => observed++;

        command.RaiseCanExecuteChanged();

        Assert.Equal(1, observed);
    }
'''
needle = "\n}\n"
if not text.endswith(needle):
    raise RuntimeError("AsyncRelayCommandTests.cs unexpected ending")
async_tests.write_text(text[:-len(needle)] + insert + needle, encoding="utf-8")

round_tests = Path("tests/DevBox.Tests/FinalBugSweepRound8Tests.cs")
round_tests.write_text(r'''using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound8Tests
{
    [Fact]
    public void ProjectActions_RejectDuplicateKeys()
    {
        var root = NewRoot();
        try
        {
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            _ = workspace.Create(new ProjectCreateRequest("app", "app.test", ProjectKind.EmptyPhp, Https: false, DatabaseEngine: "none", Addons: Array.Empty<string>()));
            var project = Path.Combine(root, "www", "app");
            File.WriteAllText(Path.Combine(project, ProjectWorkspaceService.ManifestFileName), """
            {"Name":"app","Domain":"app.test","Kind":"EmptyPhp","Actions":[
              {"Key":"build","DisplayName":"One","Executable":"php","Arguments":[],"TimeoutSeconds":60,"Enabled":true},
              {"Key":"BUILD","DisplayName":"Two","Executable":"php","Arguments":[],"TimeoutSeconds":60,"Enabled":true}
            ]}
            """);
            var service = new ProjectActionService(root, workspace);
            Assert.Throws<InvalidDataException>(() => service.GetActions(project));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void PersistedCatalogs_RejectDuplicateIdentities()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "project-profiles.json"), """
            [
              {"Key":"dup","DisplayName":"One","Kind":"EmptyPhp","PhpVersion":null,"NodeVersion":null,"DatabaseEngine":"none","Https":false,"Addons":[],"Services":[],"Description":"one"},
              {"Key":"DUP","DisplayName":"Two","Kind":"EmptyPhp","PhpVersion":null,"NodeVersion":null,"DatabaseEngine":"none","Https":false,"Addons":[],"Services":[],"Description":"two"}
            ]
            """);
            Assert.Throws<InvalidDataException>(() => new ProjectStackProfileService(root).GetProfiles());

            File.WriteAllText(Path.Combine(root, "config", "runtime-catalog.json"), """
            [
              {"Key":"tool","DisplayName":"One","Version":"1.0","Architecture":"x64","ExecutableRelativePath":"tool.exe"},
              {"Key":"TOOL","DisplayName":"Two","Version":"1.0","Architecture":"X64","ExecutableRelativePath":"other.exe"}
            ]
            """);
            using var runtimes = new RuntimePlatformService(root);
            Assert.Throws<InvalidDataException>(() => runtimes.GetCatalog());

            File.WriteAllText(Path.Combine(root, "config", "environment-profiles.json"), """
            [
              {"Key":"dup","DisplayName":"One","Kind":"EmptyPhp","Runtimes":{},"Database":{"Engine":"none","Version":null,"DatabaseName":null,"Port":3306},"Https":false,"Addons":[],"Services":[],"Actions":[]},
              {"Key":"DUP","DisplayName":"Two","Kind":"EmptyPhp","Runtimes":{},"Database":{"Engine":"none","Version":null,"DatabaseName":null,"Port":3306},"Https":false,"Addons":[],"Services":[],"Actions":[]}
            ]
            """);
            Assert.Throws<InvalidDataException>(() => new EnvironmentProfileService(root).GetProfiles());
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void EnvironmentProfile_RejectsDuplicateActionKeys()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "environment-profiles.json"), """
            [{"Key":"fixture","DisplayName":"Fixture","Kind":"EmptyPhp","Runtimes":{},"Database":{"Engine":"none","Port":3306},"Https":false,"Addons":[],"Services":[],"Actions":[
              {"Key":"build","DisplayName":"One","Executable":"php","Arguments":[],"TimeoutSeconds":60,"Enabled":true},
              {"Key":"BUILD","DisplayName":"Two","Executable":"php","Arguments":[],"TimeoutSeconds":60,"Enabled":true}
            ]}]
            """);
            Assert.Throws<InvalidDataException>(() => new EnvironmentProfileService(root).GetProfiles());
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void AddonCatalog_RejectsSharedInstallPathAndDomain()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "config", "addons.json");
            File.WriteAllText(path, CatalogJson("www/shared", "one.test", "www/shared", "two.test"));
            Assert.Throws<InvalidDataException>(() => new AddonCatalog(root).GetAddons());

            File.WriteAllText(path, CatalogJson("www/one", "same.test", "www/two", "same.test"));
            Assert.Throws<InvalidDataException>(() => new AddonCatalog(root).GetAddons());
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void AddonMarketplace_NonStringKey_IsControlledInvalidData()
    {
        var method = typeof(AddonMarketplaceService).GetMethod("MergeCatalogs", BindingFlags.Static | BindingFlags.NonPublic)!;
        var local = new JsonArray();
        var marketplace = JsonNode.Parse("[{\"key\":123}]")!.AsArray();
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [local, marketplace]));
        Assert.IsType<InvalidDataException>(error.InnerException);
    }

    [Fact]
    public void TaskCenter_DuplicateHistoryIds_AreQuarantined()
    {
        var root = NewRoot();
        try
        {
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var items = new[]
            {
                new PlatformTaskSnapshot(id, "one", PlatformTaskState.Completed, 100, "done", now, now, now, null),
                new PlatformTaskSnapshot(id, "two", PlatformTaskState.Completed, 100, "done", now, now, now, null)
            };
            var path = Path.Combine(root, "logs", "task-center-history.json");
            File.WriteAllText(path, JsonSerializer.Serialize(items));
            using var center = new PlatformTaskCenter(root);
            Assert.Empty(center.GetTasks());
            Assert.False(File.Exists(path));
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "task-center-history.json.invalid-*.bak"));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void ManagedService_PortReservationReadsDatabasePortCaseInsensitively()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "database-runtimes.json"), """
            [{"Engine":"mysql","Version":"8.4.11","PORT":3400}]
            """);
            var catalog = new ManagedServiceCatalog(root);
            var manifest = new ManagedServiceManifest(1, "fixture", "Fixture", "runtime/fixture/tool.exe", [], ".", 3400, "1");
            Assert.Throws<InvalidDataException>(() => catalog.Upsert(manifest));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void ManagedService_InvalidDatabaseRegistrationShape_IsNotIgnored()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "database-runtimes.json"), "{}");
            var catalog = new ManagedServiceCatalog(root);
            var manifest = new ManagedServiceManifest(1, "fixture", "Fixture", "runtime/fixture/tool.exe", [], ".", 3400, "1");
            Assert.Throws<InvalidDataException>(() => catalog.Upsert(manifest));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task AddonCatalog_DefaultInitialization_IsSafeUnderConcurrency()
    {
        var root = NewRoot();
        try
        {
            var tasks = Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => new AddonCatalog(root).GetAddons()))
                .ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.All(results, addons => Assert.NotEmpty(addons));
        }
        finally { TryDelete(root); }
    }

    private static string CatalogJson(string installOne, string domainOne, string installTwo, string domainTwo) => $$"""
    [
      {"Key":"one","DisplayName":"One","Description":"one","InstallRelativePath":"{{installOne}}","EntryPointRelativePath":"{{installOne}}/index.php","LocalUrl":"http://{{domainOne}}","RequiredPhpExtensions":[],"Version":"1","DownloadUrl":"https://example.com/one.zip","Sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","ArchiveRootDirectory":"one"},
      {"Key":"two","DisplayName":"Two","Description":"two","InstallRelativePath":"{{installTwo}}","EntryPointRelativePath":"{{installTwo}}/index.php","LocalUrl":"http://{{domainTwo}}","RequiredPhpExtensions":[],"Version":"1","DownloadUrl":"https://example.com/two.zip","Sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","ArchiveRootDirectory":"two"}
    ]
    """;

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-final-sweep8-" + Guid.NewGuid().ToString("N"));
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
