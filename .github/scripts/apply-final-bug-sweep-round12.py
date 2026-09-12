from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, got {count}: {old[:160]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1/2. Task Center: WaitAsync must never observe the tiny window between publishing the
# queued task and assigning Execution. History persistence is auxiliary and must not turn
# successful operations into failures when the history path is temporarily unwritable.
path = Path("src/DevBox.Core/Services/PlatformTaskCenter.cs")
text = path.read_text(encoding="utf-8")
text = text.replace("        if (entry.Execution is not null)\n            await entry.Execution.WaitAsync(cancellationToken).ConfigureAwait(false);\n", "        await entry.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);\n", 1)
text = text.replace("        PersistHistory();\n    }\n\n    public void Dispose()", "        PersistHistorySafely();\n    }\n\n    public void Dispose()", 1)
old_method = '''    private async Task ExecuteAsync(\n        TaskEntry entry,\n        Func<IProgress<(double Progress, string? Message)>, CancellationToken, Task> operation)\n    {\n        var token = entry.Cancellation!.Token;\n        var acquired = false;\n        try\n        {\n            await _parallelism.WaitAsync(token).ConfigureAwait(false);\n            acquired = true;\n        }\n        catch (OperationCanceledException)\n        {\n            Update(entry, PlatformTaskState.Cancelled, ReadSnapshot(entry).Progress, "Cancelled before execution.", null, finished: true);\n            return;\n        }\n\n        try\n        {\n            Update(entry, PlatformTaskState.Running, Math.Max(0, ReadSnapshot(entry).Progress), "Running", null, started: true);\n            var progress = new InlineProgress<(double Progress, string? Message)>(value =>\n            {\n                var current = ReadSnapshot(entry);\n                if (IsTerminal(current.State))\n                    return;\n                var normalized = double.IsFinite(value.Progress) ? Math.Clamp(value.Progress, 0, 100) : current.Progress;\n                Update(entry, PlatformTaskState.Running, normalized, value.Message, null);\n            });\n            await operation(progress, token).ConfigureAwait(false);\n            Update(entry, PlatformTaskState.Completed, 100, "Completed", null, finished: true);\n        }\n        catch (OperationCanceledException) when (token.IsCancellationRequested)\n        {\n            Update(entry, PlatformTaskState.Cancelled, ReadSnapshot(entry).Progress, "Cancelled", null, finished: true);\n        }\n        catch (Exception ex)\n        {\n            Update(entry, PlatformTaskState.Failed, ReadSnapshot(entry).Progress, "Failed", SanitizeError(ex), finished: true);\n        }\n        finally\n        {\n            if (acquired)\n                _parallelism.Release();\n        }\n    }\n'''
new_method = '''    private async Task ExecuteAsync(\n        TaskEntry entry,\n        Func<IProgress<(double Progress, string? Message)>, CancellationToken, Task> operation)\n    {\n        try\n        {\n            var token = entry.Cancellation!.Token;\n            var acquired = false;\n            try\n            {\n                await _parallelism.WaitAsync(token).ConfigureAwait(false);\n                acquired = true;\n            }\n            catch (OperationCanceledException)\n            {\n                Update(entry, PlatformTaskState.Cancelled, ReadSnapshot(entry).Progress, "Cancelled before execution.", null, finished: true);\n                return;\n            }\n\n            try\n            {\n                Update(entry, PlatformTaskState.Running, Math.Max(0, ReadSnapshot(entry).Progress), "Running", null, started: true);\n                var progress = new InlineProgress<(double Progress, string? Message)>(value =>\n                {\n                    var current = ReadSnapshot(entry);\n                    if (IsTerminal(current.State))\n                        return;\n                    var normalized = double.IsFinite(value.Progress) ? Math.Clamp(value.Progress, 0, 100) : current.Progress;\n                    Update(entry, PlatformTaskState.Running, normalized, value.Message, null);\n                });\n                await operation(progress, token).ConfigureAwait(false);\n                Update(entry, PlatformTaskState.Completed, 100, "Completed", null, finished: true);\n            }\n            catch (OperationCanceledException) when (token.IsCancellationRequested)\n            {\n                Update(entry, PlatformTaskState.Cancelled, ReadSnapshot(entry).Progress, "Cancelled", null, finished: true);\n            }\n            catch (Exception ex)\n            {\n                Update(entry, PlatformTaskState.Failed, ReadSnapshot(entry).Progress, "Failed", SanitizeError(ex), finished: true);\n            }\n            finally\n            {\n                if (acquired)\n                    _parallelism.Release();\n            }\n        }\n        finally\n        {\n            entry.Completion.TrySetResult();\n        }\n    }\n'''
if old_method not in text:
    raise RuntimeError("PlatformTaskCenter ExecuteAsync body not found")
text = text.replace(old_method, new_method, 1)
text = text.replace("        if (persist)\n            PersistHistory();\n", "        if (persist)\n            PersistHistorySafely();\n", 1)
needle = "    private void PersistHistory()\n    {\n"
insert = '''    private void PersistHistorySafely()\n    {\n        try\n        {\n            PersistHistory();\n        }\n        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)\n        {\n            Trace.TraceError($"PlatformTaskCenter history persistence failed: {ex}");\n        }\n    }\n\n'''
if needle not in text:
    raise RuntimeError("PlatformTaskCenter PersistHistory anchor not found")
text = text.replace(needle, insert + needle, 1)
old_entry = '''        public TaskEntry(PlatformTaskSnapshot snapshot, CancellationTokenSource? cancellation)\n        {\n            Snapshot = snapshot;\n            Cancellation = cancellation;\n        }\n\n        public object Sync { get; } = new();\n        public PlatformTaskSnapshot Snapshot { get; set; }\n        public CancellationTokenSource? Cancellation { get; }\n        public Task? Execution { get; set; }\n'''
new_entry = '''        public TaskEntry(PlatformTaskSnapshot snapshot, CancellationTokenSource? cancellation)\n        {\n            Snapshot = snapshot;\n            Cancellation = cancellation;\n            if (cancellation is null || IsTerminal(snapshot.State))\n                Completion.TrySetResult();\n        }\n\n        public object Sync { get; } = new();\n        public PlatformTaskSnapshot Snapshot { get; set; }\n        public CancellationTokenSource? Cancellation { get; }\n        public Task? Execution { get; set; }\n        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);\n'''
if old_entry not in text:
    raise RuntimeError("PlatformTaskCenter TaskEntry anchor not found")
text = text.replace(old_entry, new_entry, 1)
path.write_text(text, encoding="utf-8")


# 3. Lock validation: persisted locks are executable desired-state input. Reject malformed
# service/addon identifiers and database engines that do not actually pin a version.
path = Path("src/DevBox.Core/Services/EnvironmentLockService.cs")
text = path.read_text(encoding="utf-8")
text = text.replace("ValidateLock(", "ValidateLockData(")
text = text.replace("    private static void ValidateLockData(EnvironmentLockFile value)\n", "    internal static void ValidateLockData(EnvironmentLockFile value)\n", 1)
old_validation = '''        if (value.Database.Engine is not ("mysql" or "mariadb" or "postgresql" or "none"))\n            throw new InvalidDataException("Environment lock database engine is invalid.");\n        if (value.Database.Port is < 1 or > 65535)\n            throw new InvalidDataException("Environment lock database port is invalid.");\n        ProjectActionService.ValidateDefinitions(value.Actions.Cast<ProjectActionDefinition?>());\n'''
new_validation = '''        var databaseEngine = value.Database.Engine?.Trim().ToLowerInvariant();\n        if (databaseEngine is not ("mysql" or "mariadb" or "postgresql" or "none"))\n            throw new InvalidDataException("Environment lock database engine is invalid.");\n        if (databaseEngine != "none" && string.IsNullOrWhiteSpace(value.Database.Version))\n            throw new InvalidDataException("Environment lock database version is required when a database engine is pinned.");\n        if (value.Database.Port is < 1 or > 65535)\n            throw new InvalidDataException("Environment lock database port is invalid.");\n        if (value.Addons.Any(IsInvalidEnvironmentKey) || value.Services.Any(IsInvalidEnvironmentKey))\n            throw new InvalidDataException("Environment lock contains an invalid addon or service key.");\n        ProjectActionService.ValidateDefinitions(value.Actions.Cast<ProjectActionDefinition?>());\n'''
if old_validation not in text:
    raise RuntimeError("EnvironmentLock validation anchor not found")
text = text.replace(old_validation, new_validation, 1)
helper_anchor = "    private static void AtomicWrite(string path, string content)\n"
helper = '''    private static bool IsInvalidEnvironmentKey(string? value)\n    {\n        if (string.IsNullOrWhiteSpace(value))\n            return true;\n        var normalized = value.Trim();\n        return normalized.Length > 64 || !char.IsLetterOrDigit(normalized[0]) ||\n               normalized.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-');\n    }\n\n'''
if helper_anchor not in text:
    raise RuntimeError("EnvironmentLock helper anchor not found")
text = text.replace(helper_anchor, helper + helper_anchor, 1)
path.write_text(text, encoding="utf-8")


# 4/5. Snapshot and transfer imports must apply the exact same semantic lock validation
# used by EnvironmentLockService instead of carrying malformed locks into restored projects.
replace_once(
    "src/DevBox.Core/Services/ProjectSnapshotService.cs",
    '''            var lockFile = JsonSerializer.Deserialize<EnvironmentLockFile>(ReadMetadataText(lockPath, EnvironmentLockService.LockFileName), JsonOptions)\n                ?? throw new InvalidDataException("Snapshot devbox.lock.json is empty.");\n            AtomicWrite(lockPath, JsonSerializer.Serialize(lockFile with\n            {\n                ProjectName = projectName,\n                Domain = domain,\n                GeneratedAtUtc = DateTimeOffset.UtcNow\n            }, JsonOptions));\n''',
    '''            var lockFile = JsonSerializer.Deserialize<EnvironmentLockFile>(ReadMetadataText(lockPath, EnvironmentLockService.LockFileName), JsonOptions)\n                ?? throw new InvalidDataException("Snapshot devbox.lock.json is empty.");\n            var rewrittenLock = lockFile with\n            {\n                ProjectName = projectName,\n                Domain = domain,\n                GeneratedAtUtc = DateTimeOffset.UtcNow\n            };\n            EnvironmentLockService.ValidateLockData(rewrittenLock);\n            AtomicWrite(lockPath, JsonSerializer.Serialize(rewrittenLock, JsonOptions));\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''                var lockFile = JsonSerializer.Deserialize<EnvironmentLockFile>(ReadMetadataText(lockPath, EnvironmentLockService.LockFileName), JsonOptions);\n                if (lockFile is not null)\n                    AtomicWrite(lockPath, JsonSerializer.Serialize(lockFile with { ProjectName = name, Domain = domain, GeneratedAtUtc = DateTimeOffset.UtcNow }, JsonOptions));\n''',
    '''                var lockFile = JsonSerializer.Deserialize<EnvironmentLockFile>(ReadMetadataText(lockPath, EnvironmentLockService.LockFileName), JsonOptions)\n                    ?? throw new InvalidDataException("Imported devbox.lock.json is empty.");\n                var rewrittenLock = lockFile with { ProjectName = name, Domain = domain, GeneratedAtUtc = DateTimeOffset.UtcNow };\n                EnvironmentLockService.ValidateLockData(rewrittenLock);\n                AtomicWrite(lockPath, JsonSerializer.Serialize(rewrittenLock, JsonOptions));\n''')


# 6. Self-update cleanup is best-effort. A locked partial download must not replace the
# real download/checksum exception with a cleanup IOException.
replace_once(
    "src/DevBox.Core/Services/ApplicationSelfUpdateService.cs",
    '''        finally\n        {\n            if (File.Exists(temporaryPath))\n                File.Delete(temporaryPath);\n        }\n\n        return new SelfUpdatePackage(version, installerPath, expectedInstallerName, expectedSha256, releaseUrl);\n''',
    '''        finally\n        {\n            TryDeleteFile(temporaryPath);\n        }\n\n        return new SelfUpdatePackage(version, installerPath, expectedInstallerName, expectedSha256, releaseUrl);\n''')
path = Path("src/DevBox.Core/Services/ApplicationSelfUpdateService.cs")
text = path.read_text(encoding="utf-8")
anchor = "    public void Dispose()\n"
helper = '''    private static void TryDeleteFile(string path)\n    {\n        try\n        {\n            if (File.Exists(path))\n                File.Delete(path);\n        }\n        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)\n        {\n        }\n    }\n\n'''
if anchor not in text:
    raise RuntimeError("Self-update Dispose anchor not found")
text = text.replace(anchor, helper + anchor, 1)
path.write_text(text, encoding="utf-8")


# Regression coverage.
Path("tests/DevBox.Tests/FinalBugSweepRound12Tests.cs").write_text(r'''using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound12Tests
{
    [Fact]
    public async Task TaskCenter_WaitAsyncDuringInitialPublishWaitsForExecution()
    {
        var root = TempRoot();
        try
        {
            using var center = new PlatformTaskCenter(root, 1);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<PlatformTaskSnapshot>? observedWait = null;
            center.TaskChanged += (_, snapshot) =>
            {
                if (snapshot.State == PlatformTaskState.Queued && observedWait is null)
                    observedWait = center.WaitAsync(snapshot.Id);
            };

            _ = center.Enqueue("wait-race", async (_, _) => await gate.Task.ConfigureAwait(false));
            Assert.NotNull(observedWait);
            Assert.False(observedWait!.IsCompleted);

            gate.TrySetResult();
            var result = await observedWait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PlatformTaskState.Completed, result.State);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task TaskCenter_UnwritableHistoryDoesNotBreakTaskLifecycle()
    {
        var root = TempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "logs"), "blocks history directory creation");
            using var center = new PlatformTaskCenter(root, 1);
            var id = center.Enqueue("history-failure", (_, _) => Task.CompletedTask);
            var result = await center.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PlatformTaskState.Completed, result.State);
            Assert.Equal(100, result.Progress);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void EnvironmentLock_RejectsNullServiceKey()
    {
        var value = ValidLock() with { Services = new string[] { null! } };
        Assert.Throws<InvalidDataException>(() => EnvironmentLockService.ValidateLockData(value));
    }

    [Fact]
    public void EnvironmentLock_RejectsDatabaseEngineWithoutVersion()
    {
        var value = ValidLock() with { Database = new EnvironmentDatabasePin("mysql", null, "demo", 3306) };
        Assert.Throws<InvalidDataException>(() => EnvironmentLockService.ValidateLockData(value));
    }

    [Fact]
    public void EnvironmentLock_RejectsUnsafeServiceKey()
    {
        var value = ValidLock() with { Services = ["../redis"] };
        Assert.Throws<InvalidDataException>(() => EnvironmentLockService.ValidateLockData(value));
    }

    private static EnvironmentLockFile ValidLock() => new()
    {
        ProjectName = "demo",
        Domain = "demo.test",
        Runtimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Database = new EnvironmentDatabasePin("none", null, null),
        Addons = Array.Empty<string>(),
        Services = Array.Empty<string>(),
        Actions = Array.Empty<ProjectActionDefinition>()
    };

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevBoxTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
''', encoding="utf-8")


# Changelog entries for every concrete fix in this sweep.
changelog = Path("CHANGELOG.md")
text = changelog.read_text(encoding="utf-8")
needle = "### Fixed\n\n"
entries = '''- Task Center `WaitAsync` now waits on a completion handle created before the initial queued notification, closing a race where subscribers could observe completion before execution had even been assigned.\n- Task Center history persistence failures are isolated from task execution, so an unwritable history path can no longer make enqueueing fail or convert a successful operation into a failed task.\n- Environment locks reject null/unsafe ADDON and managed-service keys and require a database version whenever a database engine is pinned.\n- Project snapshot restores and project archive imports reuse canonical environment-lock validation before carrying `devbox.lock.json` into the restored project.\n- Project archive imports reject a JSON `null` environment lock instead of silently preserving the invalid file.\n- Self-update partial-download cleanup is best-effort so a locked temporary file cannot mask the original download or checksum failure.\n'''
pos = text.find(needle)
if pos < 0:
    raise RuntimeError("CHANGELOG Fixed section not found")
pos += len(needle)
changelog.write_text(text[:pos] + entries + text[pos:], encoding="utf-8")
