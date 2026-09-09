using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class AddonCatalog
{
    private readonly string _rootPath;

    public AddonCatalog(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public IReadOnlyList<AddonDefinition> GetDefaultAddons()
    {
        var phpMyAdminPath = Path.Combine(_rootPath, "www", "phpmyadmin");

        return
        [
            new AddonDefinition(
                "phpmyadmin",
                "phpMyAdmin",
                "Web interface for managing MySQL and MariaDB databases.",
                phpMyAdminPath,
                Path.Combine(phpMyAdminPath, "index.php"),
                "http://phpmyadmin.test",
                ["mysqli", "mbstring", "openssl", "json"])
        ];
    }

    public bool IsInstalled(AddonDefinition addon)
    {
        ArgumentNullException.ThrowIfNull(addon);
        return File.Exists(addon.EntryPointPath);
    }
}
