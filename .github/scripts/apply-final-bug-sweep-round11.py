from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, got {count}: {old[:160]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")

# Database runtime registration must not rewrite the persisted port while the existing
# server process is still running on its previous port.
replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''        var registration = new DatabaseRuntimeRegistration(normalizedEngine, version, selectedPort);\n        if (index >= 0)\n            registrations[index] = registration;\n''',
    '''        if (existing is not null && existing.Port != selectedPort)\n        {\n            var current = _processes.GetStatus(BuildServiceDefinition(kind, version, existing.Port));\n            if (current.State == ServiceState.Running)\n                throw new InvalidOperationException(\n                    $"Stop {DisplayEngine(kind)} {version} before changing its registered port from {existing.Port} to {selectedPort}.");\n        }\n\n        var registration = new DatabaseRuntimeRegistration(normalizedEngine, version, selectedPort);\n        if (index >= 0)\n            registrations[index] = registration;\n''')

# Remaining database command wrappers must keep draining noisy output without retaining
# an unbounded string in memory.
replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''            stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);\n''',
    '''            stdoutTask = ProcessOutputCapture.ReadBoundedAsync(process.StandardOutput, cancellationToken: cancellationToken);\n''')
replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);\n''',
    '''        var stderrTask = ProcessOutputCapture.ReadBoundedAsync(process.StandardError, cancellationToken: cancellationToken);\n''')
replace_once(
    "src/DevBox.Core/Services/ProcessManager.cs",
    '''        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);\n        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);\n''',
    '''        var outputTask = ProcessOutputCapture.ReadBoundedAsync(process.StandardOutput, cancellationToken: cancellationToken);\n        var errorTask = ProcessOutputCapture.ReadBoundedAsync(process.StandardError, cancellationToken: cancellationToken);\n''')

# Lock files are user-editable/deserialized input. Explicit JSON nulls must produce a
# controlled data error rather than NullReferenceException, and identity/action policy
# must match the rest of the project platform.
replace_once(
    "src/DevBox.Core/Services/EnvironmentLockService.cs",
    '''    private static void ValidateLock(EnvironmentLockFile value)\n    {\n        if (value.SchemaVersion != EnvironmentLockFile.CurrentSchemaVersion)\n            throw new InvalidDataException($"Unsupported devbox.lock.json schema version: {value.SchemaVersion}.");\n        if (string.IsNullOrWhiteSpace(value.ProjectName) || string.IsNullOrWhiteSpace(value.Domain) || !value.Domain.EndsWith(".test", StringComparison.OrdinalIgnoreCase))\n            throw new InvalidDataException("Environment lock project identity is invalid.");\n        if (value.Runtimes.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))\n            throw new InvalidDataException("Environment lock contains an invalid runtime pin.");\n        if (value.Database.Engine is not ("mysql" or "mariadb" or "postgresql" or "none"))\n            throw new InvalidDataException("Environment lock database engine is invalid.");\n        if (value.Database.Port is < 1 or > 65535)\n            throw new InvalidDataException("Environment lock database port is invalid.");\n    }\n''',
    '''    private static void ValidateLock(EnvironmentLockFile value)\n    {\n        if (value.SchemaVersion != EnvironmentLockFile.CurrentSchemaVersion)\n            throw new InvalidDataException($"Unsupported devbox.lock.json schema version: {value.SchemaVersion}.");\n        if (value.Runtimes is null || value.Database is null || value.Addons is null || value.Services is null || value.Actions is null)\n            throw new InvalidDataException("Environment lock contains a null collection or database definition.");\n        if (string.IsNullOrWhiteSpace(value.ProjectName) || string.IsNullOrWhiteSpace(value.Domain))\n            throw new InvalidDataException("Environment lock project identity is invalid.");\n        try\n        {\n            _ = LocalCertificateManager.NormalizeDomain(value.Domain);\n        }\n        catch (ArgumentException ex)\n        {\n            throw new InvalidDataException("Environment lock project domain is not a valid .test domain.", ex);\n        }\n        if (value.Runtimes.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))\n            throw new InvalidDataException("Environment lock contains an invalid runtime pin.");\n        if (value.Database.Engine is not ("mysql" or "mariadb" or "postgresql" or "none"))\n            throw new InvalidDataException("Environment lock database engine is invalid.");\n        if (value.Database.Port is < 1 or > 65535)\n            throw new InvalidDataException("Environment lock database port is invalid.");\n        ProjectActionService.ValidateDefinitions(value.Actions.Cast<ProjectActionDefinition?>());\n    }\n''')

# Portable environment bundles must validate null profile/action payloads explicitly.
replace_once(
    "src/DevBox.Core/Services/RemoteEnvironmentService.cs",
    '''        ArgumentNullException.ThrowIfNull(bundle.Profile);\n        if (bundle.Profile.Actions.Count > 0)\n            throw new InvalidDataException("Portable environment shares must not contain project actions because action arguments may contain sensitive values.");\n''',
    '''        if (bundle.Profile is null)\n            throw new InvalidDataException("Environment share profile is missing.");\n        if (bundle.Profile.Actions is null)\n            throw new InvalidDataException("Environment share profile contains a null actions collection.");\n        if (bundle.Profile.Actions.Count > 0)\n            throw new InvalidDataException("Portable environment shares must not contain project actions because action arguments may contain sensitive values.");\n''')

# Add regression coverage.
tests = Path("tests/DevBox.Tests/FinalBugSweepRound11Tests.cs")
tests.write_text(r'''using System.Diagnostics;
using System.Text.Json;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound11Tests
{
    [Fact]
    public void EnvironmentLock_NullCollectionsAreControlledDataErrors()
    {
        var root = TempRoot();
        try
        {
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, EnvironmentLockService.LockFileName), """
            {"SchemaVersion":1,"ProjectName":"demo","Domain":"demo.test","Runtimes":null,"Database":{"Engine":"none","Version":null,"DatabaseName":null,"Port":null},"Https":false,"Addons":[],"Services":[],"Actions":[]}
            """);

            using var service = new EnvironmentLockService(root);
            Assert.Throws<InvalidDataException>(() => service.Load(project));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void EnvironmentLock_RejectsMalformedTestDomain()
    {
        var root = TempRoot();
        try
        {
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, EnvironmentLockService.LockFileName), """
            {"SchemaVersion":1,"ProjectName":"demo","Domain":"bad..test","Runtimes":{},"Database":{"Engine":"none","Version":null,"DatabaseName":null,"Port":null},"Https":false,"Addons":[],"Services":[],"Actions":[]}
            """);

            using var service = new EnvironmentLockService(root);
            Assert.Throws<InvalidDataException>(() => service.Load(project));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void RemoteEnvironment_NullActionsAreControlledDataErrors()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var share = Path.Combine(root, "bad.devbox-env.json");
            File.WriteAllText(share, """
            {"SchemaVersion":1,"Name":"bad","Profile":{"Key":"bad","DisplayName":"Bad","Kind":1,"Runtimes":{},"Database":{"Engine":"none","Version":null,"DatabaseName":null,"Port":null},"Https":false,"Addons":[],"Services":[],"Actions":null,"Description":""},"Metadata":{}}
            """);

            var service = new RemoteEnvironmentService(root);
            Assert.Throws<InvalidDataException>(() => service.Import(share));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void DatabaseRuntime_RunningInstanceRejectsPortMutation()
    {
        var root = TempRoot();
        Process? process = null;
        try
        {
            var runtimeBin = Path.Combine(root, "runtime", "mysql", "8.4.11", "bin");
            Directory.CreateDirectory(runtimeBin);
            var executable = Path.Combine(runtimeBin, "mysqld.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);

            using var service = new DatabaseRuntimeService(root);
            _ = service.Register("mysql", "8.4.11", 3406);

            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add("ping 127.0.0.1 -n 30 >nul");
            process = Process.Start(info)!;
            Assert.NotNull(process);

            var markerDirectory = Path.Combine(root, "tmp", "services");
            Directory.CreateDirectory(markerDirectory);
            var marker = Path.Combine(markerDirectory, "db-mysql-8-4-11.pid");
            File.WriteAllLines(marker,
            [
                process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Path.GetFullPath(executable),
                process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ]);

            var error = Assert.Throws<InvalidOperationException>(() => service.Register("mysql", "8.4.11", 3407));
            Assert.Contains("Stop MySQL 8.4.11", error.Message, StringComparison.Ordinal);
            Assert.Equal(3406, service.GetInstances("mysql").Single().Port);
        }
        finally
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            process?.Dispose();
            Delete(root);
        }
    }

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevBoxTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
''', encoding="utf-8")

# Changelog entries for every fix in this round.
changelog = Path("CHANGELOG.md")
text = changelog.read_text(encoding="utf-8")
needle = "### Fixed\n\n"
entries = '''- Database runtime registration now rejects port changes while the existing server process is still running, preventing persisted port state from diverging from the active listener.\n- Database runtime commands and legacy MySQL first-start initialization use bounded stdout/stderr capture, preventing noisy native tools from growing DevBox memory without limit.\n- `devbox.lock.json` validates null collections/database definitions as controlled data errors, applies strict `.test` domain validation and reuses canonical Project Action validation.\n- Portable environment imports report missing/null profile action collections as controlled invalid data instead of `NullReferenceException`.\n'''
pos = text.find(needle)
if pos < 0:
    raise RuntimeError("CHANGELOG Fixed section not found")
pos += len(needle)
text = text[:pos] + entries + text[pos:]
changelog.write_text(text, encoding="utf-8")
