from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:180]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")

# Fix nullable analysis in managed-service key validation.
replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''        if (!SafeKeyRegex().IsMatch(manifest.Key ?? string.Empty) || ReservedKeys.Contains(manifest.Key) ||\n            ReservedKeyPrefixes.Any(prefix => manifest.Key?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true))\n        {\n            throw new InvalidDataException($"Managed service key '{manifest.Key}' is invalid or reserved.");\n        }\n''',
    '''        var manifestKey = manifest.Key ?? string.Empty;\n        if (!SafeKeyRegex().IsMatch(manifestKey) || ReservedKeys.Contains(manifestKey) ||\n            ReservedKeyPrefixes.Any(prefix => manifestKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))\n        {\n            throw new InvalidDataException($"Managed service key '{manifest.Key}' is invalid or reserved.");\n        }\n''')

# Database runtime registration used only active listeners and database registrations,
# so it could claim a port already assigned to an enabled managed service that was not
# currently running. Make reservation checks symmetric.
replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''        var registrations = LoadRegistrations().ToList();\n        var index = registrations.FindIndex(item => item.Engine.Equals(normalizedEngine, StringComparison.OrdinalIgnoreCase) && item.Version.Equals(version, StringComparison.OrdinalIgnoreCase));\n        var existing = index >= 0 ? registrations[index] : null;\n        var selectedPort = port ?? existing?.Port ?? ChooseAvailablePort(kind, registrations);\n''',
    '''        var registrations = LoadRegistrations().ToList();\n        var managedServicePorts = GetEnabledManagedServicePorts();\n        var index = registrations.FindIndex(item => item.Engine.Equals(normalizedEngine, StringComparison.OrdinalIgnoreCase) && item.Version.Equals(version, StringComparison.OrdinalIgnoreCase));\n        var existing = index >= 0 ? registrations[index] : null;\n        var selectedPort = port ?? existing?.Port ?? ChooseAvailablePort(kind, registrations, managedServicePorts);\n''')
replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''        if (registrations.Any(item => item.Port == selectedPort && !(item.Engine.Equals(normalizedEngine, StringComparison.OrdinalIgnoreCase) && item.Version.Equals(version, StringComparison.OrdinalIgnoreCase))))\n            throw new InvalidOperationException($"Port {selectedPort} is already assigned to another DevBox database runtime.");\n        if (port.HasValue && (existing is null || existing.Port != selectedPort) && IsTcpPortInUse(selectedPort))\n''',
    '''        if (registrations.Any(item => item.Port == selectedPort && !(item.Engine.Equals(normalizedEngine, StringComparison.OrdinalIgnoreCase) && item.Version.Equals(version, StringComparison.OrdinalIgnoreCase))))\n            throw new InvalidOperationException($"Port {selectedPort} is already assigned to another DevBox database runtime.");\n        if ((existing is null || existing.Port != selectedPort) && managedServicePorts.Contains(selectedPort))\n            throw new InvalidOperationException($"Port {selectedPort} is already assigned to an enabled managed service.");\n        if (port.HasValue && (existing is null || existing.Port != selectedPort) && IsTcpPortInUse(selectedPort))\n''')
replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''    private int ChooseAvailablePort(DatabaseEngineKind kind, IReadOnlyList<DatabaseRuntimeRegistration> registrations)\n    {\n''',
    '''    private int ChooseAvailablePort(DatabaseEngineKind kind, IReadOnlyList<DatabaseRuntimeRegistration> registrations, IReadOnlySet<int>? additionalReservedPorts = null)\n    {\n''')
replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''        var assigned = registrations.Select(item => item.Port).ToHashSet();\n        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet();\n        for (var port = start; port <= 65535; port++)\n        {\n            if (!assigned.Contains(port) && !listeners.Contains(port))\n                return port;\n        }\n''',
    '''        var assigned = registrations.Select(item => item.Port).ToHashSet();\n        var reserved = additionalReservedPorts ?? GetEnabledManagedServicePorts();\n        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet();\n        for (var port = start; port <= 65535; port++)\n        {\n            if (!assigned.Contains(port) && !reserved.Contains(port) && !listeners.Contains(port))\n                return port;\n        }\n''')
replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''    private static bool IsTcpPortInUse(int port) =>\n        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);\n\n''',
    '''    private static bool IsTcpPortInUse(int port) =>\n        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);\n\n    private HashSet<int> GetEnabledManagedServicePorts() =>\n        new ManagedServiceCatalog(_rootPath).GetManifests()\n            .Where(item => item.Enabled)\n            .Select(item => item.Port)\n            .ToHashSet();\n\n''')

# Add regression for managed-service -> database port collision.
test_path = Path("tests/DevBox.Tests/FinalBugSweepRound7Tests.cs")
text = test_path.read_text(encoding="utf-8")
needle = '''    [Fact]\n    public void DatabaseRuntimeRegistrations_RejectDuplicateIdentityAndNullEntries()\n'''
insert = r'''    [Fact]
    public void DatabaseRuntime_RegisterRejectsPortAssignedToManagedService()
    {
        var root = NewRoot();
        try
        {
            const int port = 3400;
            var catalog = new ManagedServiceCatalog(root);
            catalog.Upsert(new ManagedServiceManifest(
                ManagedServiceManifest.CurrentSchemaVersion,
                "custom-db-port-fixture",
                "Custom",
                "runtime/custom/tool.exe",
                Array.Empty<string>(),
                ".",
                port,
                "1"));

            var executable = Path.Combine(root, "runtime", "mysql", "8.4.11", "bin", "mysqld.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "fixture");

            using var databases = new DatabaseRuntimeService(root);
            Assert.Throws<InvalidOperationException>(() => databases.Register("mysql", "8.4.11", port));
        }
        finally { TryDelete(root); }
    }

'''
if text.count(needle) != 1:
    raise RuntimeError("test insertion point not unique")
test_path.write_text(text.replace(needle, insert + needle, 1), encoding="utf-8")
