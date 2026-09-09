using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class RuntimeCatalog
{
    public IReadOnlyList<RuntimeDefinition> GetRecommendedWindowsRuntimes() =>
    [
        new RuntimeDefinition(
            "php",
            "PHP",
            "8.5.10",
            "https://downloads.php.net/~windows/releases/archives/php-8.5.10-nts-Win32-vs17-x64.zip",
            "22ec430195984d233eb9e62c637a945bbcda06efca2f392d9d96d62c6acd34f8",
            "php-cgi.exe"),
        new RuntimeDefinition(
            "nginx",
            "Nginx",
            "1.31.5",
            "https://nginx.org/download/nginx-1.31.5.zip",
            "00ad32a2bf66cee0ec8eb194347e8e79917f47017ccd3ad4bebf5574fabe002c",
            "nginx.exe",
            "nginx-1.31.5")
    ];

    public RuntimeDefinition GetRecommended(string key) =>
        GetRecommendedWindowsRuntimes().FirstOrDefault(runtime => runtime.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"No verified recommended runtime is registered for '{key}'.");
}
