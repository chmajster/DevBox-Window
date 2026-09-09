using System.Text.Json;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class AddonCatalog
{
    private readonly string _rootPath;
    private readonly string _catalogPath;

    public AddonCatalog(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _catalogPath = Path.Combine(_rootPath, "config", "addons.json");
    }

    public string CatalogPath => _catalogPath;

    public IReadOnlyList<AddonDefinition> GetDefaultAddons() => GetAddons();

    public IReadOnlyList<AddonDefinition> GetAddons()
    {
        EnsureDefaultCatalog();
        try
        {
            var json = File.ReadAllText(_catalogPath);
            var entries = JsonSerializer.Deserialize<List<AddonManifestEntry>>(json, JsonOptions)
                ?? throw new InvalidDataException("Addon manifest is empty.");
            if (entries.Count == 0)
            {
                return Array.Empty<AddonDefinition>();
            }

            var duplicate = entries
                .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidDataException($"Addon manifest contains duplicate key '{duplicate.Key}'.");
            }

            return entries.Select(ToDefinition).ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Addon manifest contains invalid JSON.", ex);
        }
    }

    public bool IsInstalled(AddonDefinition addon)
    {
        ArgumentNullException.ThrowIfNull(addon);
        return File.Exists(addon.EntryPointPath);
    }

    private AddonDefinition ToDefinition(AddonManifestEntry entry)
    {
        ValidateEntry(entry);
        var installPath = ResolveRelativePath(entry.InstallRelativePath);
        var wwwRoot = Path.GetFullPath(Path.Combine(_rootPath, "www"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedInstall = installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!normalizedInstall.StartsWith(wwwRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Addon '{entry.Key}' install path must be inside the DevBox www directory.");
        }

        var entryPointPath = ResolveRelativePath(entry.EntryPointRelativePath);
        if (!entryPointPath.StartsWith(normalizedInstall, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Addon '{entry.Key}' entry point must be inside its install directory.");
        }

        return new AddonDefinition(
            entry.Key,
            entry.DisplayName,
            entry.Description,
            installPath,
            entryPointPath,
            entry.LocalUrl,
            entry.RequiredPhpExtensions ?? Array.Empty<string>(),
            entry.Version,
            entry.DownloadUrl,
            entry.Sha256,
            entry.ArchiveRootDirectory);
    }

    private void EnsureDefaultCatalog()
    {
        if (File.Exists(_catalogPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        var json = JsonSerializer.Serialize(DefaultManifest, JsonOptions);
        var tempPath = _catalogPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            if (!File.Exists(_catalogPath))
            {
                File.Move(tempPath, _catalogPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private string ResolveRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Addon manifest paths must be relative to the DevBox root.");
        }

        var root = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Addon manifest path escapes the DevBox root.");
        }
        return fullPath;
    }

    private static void ValidateEntry(AddonManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Key) ||
            entry.Key.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidDataException("Addon key contains invalid characters.");
        }
        if (string.IsNullOrWhiteSpace(entry.DisplayName) || string.IsNullOrWhiteSpace(entry.Version))
        {
            throw new InvalidDataException($"Addon '{entry.Key}' must define displayName and version.");
        }
        if (!Uri.TryCreate(entry.LocalUrl, UriKind.Absolute, out var localUri) ||
            localUri.Scheme is not ("http" or "https") ||
            !localUri.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Addon '{entry.Key}' localUrl must be an absolute .test URL.");
        }
        if (!Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"Addon '{entry.Key}' downloadUrl must use HTTPS.");
        }
        try
        {
            if (entry.Sha256.Length != 64 || Convert.FromHexString(entry.Sha256).Length != 32)
            {
                throw new InvalidDataException($"Addon '{entry.Key}' has an invalid SHA-256 value.");
            }
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"Addon '{entry.Key}' has an invalid SHA-256 value.", ex);
        }
        if (Path.IsPathRooted(entry.ArchiveRootDirectory) ||
            entry.ArchiveRootDirectory.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
        {
            throw new InvalidDataException($"Addon '{entry.Key}' archive root is unsafe.");
        }
    }

    private static readonly IReadOnlyList<AddonManifestEntry> DefaultManifest =
    [
        new(
            "phpmyadmin",
            "phpMyAdmin",
            "Web interface for managing MySQL and MariaDB databases.",
            "www/phpmyadmin",
            "www/phpmyadmin/index.php",
            "http://phpmyadmin.test",
            ["mysqli", "mbstring", "openssl", "json"],
            "5.2.3",
            "https://files.phpmyadmin.net/phpMyAdmin/5.2.3/phpMyAdmin-5.2.3-all-languages.zip",
            "2d2e13c735366d318425c78e4ee2cc8fc648d77faba3ddea2cd516e43885733f",
            "phpMyAdmin-5.2.3-all-languages")
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private sealed record AddonManifestEntry(
        string Key,
        string DisplayName,
        string Description,
        string InstallRelativePath,
        string EntryPointRelativePath,
        string LocalUrl,
        IReadOnlyList<string>? RequiredPhpExtensions,
        string Version,
        string DownloadUrl,
        string Sha256,
        string ArchiveRootDirectory);
}
