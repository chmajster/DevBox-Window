namespace DevBox.Core.Models;

public sealed record WordPressSiteRequest(
    string Name,
    string SiteTitle,
    string AdminUser,
    string AdminPassword,
    string AdminEmail,
    string? Domain = null,
    string? PhpVersion = null,
    string DatabaseEngine = "mysql",
    string? DatabaseName = null,
    DatabaseConnectionOptions? DatabaseOptions = null,
    bool Https = true,
    string Locale = "en_US");

public sealed record WordPressSiteResult(
    string ProjectRoot,
    string Domain,
    string DatabaseName,
    string AdminUrl,
    IReadOnlyList<string> Actions);
