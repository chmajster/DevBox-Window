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
                ["mysqli", "mbstring", "openssl", "json"],
                "5.2.3",
                "https://files.phpmyadmin.net/phpMyAdmin/5.2.3/phpMyAdmin-5.2.3-all-languages.zip",
                "2d2e13c735366d318425c78e4ee2cc8fc648d77faba3ddea2cd516e43885733f",
                "phpMyAdmin-5.2.3-all-languages")
        ];
    }

    public bool IsInstalled(AddonDefinition addon)
    {
        ArgumentNullException.ThrowIfNull(addon);
        return File.Exists(addon.EntryPointPath);
    }
}
