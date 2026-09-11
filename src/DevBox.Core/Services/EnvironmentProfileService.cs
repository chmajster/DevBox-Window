using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed partial class EnvironmentProfileService
{
    private const string RecommendedPhp = "8.5.10";
    private const string RecommendedNginx = "1.31.5";
    private const string RecommendedMySql = "8.4.11";
    private readonly string _profilesPath;

    public EnvironmentProfileService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _profilesPath = Path.Combine(Path.GetFullPath(rootPath), "config", "environment-profiles.json");
    }

    public IReadOnlyList<EnvironmentProfile> GetProfiles()
    {
        var result = BuiltInProfiles
            .Concat(LoadCustomProfiles())
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return result;
    }

    public EnvironmentProfile GetProfile(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return GetProfiles().FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Environment profile '{key}' was not found.");
    }

    public void SaveCustomProfile(EnvironmentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Validate(profile);
        using var mutationLock = CrossProcessFileLock.Acquire(_profilesPath + ".lock", TimeSpan.FromSeconds(15));

        var custom = LoadCustomProfiles().ToList();
        var index = custom.FindIndex(item => item.Key.Equals(profile.Key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            custom[index] = Normalize(profile);
        else
            custom.Add(Normalize(profile));

        Directory.CreateDirectory(Path.GetDirectoryName(_profilesPath)!);
        AtomicWrite(_profilesPath, JsonSerializer.Serialize(custom, JsonOptions));
    }

    public bool RemoveCustomProfile(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var mutationLock = CrossProcessFileLock.Acquire(_profilesPath + ".lock", TimeSpan.FromSeconds(15));
        var custom = LoadCustomProfiles().ToList();
        var removed = custom.RemoveAll(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(_profilesPath)!);
        AtomicWrite(_profilesPath, JsonSerializer.Serialize(custom, JsonOptions));
        return true;
    }

    private IReadOnlyList<EnvironmentProfile> LoadCustomProfiles()
    {
        if (!File.Exists(_profilesPath))
            return Array.Empty<EnvironmentProfile>();

        try
        {
            var profiles = JsonSerializer.Deserialize<List<EnvironmentProfile>>(File.ReadAllText(_profilesPath), JsonOptions)
                ?? new List<EnvironmentProfile>();
            foreach (var profile in profiles)
                Validate(profile);
            return profiles.Select(Normalize).ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("config/environment-profiles.json contains invalid JSON.", ex);
        }
    }

    private static EnvironmentProfile Normalize(EnvironmentProfile profile) => profile with
    {
        Key = profile.Key.Trim().ToLowerInvariant(),
        DisplayName = profile.DisplayName.Trim(),
        Runtimes = profile.Runtimes
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key.Trim().ToLowerInvariant(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase),
        Database = profile.Database with
        {
            Engine = profile.Database.Engine.Trim().ToLowerInvariant(),
            Version = string.IsNullOrWhiteSpace(profile.Database.Version) ? null : profile.Database.Version.Trim(),
            DatabaseName = string.IsNullOrWhiteSpace(profile.Database.DatabaseName) ? null : profile.Database.DatabaseName.Trim()
        },
        Addons = NormalizeKeys(profile.Addons),
        Services = NormalizeKeys(profile.Services),
        Actions = profile.Actions.Where(action => action.Enabled).Select(NormalizeAction).ToArray(),
        Description = profile.Description?.Trim() ?? string.Empty
    };

    private static ProjectActionDefinition NormalizeAction(ProjectActionDefinition action) => action with
    {
        Key = action.Key.Trim().ToLowerInvariant(),
        DisplayName = action.DisplayName.Trim(),
        Executable = action.Executable.Trim(),
        Arguments = action.Arguments.Select(value => value.Trim()).ToArray(),
        WorkingDirectory = string.IsNullOrWhiteSpace(action.WorkingDirectory) ? null : action.WorkingDirectory.Trim()
    };

    private static IReadOnlyList<string> NormalizeKeys(IReadOnlyList<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim().ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static void Validate(EnvironmentProfile profile)
    {
        if (!SafeKeyRegex().IsMatch(profile.Key ?? string.Empty))
            throw new InvalidDataException("Environment profile key contains unsupported characters.");
        if (string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 120)
            throw new InvalidDataException("Environment profile display name is invalid.");
        if (profile.Kind == ProjectKind.Unknown)
            throw new InvalidDataException("Environment profile must use a supported project kind.");

        foreach (var pair in profile.Runtimes)
        {
            if (!SafeKeyRegex().IsMatch(pair.Key) || !SafeVersionRegex().IsMatch(pair.Value))
                throw new InvalidDataException($"Environment profile contains an invalid runtime pin: {pair.Key}={pair.Value}.");
        }

        var engine = profile.Database.Engine?.Trim().ToLowerInvariant();
        if (engine is not ("mysql" or "mariadb" or "postgresql" or "none"))
            throw new InvalidDataException("Environment profile database engine is invalid.");
        if (!string.IsNullOrWhiteSpace(profile.Database.Version) && !SafeVersionRegex().IsMatch(profile.Database.Version))
            throw new InvalidDataException("Environment profile database version is invalid.");
        if (profile.Database.Port is < 1 or > 65535)
            throw new InvalidDataException("Environment profile database port is outside the valid TCP range.");
        if (profile.Addons.Any(value => !SafeKeyRegex().IsMatch(value)) || profile.Services.Any(value => !SafeKeyRegex().IsMatch(value)))
            throw new InvalidDataException("Environment profile contains an invalid addon or service key.");

        foreach (var action in profile.Actions)
        {
            if (!SafeKeyRegex().IsMatch(action.Key) || string.IsNullOrWhiteSpace(action.DisplayName))
                throw new InvalidDataException("Environment profile contains an invalid project action.");
            if (action.TimeoutSeconds is < 1 or > 3600)
                throw new InvalidDataException($"Action '{action.Key}' timeout must be between 1 and 3600 seconds.");
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, content);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static readonly IReadOnlyList<EnvironmentProfile> BuiltInProfiles =
    [
        new EnvironmentProfile
        {
            Key = "laravel-full",
            DisplayName = "Laravel - full",
            Kind = ProjectKind.Laravel,
            Runtimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["php"] = RecommendedPhp,
                ["nginx"] = RecommendedNginx,
                ["node"] = NodeRuntimeCatalog.RecommendedVersion
            },
            Database = new EnvironmentDatabasePin("mysql", RecommendedMySql, null, 3306),
            Https = true,
            Services = ["mailpit", "redis"],
            Actions =
            [
                new ProjectActionDefinition("composer-install", "Composer install", "composer", ["install", "--no-interaction"], null, 1200),
                new ProjectActionDefinition("npm-install", "npm install", "npm", ["install"], null, 1200)
            ],
            Description = "Pinned Laravel environment with PHP, Nginx, Node.js LTS, MySQL, Mailpit and Redis-compatible Garnet."
        },
        new EnvironmentProfile
        {
            Key = "symfony-full",
            DisplayName = "Symfony - full",
            Kind = ProjectKind.Symfony,
            Runtimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["php"] = RecommendedPhp,
                ["nginx"] = RecommendedNginx,
                ["node"] = NodeRuntimeCatalog.RecommendedVersion
            },
            Database = new EnvironmentDatabasePin("mysql", RecommendedMySql, null, 3306),
            Https = true,
            Services = ["mailpit", "redis"],
            Actions =
            [
                new ProjectActionDefinition("composer-install", "Composer install", "composer", ["install", "--no-interaction"], null, 1200)
            ],
            Description = "Pinned Symfony environment with PHP, Nginx, Node.js LTS, MySQL, Mailpit and Redis-compatible Garnet."
        },
        new EnvironmentProfile
        {
            Key = "wordpress-full",
            DisplayName = "WordPress - full",
            Kind = ProjectKind.WordPress,
            Runtimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["php"] = RecommendedPhp,
                ["nginx"] = RecommendedNginx
            },
            Database = new EnvironmentDatabasePin("mysql", RecommendedMySql, null, 3306),
            Https = true,
            Services = ["mailpit"],
            Description = "Pinned WordPress environment with PHP, Nginx, MySQL and Mailpit."
        },
        new EnvironmentProfile
        {
            Key = "php-full",
            DisplayName = "Plain PHP - full",
            Kind = ProjectKind.EmptyPhp,
            Runtimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["php"] = RecommendedPhp,
                ["nginx"] = RecommendedNginx
            },
            Database = new EnvironmentDatabasePin("mysql", RecommendedMySql, null, 3306),
            Https = true,
            Description = "Pinned PHP, Nginx and MySQL environment."
        },
        new EnvironmentProfile
        {
            Key = "php-minimal",
            DisplayName = "Plain PHP - minimal",
            Kind = ProjectKind.EmptyPhp,
            Runtimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["php"] = RecommendedPhp,
                ["nginx"] = RecommendedNginx
            },
            Database = new EnvironmentDatabasePin("none", null, null),
            Https = false,
            Description = "Pinned PHP and Nginx without a database."
        }
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVersionRegex();
}
