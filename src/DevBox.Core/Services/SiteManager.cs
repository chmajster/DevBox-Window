using System.Text.Json;
using System.Text.RegularExpressions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed partial class SiteManager
{
    private readonly string _rootPath;
    private readonly string _wwwRoot;
    private readonly string _sitesMetadataPath;

    public SiteManager(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _wwwRoot = Path.GetFullPath(Path.Combine(_rootPath, "www"));
        _sitesMetadataPath = Path.Combine(_rootPath, "config", "sites.json");
    }

    public IReadOnlyList<SiteDefinition> GetSites()
    {
        if (!File.Exists(_sitesMetadataPath))
        {
            return Array.Empty<SiteDefinition>();
        }

        try
        {
            var json = File.ReadAllText(_sitesMetadataPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<SiteDefinition>();
            }

            var sites = JsonSerializer.Deserialize<List<SiteDefinition>>(json, JsonOptions) ?? new List<SiteDefinition>();
            foreach (var site in sites)
            {
                ValidateLoadedSite(site);
            }
            ValidateLoadedCollection(sites);
            return sites;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or InvalidDataException)
        {
            QuarantineInvalidSitesMetadata();
            return Array.Empty<SiteDefinition>();
        }
    }

    public SiteDefinition Create(string name, string? domain = null, string? documentRoot = null)
    {
        var normalizedName = NormalizeName(name);
        var normalizedDomain = NormalizeDomain(domain ?? $"{normalizedName}.test");
        var root = documentRoot is null
            ? Path.Combine(_wwwRoot, normalizedName)
            : EnsureDocumentRootUnderWww(documentRoot);

        var sites = GetSites().ToList();
        ValidateNewSite(sites, normalizedName, normalizedDomain);

        Directory.CreateDirectory(root);
        var indexPath = Path.Combine(root, "index.php");
        var scaffoldedIndex = false;
        if (documentRoot is null && !File.Exists(indexPath))
        {
            File.WriteAllText(indexPath, "<?php\nphpinfo();\n");
            scaffoldedIndex = true;
        }

        var site = new SiteDefinition(normalizedName, normalizedDomain, root);
        try
        {
            return PersistNewSite(site, sites);
        }
        catch
        {
            if (scaffoldedIndex)
                TryDeleteFile(indexPath);
            throw;
        }
    }

    public SiteDefinition RegisterExisting(string name, string domain, string documentRoot)
    {
        var normalizedName = NormalizeName(name);
        var normalizedDomain = NormalizeDomain(domain);
        var root = EnsureDocumentRootUnderWww(documentRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Site document root was not found: {root}");

        var sites = GetSites().ToList();
        ValidateNewSite(sites, normalizedName, normalizedDomain);

        var site = new SiteDefinition(normalizedName, normalizedDomain, root);
        return PersistNewSite(site, sites);
    }

    public SiteDefinition Update(SiteDefinition site)
    {
        ArgumentNullException.ThrowIfNull(site);
        var normalizedName = NormalizeName(site.Name);
        var normalizedDomain = NormalizeDomain(site.Domain);
        var documentRoot = EnsureDocumentRootUnderWww(site.DocumentRoot);
        var phpVersion = NormalizePhpVersion(site.PhpVersion);

        var sites = GetSites().ToList();
        var index = sites.FindIndex(item => item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException($"Site '{normalizedName}' does not exist.");
        }
        if (sites.Where((_, itemIndex) => itemIndex != index)
            .Any(item => item.Domain.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Domain '{normalizedDomain}' is already assigned to another site.");
        }

        var previous = sites[index];
        if (!previous.Domain.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase))
        {
            DeleteNginxConfig(previous.Domain);
        }

        var updated = site with
        {
            Name = normalizedName,
            Domain = normalizedDomain,
            DocumentRoot = documentRoot,
            PhpVersion = phpVersion
        };
        WriteNginxConfig(updated);
        sites[index] = updated;
        SaveSites(sites);
        return updated;
    }

    public SiteDefinition SetHttps(string name, bool enabled)
    {
        var normalizedName = NormalizeName(name);
        var sites = GetSites().ToList();
        var index = sites.FindIndex(site => site.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException($"Site '{normalizedName}' does not exist.");
        }

        var updated = sites[index] with { HttpsEnabled = enabled };
        WriteNginxConfig(updated);
        sites[index] = updated;
        SaveSites(sites);
        return updated;
    }

    public SiteDefinition SetPhpVersion(string name, string? version)
    {
        var normalizedName = NormalizeName(name);
        var normalizedVersion = NormalizePhpVersion(version);
        var sites = GetSites().ToList();
        var index = sites.FindIndex(site => site.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException($"Site '{normalizedName}' does not exist.");
        }

        if (normalizedVersion is not null)
        {
            var requestedPort = PhpRuntimePoolManager.GetPort(normalizedVersion);
            var collision = sites
                .Where((_, siteIndex) => siteIndex != index)
                .Select(site => site.PhpVersion)
                .Where(existing => !string.IsNullOrWhiteSpace(existing) && !existing.Equals(normalizedVersion, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(existing => PhpRuntimePoolManager.GetPort(existing!) == requestedPort);
            if (collision is not null)
            {
                throw new InvalidOperationException(
                    $"PHP {normalizedVersion} conflicts with PHP {collision} on FastCGI port {requestedPort}. Choose a different runtime version.");
            }
        }

        var updated = sites[index] with { PhpVersion = normalizedVersion };
        WriteNginxConfig(updated);
        sites[index] = updated;
        SaveSites(sites);
        return updated;
    }

    public void Delete(string name, bool deleteDocumentRoot = false)
    {
        var normalizedName = NormalizeName(name);
        var sites = GetSites().ToList();
        var site = sites.FirstOrDefault(item => item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        if (site is null)
        {
            return;
        }

        DeleteNginxConfig(site.Domain);
        sites.Remove(site);
        SaveSites(sites);

        if (deleteDocumentRoot && Directory.Exists(site.DocumentRoot))
        {
            var safeRoot = EnsureDocumentRootUnderWww(site.DocumentRoot);
            Directory.Delete(safeRoot, recursive: true);
        }
    }

    public string GetNginxConfigPath(string domain) =>
        Path.Combine(_rootPath, "config", "nginx", "sites-enabled", $"{NormalizeDomain(domain)}.conf");

    private SiteDefinition PersistNewSite(SiteDefinition site, ICollection<SiteDefinition> sites)
    {
        try
        {
            WriteNginxConfig(site);
            sites.Add(site);
            SaveSites(sites);
            return site;
        }
        catch
        {
            TryDeleteNginxConfig(site.Domain);
            throw;
        }
    }

    private static void ValidateNewSite(IReadOnlyCollection<SiteDefinition> sites, string normalizedName, string normalizedDomain)
    {
        if (sites.Any(site => site.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Site '{normalizedName}' already exists.");
        if (sites.Any(site => site.Domain.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Domain '{normalizedDomain}' is already assigned to another site.");
    }

    private void WriteNginxConfig(SiteDefinition site)
    {
        var configPath = GetNginxConfigPath(site.Domain);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var documentRoot = EnsureDocumentRootUnderWww(site.DocumentRoot);
        var relativeRoot = Path.GetRelativePath(_rootPath, documentRoot).Replace('\\', '/');

        var fastCgiPort = site.PhpVersion is null ? 9084 : PhpRuntimePoolManager.GetPort(site.PhpVersion);
        var applicationLocations = $$"""
    root {{relativeRoot}};
    index index.php index.html;

    location / {
        try_files $uri $uri/ /index.php?$query_string;
    }

    location ~ \.php$ {
        include config/nginx/fastcgi_params;
        fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
        fastcgi_pass 127.0.0.1:{{fastCgiPort}};
    }
""";

        string config;
        if (site.HttpsEnabled)
        {
            var certificate = $"config/ssl/sites/{site.Domain}.crt.pem";
            var privateKey = $"config/ssl/sites/{site.Domain}.key.pem";
            config = $$"""
server {
    listen 80;
    server_name {{site.Domain}};
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    server_name {{site.Domain}};
    ssl_certificate {{certificate}};
    ssl_certificate_key {{privateKey}};
    ssl_protocols TLSv1.2 TLSv1.3;

{{applicationLocations}}}
""";
        }
        else
        {
            config = $$"""
server {
    listen 80;
    server_name {{site.Domain}};
{{applicationLocations}}}
""";
        }

        AtomicWrite(configPath, config.Replace("\n", Environment.NewLine));
    }

    private void DeleteNginxConfig(string domain)
    {
        var configPath = GetNginxConfigPath(domain);
        if (File.Exists(configPath))
        {
            File.Delete(configPath);
        }
    }

    private void TryDeleteNginxConfig(string domain)
    {
        try
        {
            DeleteNginxConfig(domain);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SaveSites(IReadOnlyCollection<SiteDefinition> sites)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_sitesMetadataPath)!);
        var tempPath = _sitesMetadataPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(sites.OrderBy(site => site.Name), JsonOptions));
            if (File.Exists(_sitesMetadataPath))
            {
                File.Replace(tempPath, _sitesMetadataPath, null);
            }
            else
            {
                File.Move(tempPath, _sitesMetadataPath);
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

    private static void AtomicWrite(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ValidateLoadedSite(SiteDefinition? site)
    {
        if (site is null)
        {
            throw new InvalidDataException("Site metadata contains a null entry.");
        }

        _ = NormalizeName(site.Name);
        _ = NormalizeDomain(site.Domain);
        _ = EnsureDocumentRootUnderWww(site.DocumentRoot);
        _ = NormalizePhpVersion(site.PhpVersion);
        if (!string.Equals(site.PhpRuntimeKey, "php", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Site metadata contains an unsupported PHP runtime key.");
        }
    }

    private static void ValidateLoadedCollection(IReadOnlyCollection<SiteDefinition> sites)
    {
        var duplicateName = sites
            .GroupBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateName is not null)
        {
            throw new InvalidDataException($"Site metadata contains duplicate site name '{duplicateName.Key}'.");
        }

        var duplicateDomain = sites
            .GroupBy(site => site.Domain, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDomain is not null)
        {
            throw new InvalidDataException($"Site metadata contains duplicate domain '{duplicateDomain.Key}'.");
        }

        var portCollision = sites
            .Where(site => !string.IsNullOrWhiteSpace(site.PhpVersion))
            .GroupBy(site => PhpRuntimePoolManager.GetPort(site.PhpVersion!))
            .FirstOrDefault(group => group
                .Select(site => site.PhpVersion!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any());
        if (portCollision is not null)
        {
            var versions = string.Join(", ", portCollision
                .Select(site => site.PhpVersion!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(version => version, StringComparer.OrdinalIgnoreCase));
            throw new InvalidDataException($"Site metadata contains PHP runtime versions that collide on FastCGI port {portCollision.Key}: {versions}.");
        }
    }

    private void QuarantineInvalidSitesMetadata()
    {
        if (!File.Exists(_sitesMetadataPath))
        {
            return;
        }

        var quarantinePath = $"{_sitesMetadataPath}.invalid-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.bak";
        try
        {
            File.Move(_sitesMetadataPath, quarantinePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string EnsureDocumentRootUnderWww(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullWwwRoot = _wwwRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!fullPath.StartsWith(fullWwwRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Site document root must be inside the DevBox www directory.");
        }
        return fullPath;
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim().ToLowerInvariant();
        if (!SafeNameRegex().IsMatch(normalized))
        {
            throw new ArgumentException("Site name may contain only letters, digits, dots, hyphens and underscores.", nameof(name));
        }
        return normalized;
    }

    private static string NormalizeDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (!normalized.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("DevBox local domains must end with .test.", nameof(domain));
        }
        if (!DomainRegex().IsMatch(normalized))
        {
            throw new ArgumentException("Invalid local domain.", nameof(domain));
        }
        return normalized;
    }

    private static string? NormalizePhpVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }
        var normalized = version.Trim();
        if (!PhpVersionRegex().IsMatch(normalized))
        {
            throw new ArgumentException("PHP version must use MAJOR.MINOR.PATCH format.", nameof(version));
        }
        return normalized;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeNameRegex();

    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+test$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainRegex();

    [GeneratedRegex("^\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex PhpVersionRegex();
}
