from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


# Preserve the cancellation semantics hardened in the earlier reliability audit:
# cancelling a graceful stop must not be converted into a destructive force-kill.
replace_once(
    "src/DevBox.Core/Services/ProcessManager.cs",
    '''            catch (OperationCanceledException)\n            {\n                AppendLog(managed, "APP", "Shutdown was cancelled; terminating the managed process tree to avoid leaving an orphaned service.");\n                TryTerminateStartedProcess(managed.Process);\n                CleanupStoppedProcess(definition, managed);\n                throw;\n            }\n\n''',
    '''''')

# A failure to stop one versioned PHP pool must not prevent cleanup of every
# remaining pool during application shutdown.
replace_once(
    "src/DevBox.Core/Services/PhpRuntimePoolManager.cs",
    '''    public async Task StopAllAsync(CancellationToken cancellationToken = default)\n    {\n        cancellationToken.ThrowIfCancellationRequested();\n        foreach (var version in GetKnownVersions())\n        {\n            cancellationToken.ThrowIfCancellationRequested();\n            try\n            {\n                await StopVersionAsync(version, cancellationToken).ConfigureAwait(false);\n            }\n            catch (FileNotFoundException)\n            {\n                RemoveKnownVersion(version);\n            }\n        }\n    }\n''',
    '''    public async Task StopAllAsync(CancellationToken cancellationToken = default)\n    {\n        cancellationToken.ThrowIfCancellationRequested();\n        Exception? firstFailure = null;\n        foreach (var version in GetKnownVersions())\n        {\n            cancellationToken.ThrowIfCancellationRequested();\n            try\n            {\n                await StopVersionAsync(version, cancellationToken).ConfigureAwait(false);\n            }\n            catch (FileNotFoundException)\n            {\n                RemoveKnownVersion(version);\n            }\n            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ObjectDisposedException)\n            {\n                firstFailure ??= ex;\n            }\n        }\n\n        if (firstFailure is not null)\n            throw new InvalidOperationException("One or more versioned PHP pools could not be stopped.", firstFailure);\n    }\n''')

# Direct regression coverage for the service-level cancellation contract.
test = Path("tests/DevBox.Tests/ProcessManagerTests.cs")
text = test.read_text(encoding="utf-8")
marker = '''    [Fact]\n    public void RuntimeLayout_CreatesExpectedConfigFiles()\n'''
insert = '''    [Fact]\n    public async Task StopAsync_CancelledGracefulWait_DoesNotKillManagedService()\n    {\n        var root = Path.Combine(Path.GetTempPath(), "devbox-process-tests", Guid.NewGuid().ToString("N"));\n        try\n        {\n            Directory.CreateDirectory(root);\n            var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");\n            var stopExecutable = Path.Combine(Environment.SystemDirectory, "cmd.exe");\n            var definition = new ServiceDefinition(\n                "cancel-stop", "Cancellation stop", executable,\n                new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" },\n                root, 0, "test",\n                StopExecutablePath: stopExecutable,\n                StopArguments: new[] { "/d", "/c", "exit 0" },\n                ShutdownTimeout: TimeSpan.FromMilliseconds(500));\n\n            using var manager = new ProcessManager();\n            var started = await manager.StartAsync(definition);\n            Assert.Equal(ServiceState.Running, started.State);\n\n            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));\n            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StopAsync(definition, cancellation.Token));\n\n            Assert.Equal(ServiceState.Running, manager.GetStatus(definition).State);\n            await manager.StopAsync(definition);\n        }\n        finally\n        {\n            if (Directory.Exists(root)) Directory.Delete(root, true);\n        }\n    }\n\n'''
if marker not in text or "StopAsync_CancelledGracefulWait_DoesNotKillManagedService" in text:
    raise RuntimeError("ProcessManagerTests insertion marker mismatch")
test.write_text(text.replace(marker, insert + marker, 1), encoding="utf-8")
