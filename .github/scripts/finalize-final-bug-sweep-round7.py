from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:180]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")

# Managed-service templates intentionally use '.' as the DevBox root working directory.
# The generic path resolver required a strict child path and rejected the root itself.
replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''    private string ResolveRelativeFile(string relativePath, string name) => ResolveInsideRoot(relativePath, name);\n    private string ResolveRelativeDirectory(string relativePath, string name) => ResolveInsideRoot(relativePath, name);\n\n    private string ResolveInsideRoot(string relativePath, string name)\n    {\n        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath, name);\n        if (Path.IsPathRooted(relativePath))\n        {\n            throw new InvalidDataException($"{name} must be relative to the DevBox root.");\n        }\n        var root = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;\n        var full = Path.GetFullPath(Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));\n        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))\n        {\n            throw new InvalidDataException($"{name} escapes the DevBox root.");\n        }\n        return full;\n    }\n''',
    '''    private string ResolveRelativeFile(string relativePath, string name) => ResolveInsideRoot(relativePath, name, allowRoot: false);\n    private string ResolveRelativeDirectory(string relativePath, string name) => ResolveInsideRoot(relativePath, name, allowRoot: true);\n\n    private string ResolveInsideRoot(string relativePath, string name, bool allowRoot)\n    {\n        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath, name);\n        if (Path.IsPathRooted(relativePath))\n        {\n            throw new InvalidDataException($"{name} must be relative to the DevBox root.");\n        }\n        var root = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        var full = Path.GetFullPath(Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)))\n            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        var isRoot = full.Equals(root, StringComparison.OrdinalIgnoreCase);\n        if ((!allowRoot || !isRoot) && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))\n        {\n            throw new InvalidDataException($"{name} escapes the DevBox root.");\n        }\n        return full;\n    }\n''')

# Null entries in user-editable persisted lists should be controlled invalid data,
# never NullReferenceException.
replace_once(
    "src/DevBox.Core/Services/EnvironmentProfileService.cs",
    '''            var profiles = JsonSerializer.Deserialize<List<EnvironmentProfile>>(File.ReadAllText(_profilesPath), JsonOptions)\n                ?? new List<EnvironmentProfile>();\n            foreach (var profile in profiles)\n                Validate(profile);\n            return profiles.Select(Normalize).ToArray();\n''',
    '''            var profiles = JsonSerializer.Deserialize<List<EnvironmentProfile?>>(File.ReadAllText(_profilesPath), JsonOptions)\n                ?? new List<EnvironmentProfile?>();\n            if (profiles.Any(profile => profile is null))\n                throw new InvalidDataException("config/environment-profiles.json contains a null profile entry.");\n            var materialized = profiles.Select(profile => profile!).ToArray();\n            foreach (var profile in materialized)\n                Validate(profile);\n            return materialized.Select(Normalize).ToArray();\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectStackProfileService.cs",
    '''            var profiles = JsonSerializer.Deserialize<List<ProjectStackProfile>>(File.ReadAllText(_profilesPath), JsonOptions)\n                ?? new List<ProjectStackProfile>();\n            foreach (var profile in profiles) Validate(profile);\n            return profiles;\n''',
    '''            var profiles = JsonSerializer.Deserialize<List<ProjectStackProfile?>>(File.ReadAllText(_profilesPath), JsonOptions)\n                ?? new List<ProjectStackProfile?>();\n            if (profiles.Any(profile => profile is null))\n                throw new InvalidDataException("config/project-profiles.json contains a null profile entry.");\n            var materialized = profiles.Select(profile => profile!).ToArray();\n            foreach (var profile in materialized) Validate(profile);\n            return materialized;\n''')
