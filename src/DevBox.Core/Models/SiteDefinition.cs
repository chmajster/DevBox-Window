namespace DevBox.Core.Models;

public sealed record SiteDefinition(
    string Name,
    string Domain,
    string DocumentRoot,
    string PhpRuntimeKey = "php",
    string? PhpVersion = null,
    bool HttpsEnabled = false);
