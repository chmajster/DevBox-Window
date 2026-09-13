using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class AddonMarketplaceService : IDisposable
{
    private const int MaximumCatalogBytes = 2 * 1024 * 1024;
    private const int MaximumSourceBytes = 256 * 1024;
    private readonly string _rootPath;
    private readonly string _sourcePath;
    private readonly string _addonsPath;
    private readonly string _localAddonsPath;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public AddonMarketplaceService(string rootPath, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _sourcePath = SafeManagedPath(
            Path.Combine(_rootPath, "config", "addon-marketplace-source.json"),
            "Marketplace source configuration cannot escape the DevBox root or traverse a reparse point.");
        _addonsPath = SafeManagedPath(
            Path.Combine(_rootPath, "config", "addons.json"),
            "Marketplace merged catalog cannot escape the DevBox root or traverse a reparse point.");
        _localAddonsPath = SafeManagedPath(
            Path.Combine(_rootPath, "config", "addons.local.json"),
            "Marketplace local catalog cannot escape the DevBox root or traverse a reparse point.");
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Configure(string catalogUrl, string signatureUrl, string publicKeyPem)
    {
        ThrowIfDisposed();
        var source = new MarketplaceSource(catalogUrl, signatureUrl, publicKeyPem);
        ValidateSource(source);
        EnsureLocalCatalog();
        var sourcePath = EnsureManagedFile(_sourcePath, "Marketplace source configuration cannot traverse a reparse point.");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        AtomicWrite(sourcePath, JsonSerializer.Serialize(source, JsonOptions));
    }

    public bool IsConfigured
    {
        get
        {
            ThrowIfDisposed();
            return File.Exists(EnsureManagedFile(_sourcePath, "Marketplace source configuration cannot traverse a reparse point."));
        }
    }

    public async Task<IReadOnlyList<AddonDefinition>> SyncAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var source = LoadSource();
        EnsureLocalCatalog();
        var catalogBytes = await DownloadBytesAsync(source.CatalogUrl, MaximumCatalogBytes, cancellationToken).ConfigureAwait(false);
        var signatureBytes = await DownloadBytesAsync(source.SignatureUrl, 64 * 1024, cancellationToken).ConfigureAwait(false);
        VerifySignature(source.PublicKeyPem, catalogBytes, signatureBytes);

        JsonArray marketplace;
        try
        {
            marketplace = JsonNode.Parse(catalogBytes) as JsonArray
                ?? throw new InvalidDataException("Marketplace catalog root must be a JSON array.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Marketplace catalog contains invalid JSON.", ex);
        }

        var localPath = EnsureManagedFile(_localAddonsPath, "Marketplace local catalog cannot traverse a reparse point.");
        var merged = MergeCatalogs(LoadCatalogArray(localPath, "Local ADDONS catalog"), marketplace);
        ValidateCatalogWithAddonCatalog(merged);
        var addonsPath = EnsureManagedFile(_addonsPath, "Marketplace merged catalog cannot traverse a reparse point.");
        Directory.CreateDirectory(Path.GetDirectoryName(addonsPath)!);
        AtomicWrite(addonsPath, merged.ToJsonString(JsonOptions));
        return new AddonCatalog(_rootPath).GetAddons();
    }

    public void SaveLocalCatalog(IReadOnlyList<AddonDefinition> addons)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(addons);
        var array = JsonSerializer.SerializeToNode(addons.Select(ToManifestEntry).ToArray(), JsonOptions) as JsonArray
            ?? throw new InvalidDataException("Unable to serialize the local ADDONS catalog.");
        ValidateCatalogWithAddonCatalog(array);
        var localPath = EnsureManagedFile(_localAddonsPath, "Marketplace local catalog cannot traverse a reparse point.");
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        AtomicWrite(localPath, array.ToJsonString(JsonOptions));
    }

    public async Task InstallOrUpdateAsync(string key, bool syncFirst = true, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (syncFirst)
            _ = await SyncAsync(cancellationToken).ConfigureAwait(false);
        var addon = new AddonCatalog(_rootPath).GetAddons().FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"ADDON '{key}' was not found in the marketplace/local catalog.");
        using var installer = new AddonInstaller(_rootPath, _httpClient);
        await installer.InstallAsync(addon, cancellationToken).ConfigureAwait(false);
    }

    public async Task UninstallAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var addon = new AddonCatalog(_rootPath).GetAddons().FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"ADDON '{key}' was not found in the local catalog.");
        using var installer = new AddonInstaller(_rootPath, _httpClient);
        await installer.UninstallAsync(addon, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private MarketplaceSource LoadSource()
    {
        var sourcePath = EnsureManagedFile(_sourcePath, "Marketplace source configuration cannot traverse a reparse point.");
        if (!File.Exists(sourcePath))
            throw new InvalidOperationException("ADDONS marketplace is not configured. Configure a signed catalog source first.");
        try
        {
            var source = JsonSerializer.Deserialize<MarketplaceSource>(ReadTextWithLimit(sourcePath, MaximumSourceBytes, "Marketplace source configuration"), JsonOptions)
                ?? throw new InvalidDataException("Marketplace source configuration is empty.");
            ValidateSource(source);
            return source;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("config/addon-marketplace-source.json contains invalid JSON.", ex);
        }
    }

    private void EnsureLocalCatalog()
    {
        var localPath = EnsureManagedFile(_localAddonsPath, "Marketplace local catalog cannot traverse a reparse point.");
        if (File.Exists(localPath))
            return;

        _ = new AddonCatalog(_rootPath).GetAddons();
        var addonsPath = EnsureManagedFile(_addonsPath, "Marketplace merged catalog cannot traverse a reparse point.");
        var local = LoadCatalogArray(addonsPath, "ADDONS catalog");
        ValidateCatalogWithAddonCatalog(local);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        AtomicWrite(localPath, local.ToJsonString(JsonOptions));
    }

    private static JsonArray LoadCatalogArray(string path, string label)
    {
        try
        {
            return JsonNode.Parse(ReadTextWithLimit(path, MaximumCatalogBytes, label)) as JsonArray
                ?? throw new InvalidDataException($"{label} root must be a JSON array.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{label} contains invalid JSON.", ex);
        }
    }

    private static JsonArray MergeCatalogs(JsonArray local, JsonArray marketplace)
    {
        var byKey = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { local, marketplace })
        {
            foreach (var node in source)
            {
                if (node is not JsonObject item)
                    throw new InvalidDataException("ADDONS catalog contains a non-object entry.");
                var keyNode = item.FirstOrDefault(pair => pair.Key.Equals("key", StringComparison.OrdinalIgnoreCase)).Value;
                string? key = null;
                if (keyNode is JsonValue keyValue && keyValue.TryGetValue<string>(out var parsedKey))
                    key = parsedKey;
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidDataException("ADDONS catalog entry has a missing or non-string key.");
                byKey[key] = item.DeepClone() as JsonObject ?? throw new InvalidDataException("Unable to clone ADDONS catalog entry.");
            }
        }
        var result = new JsonArray();
        foreach (var pair in byKey.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            result.Add(pair.Value);
        return result;
    }

    private void ValidateCatalogWithAddonCatalog(JsonArray merged)
    {
        var tempRoot = SafeManagedPath(
            Path.Combine(_rootPath, "tmp", "addon-marketplace-validate", Guid.NewGuid().ToString("N")),
            "Marketplace validation directory cannot escape the DevBox root or traverse a reparse point.");
        try
        {
            var tempConfig = PathSafety.EnsureUnderRootWithoutReparsePoints(
                tempRoot,
                Path.Combine(tempRoot, "config"),
                "Marketplace validation config path cannot traverse a reparse point.");
            var tempWww = PathSafety.EnsureUnderRootWithoutReparsePoints(
                tempRoot,
                Path.Combine(tempRoot, "www"),
                "Marketplace validation www path cannot traverse a reparse point.");
            Directory.CreateDirectory(tempConfig);
            Directory.CreateDirectory(tempWww);
            File.WriteAllText(Path.Combine(tempConfig, "addons.json"), merged.ToJsonString(JsonOptions));
            _ = new AddonCatalog(tempRoot).GetAddons();
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task<byte[]> DownloadBytesAsync(string url, int maximumBytes, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Marketplace URLs must use HTTPS.");
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
            throw new InvalidDataException("Marketplace response exceeds the configured size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Marketplace response exceeds the configured size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void VerifySignature(string publicKeyPem, byte[] catalogBytes, byte[] signaturePayload)
    {
        byte[] signature;
        try
        {
            var text = Encoding.UTF8.GetString(signaturePayload).Trim();
            signature = Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Marketplace detached signature must be base64 encoded.", ex);
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(publicKeyPem);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Marketplace RSA public key PEM is invalid.", ex);
        }
        if (!rsa.VerifyData(catalogBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("Marketplace catalog RSA-SHA256 signature verification failed.");
    }

    private static void ValidateSource(MarketplaceSource source)
    {
        foreach (var value in new[] { source.CatalogUrl, source.SignatureUrl })
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Marketplace catalog and signature URLs must use HTTPS.");
        }
        if (string.IsNullOrWhiteSpace(source.PublicKeyPem) ||
            source.PublicKeyPem.Length > 64 * 1024 ||
            !source.PublicKeyPem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal))
            throw new InvalidDataException("Marketplace source requires a bounded RSA public key in PEM format.");
    }

    private object ToManifestEntry(AddonDefinition addon)
    {
        ArgumentNullException.ThrowIfNull(addon);
        return new
        {
            key = addon.Key,
            displayName = addon.DisplayName,
            description = addon.Description,
            installRelativePath = ToRootRelativePath(addon.InstallPath, "ADDON install path"),
            entryPointRelativePath = ToRootRelativePath(addon.EntryPointPath, "ADDON entry point"),
            localUrl = addon.LocalUrl,
            requiredPhpExtensions = addon.RequiredPhpExtensions,
            version = addon.Version,
            downloadUrl = addon.DownloadUrl,
            sha256 = addon.Sha256,
            archiveRootDirectory = addon.ArchiveRootDirectory
        };
    }

    private string ToRootRelativePath(string path, string label)
    {
        try
        {
            var full = PathSafety.EnsureUnderRootWithoutReparsePoints(
                _rootPath,
                path,
                $"{label} must be inside the DevBox root and cannot traverse a reparse point.");
            return Path.GetRelativePath(_rootPath, full).Replace('\\', '/');
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    private string EnsureManagedFile(string path, string message) =>
        SafeManagedPath(path, message);

    private string SafeManagedPath(string path, string message) =>
        PathSafety.EnsureUnderRootWithoutReparsePoints(_rootPath, path, message);

    private static string ReadTextWithLimit(string path, int maximumBytes, string label)
    {
        var info = new FileInfo(path);
        if (info.Length > maximumBytes)
            throw new InvalidDataException($"{label} exceeds the configured size limit.");
        return File.ReadAllText(path);
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record MarketplaceSource(string CatalogUrl, string SignatureUrl, string PublicKeyPem);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
