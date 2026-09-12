using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed partial class ProjectWorkspaceService
{
    public const string ManifestFileName = "devbox.json";

    private readonly string _rootPath;
    private readonly string _wwwRoot;
    private readonly SiteManager _siteManager;
    private readonly PhpExtensionInspector _phpExtensionInspector;
    private readonly LocalCertificateManager _certificateManager;

    public ProjectWorkspaceService(
        string rootPath,
        SiteManager siteManager,
        PhpExtensionInspector phpExtensionInspector,
        LocalCertificateManager certificateManager)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _wwwRoot = Path.GetFullPath(Path.Combine(_rootPath, "www"));
        _siteManager = siteManager ?? throw new ArgumentNullException(nameof(siteManager));
        _phpExtensionInspector = phpExtensionInspector ?? throw new ArgumentNullException(nameof(phpExtensionInspector));
        _certificateManager = certificateManager ?? throw new ArgumentNullException(nameof(certificateManager));
    }

    public ProjectDetectionResult Detect(string projectPath)
    {
        var root = RequireExistingDirectory(projectPath);
        var evidence = new List<string>();
        var composer = TryReadComposer(root);
        var requiredExtensions = ReadRequiredExtensions(composer);

        ProjectKind kind;
        if (File.Exists(Path.Combine(root, "artisan")) || ComposerRequires(composer, "laravel/framework"))
        {
            kind = ProjectKind.Laravel;
            evidence.Add("Laravel artisan/framework dependency detected.");
        }
        else if (File.Exists(Path.Combine(root, "bin", "console")) || ComposerRequires(composer, "symfony/framework-bundle"))
        {
            kind = ProjectKind.Symfony;
            evidence.Add("Symfony console/framework dependency detected.");
        }
        else if (File.Exists(Path.Combine(root, "wp-config.php")) ||
                 File.Exists(Path.Combine(root, "wp-includes", "version.php")))
        {
            kind = ProjectKind.WordPress;
            evidence.Add("WordPress configuration/core files detected.");
        }
        else if (File.Exists(Path.Combine(root, "package.json")) && composer is null)
        {
            kind = ProjectKind.Node;
            evidence.Add("package.json detected without a PHP Composer project.");
        }
        else if (composer is not null)
        {
            kind = ProjectKind.Php;
            evidence.Add("composer.json detected.");
        }
        else if (Directory.EnumerateFiles(root, "*.php", SearchOption.TopDirectoryOnly).Any())
        {
            kind = ProjectKind.Php;
            evidence.Add("PHP source files detected.");
        }
        else
        {
            kind = ProjectKind.Unknown;
            evidence.Add("No supported framework marker was detected.");
        }

        if (requiredExtensions.Count > 0)
            evidence.Add($"Composer requires {requiredExtensions.Count} PHP extension(s).");

        return new ProjectDetectionResult(kind, root, evidence, requiredExtensions);
    }

    public SiteDefinition Create(ProjectCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == ProjectKind.Node)
            throw new InvalidOperationException("Node-only projects are not served by the current PHP/Nginx Site model. Import the project first and run its Node service separately.");

        var projectRoot = Path.Combine(_wwwRoot, NormalizeProjectDirectoryName(request.Name));
        _ = PathSafety.EnsureUnderRootWithoutReparsePoints(
            _wwwRoot, projectRoot, "Project destination must remain inside DevBox www and cannot traverse a reparse point.");
        var projectRootExisted = Directory.Exists(projectRoot);
        if (projectRootExisted && Directory.EnumerateFileSystemEntries(projectRoot).Any())
            throw new InvalidOperationException($"Project destination is not empty: {projectRoot}");

        var rollbackDomain = request.Domain ?? LocalDomainName.FromName(NormalizeProjectDirectoryName(request.Name));
        var tlsRollback = new TlsRollbackStateService(_rootPath);
        var tlsState = tlsRollback.Capture(rollbackDomain);
        var siteCreated = false;
        try
        {
            Directory.CreateDirectory(projectRoot);
            var documentRoot = UsesPublicDocumentRoot(request.Kind)
                ? Path.Combine(projectRoot, "public")
                : projectRoot;
            Directory.CreateDirectory(documentRoot);

            var site = _siteManager.Create(request.Name, request.Domain, documentRoot);
            siteCreated = true;
            if (!string.IsNullOrWhiteSpace(request.PhpVersion))
                site = _siteManager.SetPhpVersion(site.Name, request.PhpVersion);

            if (request.Https)
            {
                _certificateManager.Ensure(site.Domain);
                site = _siteManager.SetHttps(site.Name, true);
            }

            EnsurePlaceholderEntryPoint(projectRoot, documentRoot, request.Kind);
            SaveManifest(projectRoot, new DevBoxProjectManifest(
                DevBoxProjectManifest.CurrentSchemaVersion,
                site.Name,
                site.Domain,
                request.Kind,
                site.PhpVersion,
                request.NodeVersion,
                NormalizeDatabaseEngine(request.DatabaseEngine),
                string.IsNullOrWhiteSpace(request.DatabaseName) ? site.Name.Replace('-', '_') : request.DatabaseName.Trim(),
                site.HttpsEnabled,
                (request.Addons ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray()));

            return site;
        }
        catch (Exception original)
        {
            var rollbackActions = new List<Action>();
            if (siteCreated)
                rollbackActions.Add(() => _siteManager.Delete(request.Name));
            rollbackActions.Add(() => tlsRollback.Restore(tlsState));
            rollbackActions.Add(() => RollbackOwnedProjectDirectory(projectRoot, projectRootExisted));
            RollbackExecutor.RethrowAfterRollback(original, rollbackActions.ToArray());
            throw new InvalidOperationException("Project create rollback executor returned unexpectedly.");
        }
    }

    public SiteDefinition Import(ProjectImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = RequireExistingDirectory(request.SourcePath);
        if (request.CopyIntoDevBox && (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Project import source cannot be a reparse point: {source}");
        var detection = Detect(source);

        var projectRoot = request.CopyIntoDevBox
            ? Path.Combine(_wwwRoot, NormalizeProjectDirectoryName(request.Name))
            : EnsureUnderWww(source);
        _ = PathSafety.EnsureUnderRootWithoutReparsePoints(
            _wwwRoot, projectRoot, "Project import destination must remain inside DevBox www and cannot traverse a reparse point.");
        var projectRootExisted = Directory.Exists(projectRoot);
        if (request.CopyIntoDevBox && projectRootExisted && Directory.EnumerateFileSystemEntries(projectRoot).Any())
            throw new InvalidOperationException($"Import destination is not empty: {projectRoot}");

        var manifestPath = Path.Combine(projectRoot, ManifestFileName);
        var previousManifest = !request.CopyIntoDevBox && File.Exists(manifestPath) ? File.ReadAllBytes(manifestPath) : null;
        var rollbackDomain = request.Domain ?? LocalDomainName.FromName(NormalizeProjectDirectoryName(request.Name));
        var tlsRollback = new TlsRollbackStateService(_rootPath);
        var tlsState = tlsRollback.Capture(rollbackDomain);
        var siteCreated = false;

        try
        {
            if (request.CopyIntoDevBox)
            {
                Directory.CreateDirectory(projectRoot);
                CopyDirectorySafely(source, projectRoot);
            }

            var documentRoot = ResolveDocumentRoot(projectRoot, detection.Kind);
            var site = _siteManager.Create(request.Name, request.Domain, documentRoot);
            siteCreated = true;
            if (!string.IsNullOrWhiteSpace(request.PhpVersion))
                site = _siteManager.SetPhpVersion(site.Name, request.PhpVersion);
            if (request.Https)
            {
                _certificateManager.Ensure(site.Domain);
                site = _siteManager.SetHttps(site.Name, true);
            }

            SaveManifest(projectRoot, new DevBoxProjectManifest(
                DevBoxProjectManifest.CurrentSchemaVersion,
                site.Name,
                site.Domain,
                detection.Kind,
                site.PhpVersion,
                null,
                "mysql",
                site.Name.Replace('-', '_'),
                site.HttpsEnabled,
                Array.Empty<string>()));

            return site;
        }
        catch (Exception original)
        {
            var rollbackActions = new List<Action>();
            if (siteCreated)
                rollbackActions.Add(() => _siteManager.Delete(request.Name));
            rollbackActions.Add(() => tlsRollback.Restore(tlsState));
            rollbackActions.Add(request.CopyIntoDevBox
                ? () => RollbackOwnedProjectDirectory(projectRoot, projectRootExisted)
                : () => RestoreManifest(manifestPath, previousManifest));
            RollbackExecutor.RethrowAfterRollback(original, rollbackActions.ToArray());
            throw new InvalidOperationException("Project import rollback executor returned unexpectedly.");
        }
    }

    public DevBoxProjectManifest? LoadManifest(string projectPath)
    {
        var root = Path.GetFullPath(projectPath);
        var path = Path.Combine(root, ManifestFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            var manifest = JsonSerializer.Deserialize<DevBoxProjectManifest>(File.ReadAllText(path), JsonOptions);
            if (manifest is null)
                return null;
            ValidateManifest(manifest);
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("devbox.json contains invalid JSON.", ex);
        }
    }

    public void SaveManifest(string projectPath, DevBoxProjectManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateManifest(manifest);
        var root = RequireExistingDirectory(projectPath);
        var path = Path.Combine(root, ManifestFileName);
        AtomicWrite(path, JsonSerializer.Serialize(manifest, JsonOptions));
    }

    public async Task<ProjectHealthReport> CheckHealthAsync(SiteDefinition site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        var projectRoot = ResolveProjectRoot(site.DocumentRoot);
        var detection = Detect(projectRoot);
        var checks = new List<ProjectHealthCheck>();

        checks.Add(Directory.Exists(site.DocumentRoot)
            ? Healthy("document-root", "Document root", site.DocumentRoot)
            : Error("document-root", "Document root", $"Missing directory: {site.DocumentRoot}"));

        var vhost = _siteManager.GetNginxConfigPath(site.Domain);
        checks.Add(File.Exists(vhost)
            ? Healthy("nginx-vhost", "Nginx vhost", vhost)
            : Error("nginx-vhost", "Nginx vhost", $"Missing generated vhost: {vhost}", true));

        var phpRoot = site.PhpVersion is null
            ? Path.Combine(_rootPath, "runtime", "php", "current")
            : Path.Combine(_rootPath, "runtime", "php", site.PhpVersion);
        var phpCgi = Path.Combine(phpRoot, "php-cgi.exe");
        checks.Add(File.Exists(phpCgi)
            ? Healthy("php-runtime", "PHP runtime", phpCgi)
            : Error("php-runtime", "PHP runtime", $"Missing PHP FastCGI executable: {phpCgi}"));

        var manifestPath = Path.Combine(projectRoot, ManifestFileName);
        checks.Add(File.Exists(manifestPath)
            ? Healthy("manifest", "Project manifest", manifestPath)
            : new ProjectHealthCheck("manifest", "Project manifest", ProjectHealthState.Warning, "devbox.json is missing.", true));

        var entryPoint = Path.Combine(site.DocumentRoot, "index.php");
        if (detection.Kind != ProjectKind.Node)
        {
            checks.Add(File.Exists(entryPoint)
                ? Healthy("entry-point", "Application entry point", entryPoint)
                : new ProjectHealthCheck("entry-point", "Application entry point", ProjectHealthState.Warning, $"index.php was not found in {site.DocumentRoot}."));
        }

        if (site.HttpsEnabled)
        {
            var certificate = Path.Combine(_rootPath, "config", "ssl", "sites", $"{site.Domain}.crt.pem");
            checks.Add(_certificateManager.IsMaterialValid(site.Domain)
                ? Healthy("certificate", "TLS certificate", certificate)
                : Error("certificate", "TLS certificate", "HTTPS is enabled but certificate material is missing, invalid, expired, mismatched, or issued for another domain.", true));
        }

        if (detection.RequiredPhpExtensions.Count > 0)
        {
            if (site.PhpVersion is null)
            {
                var extensionResult = await _phpExtensionInspector.CheckAsync(detection.RequiredPhpExtensions, cancellationToken).ConfigureAwait(false);
                if (!extensionResult.RuntimeAvailable)
                {
                    checks.Add(Error("php-extensions", "PHP extensions", extensionResult.Error ?? "PHP CLI is unavailable."));
                }
                else if (extensionResult.Missing.Count == 0)
                {
                    checks.Add(Healthy("php-extensions", "PHP extensions", "All Composer ext-* requirements are loaded."));
                }
                else
                {
                    checks.Add(Error(
                        "php-extensions",
                        "PHP extensions",
                        $"Missing: {string.Join(", ", extensionResult.Missing)}",
                        CanConfigureExtensions(extensionResult.Missing)));
                }
            }
            else
            {
                checks.Add(new ProjectHealthCheck(
                    "php-extensions",
                    "PHP extensions",
                    ProjectHealthState.Warning,
                    $"Composer requires: {string.Join(", ", detection.RequiredPhpExtensions)}. Automatic loaded-module verification currently targets the global PHP runtime; this site uses PHP {site.PhpVersion}."));
            }
        }

        return new ProjectHealthReport(site, detection.Kind, checks, detection.RequiredPhpExtensions);
    }

    public async Task<ProjectRepairResult> RepairAsync(SiteDefinition site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        var repaired = new List<string>();
        var projectRoot = ResolveProjectRoot(site.DocumentRoot);
        var detection = Detect(projectRoot);

        _siteManager.Update(site);
        repaired.Add("Regenerated Nginx vhost from site metadata.");

        if (site.HttpsEnabled)
        {
            _certificateManager.Ensure(site.Domain);
            _siteManager.SetHttps(site.Name, true);
            repaired.Add("Ensured local TLS certificate and HTTPS vhost configuration.");
        }

        if (!File.Exists(Path.Combine(projectRoot, ManifestFileName)))
        {
            SaveManifest(projectRoot, new DevBoxProjectManifest(
                DevBoxProjectManifest.CurrentSchemaVersion,
                site.Name,
                site.Domain,
                detection.Kind,
                site.PhpVersion,
                null,
                "mysql",
                site.Name.Replace('-', '_'),
                site.HttpsEnabled,
                Array.Empty<string>()));
            repaired.Add("Created devbox.json from current site metadata.");
        }

        if (site.PhpVersion is null && detection.RequiredPhpExtensions.Count > 0)
        {
            var available = detection.RequiredPhpExtensions.Where(IsExtensionBinaryAvailable).ToArray();
            if (available.Length > 0 && _phpExtensionInspector.EnsureConfigured(available))
                repaired.Add($"Enabled Composer-required PHP extensions available in the active runtime: {string.Join(", ", available)}.");
        }

        var report = await CheckHealthAsync(site, cancellationToken).ConfigureAwait(false);
        var remaining = report.Checks
            .Where(check => check.State == ProjectHealthState.Error)
            .Select(check => $"{check.DisplayName}: {check.Details}")
            .ToArray();
        return new ProjectRepairResult(repaired, remaining);
    }

    public string ResolveProjectRoot(string documentRoot)
    {
        var root = Path.GetFullPath(documentRoot);
        if (Path.GetFileName(root).Equals("public", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(root)?.FullName;
            if (parent is not null &&
                (File.Exists(Path.Combine(parent, "artisan")) ||
                 File.Exists(Path.Combine(parent, "composer.json")) ||
                 File.Exists(Path.Combine(parent, ManifestFileName))))
            {
                return parent;
            }
        }
        return root;
    }

    private static void RollbackOwnedProjectDirectory(string projectRoot, bool existedBefore)
    {
        if (!Directory.Exists(projectRoot))
            return;
        if (!existedBefore)
        {
            Directory.Delete(projectRoot, recursive: true);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(projectRoot))
            File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(projectRoot))
            Directory.Delete(directory, recursive: true);
    }

    private static void RestoreManifest(string path, byte[]? previous)
    {
        if (previous is null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, previous);
    }

    private static bool UsesPublicDocumentRoot(ProjectKind kind) =>
        kind is ProjectKind.Laravel or ProjectKind.Symfony;

    private static string ResolveDocumentRoot(string projectRoot, ProjectKind kind)
    {
        if (!UsesPublicDocumentRoot(kind))
            return projectRoot;

        var publicRoot = Path.Combine(projectRoot, "public");
        return Directory.Exists(publicRoot) ? publicRoot : projectRoot;
    }

    private static void EnsurePlaceholderEntryPoint(string projectRoot, string documentRoot, ProjectKind kind)
    {
        if (kind is ProjectKind.Laravel or ProjectKind.Symfony or ProjectKind.WordPress)
        {
            var marker = Path.Combine(projectRoot, ".devbox-scaffold-pending");
            if (!File.Exists(marker))
                File.WriteAllText(marker, $"{kind} project registered by DevBox. Run the scaffold action to install framework files.{Environment.NewLine}");
            return;
        }

        var index = Path.Combine(documentRoot, "index.php");
        if (!File.Exists(index))
            File.WriteAllText(index, "<?php\nphpinfo();\n");
    }

    private static JsonDocument? TryReadComposer(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "composer.json");
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ComposerRequires(JsonDocument? composer, string package)
    {
        if (composer is null)
            return false;

        return HasProperty(composer.RootElement, "require", package) ||
               HasProperty(composer.RootElement, "require-dev", package);
    }

    private static bool HasProperty(JsonElement root, string section, string name)
    {
        if (!root.TryGetProperty(section, out var objectValue) || objectValue.ValueKind != JsonValueKind.Object)
            return false;
        return objectValue.EnumerateObject().Any(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ReadRequiredExtensions(JsonDocument? composer)
    {
        if (composer is null)
            return Array.Empty<string>();

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in new[] { "require", "require-dev" })
        {
            if (!composer.RootElement.TryGetProperty(section, out var requirements) || requirements.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var property in requirements.EnumerateObject())
            {
                if (!property.Name.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
                    continue;
                var extension = property.Name[4..].Trim();
                if (SafeExtensionNameRegex().IsMatch(extension))
                    extensions.Add(extension);
            }
        }
        return extensions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private bool CanConfigureExtensions(IReadOnlyCollection<string> extensions) =>
        extensions.All(extension => extension.Equals("json", StringComparison.OrdinalIgnoreCase) || IsExtensionBinaryAvailable(extension));

    private bool IsExtensionBinaryAvailable(string extension)
    {
        if (extension.Equals("json", StringComparison.OrdinalIgnoreCase))
            return true;
        return File.Exists(Path.Combine(_rootPath, "runtime", "php", "current", "ext", $"php_{extension}.dll"));
    }

    private string EnsureUnderWww(string path)
    {
        return PathSafety.EnsureUnderRootWithoutReparsePoints(
            _wwwRoot,
            path,
            "Existing projects can only be registered in-place when they are already inside DevBox www and the path does not traverse a reparse point. Enable CopyIntoDevBox for external projects.");
    }

    private static string RequireExistingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"Project directory was not found: {full}");
        return full;
    }

    private static string NormalizeProjectDirectoryName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var value = name.Trim().ToLowerInvariant();
        if (!SafeProjectNameRegex().IsMatch(value))
            throw new ArgumentException("Project name may contain only letters, digits, dots, hyphens and underscores.", nameof(name));
        return value;
    }

    private static string NormalizeDatabaseEngine(string value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "mysql" : value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "mysql" or "mariadb" or "postgresql" or "none" => normalized,
            _ => throw new ArgumentException("Database engine must be mysql, mariadb, postgresql or none.", nameof(value))
        };
    }

    private static void ValidateManifest(DevBoxProjectManifest manifest)
    {
        if (manifest.SchemaVersion != DevBoxProjectManifest.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported devbox.json schema version: {manifest.SchemaVersion}.");
        _ = NormalizeProjectDirectoryName(manifest.Name);
        try
        {
            _ = LocalCertificateManager.NormalizeDomain(manifest.Domain);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Manifest domain is not a valid .test domain.", ex);
        }
        _ = NormalizeDatabaseEngine(manifest.DatabaseEngine);
        if (manifest.Addons.Any(addon => string.IsNullOrWhiteSpace(addon) || !SafeAddonKeyRegex().IsMatch(addon)))
            throw new InvalidDataException("Manifest contains an invalid addon key.");
    }

    private static void CopyDirectorySafely(string source, string destination)
    {
        var sourcePath = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destinationPath = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sourcePrefix = sourcePath + Path.DirectorySeparatorChar;
        var destinationPrefix = destinationPath + Path.DirectorySeparatorChar;
        if (destinationPrefix.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Import destination cannot be inside the source project directory.");
        if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Project import source cannot be a reparse point: {sourcePath}");

        Directory.CreateDirectory(destinationPath);
        var pending = new Stack<string>();
        pending.Push(sourcePath);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Project import does not follow reparse points: {file}");
                var relative = Path.GetRelativePath(sourcePath, file);
                var target = Path.Combine(destinationPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }

            foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Project import does not follow reparse points: {directory}");
                var relative = Path.GetRelativePath(sourcePath, directory);
                Directory.CreateDirectory(Path.Combine(destinationPath, relative));
                pending.Push(directory);
            }
        }
    }

    private static ProjectHealthCheck Healthy(string key, string name, string details) =>
        new(key, name, ProjectHealthState.Healthy, details);

    private static ProjectHealthCheck Error(string key, string name, string details, bool canRepair = false) =>
        new(key, name, ProjectHealthState.Error, details, canRepair);

    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeProjectNameRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeExtensionNameRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeAddonKeyRegex();
}