replace_once(
    "src/DevBox.Core/Services/RuntimePlatformService.cs",
    '''            var packages = JsonSerializer.Deserialize<List<RuntimePackageEntry>>(File.ReadAllText(_catalogPath), JsonOptions)\n                ?? new List<RuntimePackageEntry>();\n            foreach (var package in packages)\n                ValidatePackage(package);\n            return packages;\n''',
    '''            var packages = JsonSerializer.Deserialize<List<RuntimePackageEntry?>>(File.ReadAllText(_catalogPath), JsonOptions)\n                ?? new List<RuntimePackageEntry?>();\n            if (packages.Any(package => package is null))\n                throw new InvalidDataException("config/runtime-catalog.json contains a null runtime entry.");\n            var materialized = packages.Select(package => package!).ToArray();\n            foreach (var package in materialized)\n                ValidatePackage(package);\n            return materialized;\n''')
replace_once(
    "src/DevBox.Core/Services/SiteManager.cs",
    '''            var sites = JsonSerializer.Deserialize<List<SiteDefinition>>(json, JsonOptions) ?? new List<SiteDefinition>();\n            foreach (var site in sites)\n                ValidateLoadedSite(site);\n            ValidateLoadedCollection(sites);\n            return sites;\n''',
    '''            var sites = JsonSerializer.Deserialize<List<SiteDefinition?>>(json, JsonOptions) ?? new List<SiteDefinition?>();\n            if (sites.Any(site => site is null))\n                throw new InvalidDataException("config/sites.json contains a null site entry.");\n            var materialized = sites.Select(site => site!).ToArray();\n            foreach (var site in materialized)\n                ValidateLoadedSite(site);\n            ValidateLoadedCollection(materialized);\n            return materialized;\n''')

# DPAPI store JSON may be manually corrupted with null encoded values; report it as
# invalid persisted state instead of leaking ArgumentNullException from Base64 decode.
replace_once(
    "src/DevBox.Core/Services/SecureSecretStore.cs",
    '''            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_storePath))\n                ?? new Dictionary<string, string>();\n            return new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);\n''',
    '''            var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(_storePath))\n                ?? new Dictionary<string, string?>();\n            if (values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))\n                throw new InvalidDataException("config/secrets.dpapi.json contains an invalid secret entry.");\n            return values.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);\n''')

# Extend regression suite.
test_path = Path("tests/DevBox.Tests/FinalBugSweepRound7Tests.cs")
text = test_path.read_text(encoding="utf-8")
needle = '''    [Fact]\n    public void ManagedServices_RejectNullStateReservedPidPrefixesAndPhpPoolPorts()\n'''
insert = r'''    [Fact]
    public void ManagedServiceTemplates_AcceptDevBoxRootWorkingDirectory()
    {
        var root = NewRoot();
        try
        {
            var catalog = new ManagedServiceCatalog(root);
            catalog.Upsert(ManagedServiceCatalog.MailpitTemplate());
            catalog.Upsert(ManagedServiceCatalog.RedisTemplate());
            var keys = catalog.GetManifests().Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("mailpit", keys);
            Assert.Contains("redis", keys);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void PersistedCatalogs_NullEntriesAreHandledWithoutNullReferenceCrashes()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "project-profiles.json"), "[null]");
            Assert.Throws<InvalidDataException>(() => new ProjectStackProfileService(root).GetProfiles());

            File.WriteAllText(Path.Combine(root, "config", "runtime-catalog.json"), "[null]");
            using (var runtimes = new RuntimePlatformService(root))
                Assert.Throws<InvalidDataException>(() => runtimes.GetCatalog());

            File.WriteAllText(Path.Combine(root, "config", "sites.json"), "[null]");
            Assert.Empty(new SiteManager(root).GetSites());
            Assert.False(File.Exists(Path.Combine(root, "config", "sites.json")));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void SecureSecretStore_NullEncodedValueIsControlledInvalidData()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "secrets.dpapi.json"), "{\"fixture\":null}");
            var store = new SecureSecretStore(root);
            Assert.Throws<InvalidDataException>(() => store.Get("fixture"));
        }
        finally { TryDelete(root); }
    }

'''
if text.count(needle) != 1:
    raise RuntimeError("test insertion point not unique")
test_path.write_text(text.replace(needle, insert + needle, 1), encoding="utf-8")
