from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:180]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


# Case-insensitive secret keys are the store contract. JSON can contain keys that
# differ only by case; converting them to an OrdinalIgnoreCase dictionary otherwise
# throws ArgumentException instead of reporting corrupt persisted state.
replace_once(
    "src/DevBox.Core/Services/SecureSecretStore.cs",
    '''            if (values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))\n                throw new InvalidDataException("config/secrets.dpapi.json contains an invalid secret entry.");\n            return values.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);\n''',
    '''            if (values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))\n                throw new InvalidDataException("config/secrets.dpapi.json contains an invalid secret entry.");\n            var duplicateKey = values.Keys.GroupBy(key => key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);\n            if (duplicateKey is not null)\n                throw new InvalidDataException($"config/secrets.dpapi.json contains duplicate secret key '{duplicateKey.Key}'.");\n            return values.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.OrdinalIgnoreCase);\n''')

# Environment runtime pins are normalized to a case-insensitive dictionary. Reject
# duplicate pins before normalization so hand-edited JSON cannot crash ToDictionary.
replace_once(
    "src/DevBox.Core/Services/EnvironmentProfileService.cs",
    '''        foreach (var pair in profile.Runtimes)\n        {\n            if (!SafeKeyRegex().IsMatch(pair.Key ?? string.Empty) || !SafeVersionRegex().IsMatch(pair.Value ?? string.Empty))\n                throw new InvalidDataException($"Environment profile contains an invalid runtime pin: {pair.Key}={pair.Value}.");\n        }\n\n        var engine = profile.Database.Engine?.Trim().ToLowerInvariant();\n''',
    '''        foreach (var pair in profile.Runtimes)\n        {\n            if (!SafeKeyRegex().IsMatch(pair.Key ?? string.Empty) || !SafeVersionRegex().IsMatch(pair.Value ?? string.Empty))\n                throw new InvalidDataException($"Environment profile contains an invalid runtime pin: {pair.Key}={pair.Value}.");\n        }\n        var duplicateRuntime = profile.Runtimes.Keys.GroupBy(key => key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);\n        if (duplicateRuntime is not null)\n            throw new InvalidDataException($"Environment profile contains duplicate runtime key '{duplicateRuntime.Key}'.");\n\n        var engine = profile.Database.Engine?.Trim().ToLowerInvariant();\n''')

# Extend round8 regression coverage.
test_path = Path("tests/DevBox.Tests/FinalBugSweepRound8Tests.cs")
text = test_path.read_text(encoding="utf-8")
needle = '''    [Fact]\n    public void EnvironmentProfile_RejectsDuplicateActionKeys()\n'''
insert = r'''    [Fact]
    public void EnvironmentProfile_RejectsCaseInsensitiveDuplicateRuntimePins()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "environment-profiles.json"), """
            [{"Key":"fixture","DisplayName":"Fixture","Kind":"EmptyPhp","Runtimes":{"php":"8.5.10","PHP":"8.4.0"},"Database":{"Engine":"none","Port":3306},"Https":false,"Addons":[],"Services":[],"Actions":[]}]
            """);
            Assert.Throws<InvalidDataException>(() => new EnvironmentProfileService(root).GetProfiles());
        }
        finally { TryDelete(root); }
    }

'''
if text.count(needle) != 1:
    raise RuntimeError("Environment profile test insertion point not unique")
text = text.replace(needle, insert + needle, 1)
needle2 = '''    [Fact]\n    public void ManagedService_PortReservationReadsDatabasePortCaseInsensitively()\n'''
insert2 = r'''    [Fact]
    public void SecureSecretStore_RejectsCaseInsensitiveDuplicateKeys()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "secrets.dpapi.json"), "{\"Token\":\"AA==\",\"token\":\"AA==\"}");
            var store = new SecureSecretStore(root);
            var method = typeof(SecureSecretStore).GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(store, null));
            Assert.IsType<InvalidDataException>(error.InnerException);
        }
        finally { TryDelete(root); }
    }

'''
if text.count(needle2) != 1:
    raise RuntimeError("secret-store test insertion point not unique")
text = text.replace(needle2, insert2 + needle2, 1)
test_path.write_text(text, encoding="utf-8")
