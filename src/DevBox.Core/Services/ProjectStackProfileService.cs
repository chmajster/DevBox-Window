using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed partial class ProjectStackProfileService
{
    private readonly string _profilesPath;

    public ProjectStackProfileService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _profilesPath = Path.Combine(Path.GetFullPath(rootPath), "config", "project-profiles.json");
    }

    public IReadOnlyList<ProjectStackProfile> GetProfiles()
    {
        var custom = LoadCustomProfiles();
        return BuiltInProfiles
            .Concat(custom)
            .GroupBy(profile => profile.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ProjectStackProfile GetProfile(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return GetProfiles().FirstOrDefault(profile => profile.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Project stack profile '{key}' was not found.");
    }

    public ProjectCreateRequest CreateRequest(string profileKey, string projectName, string? domain = null)
    {
        var profile = GetProfile(profileKey);
        return new ProjectCreateRequest(
            projectName,
            domain,
            profile.Kind,
            profile.PhpVersion,
            profile.Https,
            profile.DatabaseEngine,
            null,
            profile.NodeVersion,
            profile.Addons,
            profile.Services);
    }

    public void SaveCustomProfile(ProjectStackProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Validate(profile);

        var profiles = LoadCustomProfiles().ToList();
        var existing = profiles.FindIndex(item => item.Key.Equals(profile.Key, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            profiles[existing] = profile;
        }
        else
        {
            profiles.Add(profile);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_profilesPath)!);
        AtomicWrite(_profilesPath, JsonSerializer.Serialize(profiles, JsonOptions));
    }

    public bool RemoveCustomProfile(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var profiles = LoadCustomProfiles().ToList();
        var removed = profiles.RemoveAll(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_profilesPath)!);
        AtomicWrite(_profilesPath, JsonSerializer.Serialize(profiles, JsonOptions));
        return true;
    }

    private IReadOnlyList<ProjectStackProfile> LoadCustomProfiles()
    {
        if (!File.Exists(_profilesPath))
        {
            return Array.Empty<ProjectStackProfile>();
        }

        try
        {
            var profiles = JsonSerializer.Deserialize<List<ProjectStackProfile>>(File.ReadAllText(_profilesPath), JsonOptions)
                ?? new List<ProjectStackProfile>();
            foreach (var profile in profiles)
            {
                Validate(profile);
            }
            return profiles;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("config/project-profiles.json contains invalid JSON.", ex);
        }
    }

    private static void Validate(ProjectStackProfile profile)
    {
        if (!SafeKeyRegex().IsMatch(profile.Key))
        {
            throw new InvalidDataException("Profile key contains unsupported characters.");
        }
        if (string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 100)
        {
            throw new InvalidDataException("Profile display name is invalid.");
        }
        if (profile.Kind == ProjectKind.Unknown)
        {
            throw new InvalidDataException("Profile must select a supported project kind.");
        }
        if (profile.DatabaseEngine is not ("mysql" or "mariadb" or "postgresql" or "none"))
        {
            throw new InvalidDataException("Profile database engine is invalid.");
        }
        if (profile.Addons.Any(addon => !SafeKeyRegex().IsMatch(addon)))
        {
            throw new InvalidDataException("Profile contains an invalid addon key.");
        }
        if (profile.Services.Any(service => !SafeKeyRegex().IsMatch(service)))
        {
            throw new InvalidDataException("Profile contains an invalid managed-service key.");
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, content);
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static readonly IReadOnlyList<ProjectStackProfile> BuiltInProfiles =
    [
        new("laravel", "Laravel", ProjectKind.Laravel, null, "22", "mysql", true, Array.Empty<string>(), ["mailpit", "redis"], "Laravel stack with MySQL, Node.js, Redis and Mailpit."),
        new("symfony", "Symfony", ProjectKind.Symfony, null, "22", "mysql", true, Array.Empty<string>(), ["mailpit", "redis"], "Symfony stack with MySQL, Node.js, Redis and Mailpit."),
        new("wordpress", "WordPress", ProjectKind.WordPress, null, null, "mysql", true, Array.Empty<string>(), ["mailpit"], "WordPress stack with MySQL and local mail capture."),
        new("php", "Plain PHP", ProjectKind.EmptyPhp, null, null, "mysql", true, Array.Empty<string>(), Array.Empty<string>(), "Minimal PHP site with MySQL and HTTPS."),
        new("php-minimal", "Plain PHP - minimal", ProjectKind.EmptyPhp, null, null, "none", false, Array.Empty<string>(), Array.Empty<string>(), "Minimal PHP site without database or HTTPS.")
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeKeyRegex();
}
