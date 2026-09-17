using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class SupportBundleService
{
    public const int BundleSchemaVersion = 1;
    private const long MaxConfigurationBytes = 2L * 1024 * 1024;
    private const int MaxLogBytes = 512 * 1024;
    private const long MaxTotalLogBytes = 5L * 1024 * 1024;
    private const int MaxLogFiles = 20;
    private const int MaxLogCandidates = 500;

    private static readonly string[] RedactedConfigurationFiles =
    [
        "appsettings.json",
        "services.json",
        "sites.json",
        "database-runtimes.json",
        "environment-profiles.json",
        "project-profiles.json",
        "runtime-catalog.json",
        "addons.json",
        "addons.local.json",
        "addon-marketplace-source.json",
        "schema-versions.json",
        "nginx/nginx.conf",
        "nginx/fastcgi_params",
        "php/php.ini",
        "mysql/my.ini"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex AuthorizationRegex = new(
        @"(?im)(\bauthorization\s*[:=]\s*)(?:bearer|basic)\s+[A-Za-z0-9+/_=.~\-]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex SecretAssignmentRegex = new(
        "(?im)([\"']?)([A-Za-z0-9_.-]*(?:password|passwd|pwd|secret|token|api[_-]?key|cookie|connectionstring|private[_-]?key|credential))\\1(\\s*[:=]\\s*)(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s;\\r\\n]+)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex BearerRegex = new(
        @"(?i)\bbearer\s+[A-Za-z0-9+/_=.~\-]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex UrlCredentialRegex = new(
        @"(?i)(https?://[^:/\s]+:)[^@\s/]+@",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex JwtRegex = new(
        @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex PrivateKeyRegex = new(
        @"-----BEGIN [^-\r\n]*PRIVATE KEY-----.*?-----END [^-\r\n]*PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly string _rootPath;

    public SupportBundleService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public SupportBundleResult Create()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var outputRoot = PathSafety.EnsureUnderRootWithoutReparsePoints(
            _rootPath,
            Path.Combine(_rootPath, "backups", "support-bundles"),
            "Support bundle output directory cannot traverse a reparse point.");
        Directory.CreateDirectory(outputRoot);

        var suffix = $"{createdAt:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var archivePath = Path.Combine(outputRoot, $"devbox-support-{suffix}.zip");
        var temporaryPath = Path.Combine(outputRoot, $".devbox-support-{suffix}.tmp");
        var entries = new List<string>();
        var includedLogCount = 0;

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                AddJsonEntry(archive, entries, "diagnostics.json", BuildSafeSection("diagnostics", BuildDiagnostics));
                AddJsonEntry(archive, entries, "system.json", BuildSafeSection("system", () => BuildSystemSummary(createdAt)));
                AddJsonEntry(archive, entries, "services.json", BuildSafeSection("services", BuildServiceSummary));
                AddJsonEntry(archive, entries, "runtime-status.json", BuildSafeSection("runtimes", BuildRuntimeSummary));
                AddJsonEntry(archive, entries, "database-status.json", BuildSafeSection("databases", BuildDatabaseSummary));

                AddRedactedConfigurations(archive, entries);
                includedLogCount = AddRedactedLogs(archive, entries);

                var manifestEntries = entries.ToArray();
                AddJsonEntry(archive, entries, "manifest.json", new
                {
                    schemaVersion = BundleSchemaVersion,
                    generatedAtUtc = createdAt,
                    includedEntries = manifestEntries,
                    limits = new
                    {
                        maxConfigurationBytes = MaxConfigurationBytes,
                        maxLogBytes = MaxLogBytes,
                        maxTotalLogBytes = MaxTotalLogBytes,
                        maxLogFiles = MaxLogFiles
                    },
                    privacy = new
                    {
                        redacted = true,
                        excluded = new[]
                        {
                            "config/secrets.dpapi.json and secret values",
                            "private keys, PFX files and certificate private material",
                            "database contents and database backups",
                            "project source trees, devbox.json and devbox.lock.json",
                            "raw process arguments"
                        }
                    }
                });
            }

            File.Move(temporaryPath, archivePath);
            var size = new FileInfo(archivePath).Length;
            return new SupportBundleResult(
                archivePath,
                size,
                createdAt,
                entries.Count,
                includedLogCount,
                entries.ToArray());
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private object BuildSafeSection(string section, Func<object> builder)
    {
        try
        {
            return builder();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or PlatformNotSupportedException or JsonException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException or RegexMatchTimeoutException)
        {
            return new
            {
                section,
                available = false,
                error = RedactText(ex.Message)
            };
        }
    }

    private object BuildDiagnostics()
    {
        var report = new AdvancedDiagnosticsService(_rootPath).Run();
        return new
        {
            report.GeneratedAtUtc,
            report.HasErrors,
            report.HasWarnings,
            Findings = report.Findings.Select(item => new
            {
                item.Key,
                item.Area,
                Severity = item.Severity.ToString(),
                Summary = RedactText(item.Summary),
                Details = RedactText(item.Details),
                SuggestedAction = item.SuggestedAction is null ? null : RedactText(item.SuggestedAction)
            }).ToArray()
        };
    }

    private object BuildSystemSummary(DateTimeOffset createdAt) => new
    {
        GeneratedAtUtc = createdAt,
        DevBoxCoreVersion = typeof(SupportBundleService).Assembly.GetName().Version?.ToString() ?? "unknown",
        RootPath = "<DEVBOX_ROOT>",
        OperatingSystem = RuntimeInformation.OSDescription,
        OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Framework = RuntimeInformation.FrameworkDescription,
        Environment.Is64BitOperatingSystem,
        Culture = CultureInfo.CurrentCulture.Name
    };

    private object BuildServiceSummary()
    {
        var services = new ServiceCatalog(_rootPath).GetServices();
        using var processes = new ProcessManager();
        return services.Select(service =>
        {
            var status = processes.GetStatus(service);
            return new
            {
                service.Key,
                service.DisplayName,
                service.Port,
                service.Version,
                State = status.State.ToString(),
                status.ProcessId,
                ExecutablePath = RedactText(service.ExecutablePath),
                WorkingDirectory = RedactText(service.WorkingDirectory),
                LogPath = service.LogPath is null ? null : RedactText(service.LogPath)
            };
        }).ToArray();
    }

    private object BuildRuntimeSummary()
    {
        using var runtimes = new RuntimePlatformService(_rootPath);
        return runtimes.GetStatuses().Select(status => new
        {
            status.Package.Key,
            status.Package.DisplayName,
            status.Package.Version,
            status.Package.Architecture,
            status.Package.Channel,
            status.Package.EndOfLifeDate,
            status.Installed,
            status.Active,
            status.Valid,
            SupportState = status.SupportState.ToString()
        }).ToArray();
    }

    private object BuildDatabaseSummary()
    {
        using var databases = new DatabaseRuntimeService(_rootPath);
        return databases.GetInstances().Select(instance => new
        {
            Engine = instance.Engine.ToString(),
            instance.Version,
            instance.Port,
            instance.Initialized,
            State = instance.State.ToString(),
            instance.ProcessId,
            RuntimePath = RedactText(instance.RuntimePath),
            DataPath = RedactText(instance.DataPath),
            LogPath = RedactText(instance.LogPath)
        }).ToArray();
    }

    private void AddRedactedConfigurations(ZipArchive archive, List<string> entries)
    {
        var configRoot = PathSafety.EnsureUnderRootWithoutReparsePoints(
            _rootPath,
            Path.Combine(_rootPath, "config"),
            "Support bundle configuration root cannot traverse a reparse point.");

        foreach (var relative in RedactedConfigurationFiles)
        {
            var platformRelative = relative.Replace('/', Path.DirectorySeparatorChar);
            var path = PathSafety.EnsureUnderRootWithoutReparsePoints(
                configRoot,
                Path.Combine(configRoot, platformRelative),
                $"Support bundle configuration path '{relative}' cannot traverse a reparse point.");
            if (!File.Exists(path))
                continue;

            var archiveName = "config-redacted/" + relative.Replace('\\', '/');
            try
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    AddTextEntry(archive, entries, archiveName + ".omitted.txt", "Omitted: reparse-point files are not exported.");
                    continue;
                }
                if (info.Length > MaxConfigurationBytes)
                {
                    AddTextEntry(archive, entries, archiveName + ".omitted.txt", $"Omitted: file exceeds {MaxConfigurationBytes} bytes.");
                    continue;
                }

                var text = File.ReadAllText(path);
                var redacted = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? RedactJson(text)
                    : RedactText(text);
                AddTextEntry(archive, entries, archiveName, redacted);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AddTextEntry(
                    archive,
                    entries,
                    archiveName + ".omitted.txt",
                    "Omitted: " + RedactText(ex.Message));
            }
        }
    }

    private int AddRedactedLogs(ZipArchive archive, List<string> entries)
    {
        var logsRoot = PathSafety.EnsureUnderRootWithoutReparsePoints(
            _rootPath,
            Path.Combine(_rootPath, "logs"),
            "Support bundle log root cannot traverse a reparse point.");
        if (!Directory.Exists(logsRoot))
            return 0;

        var candidates = new PriorityQueue<(string Path, DateTime LastWriteTimeUtc), long>();
        foreach (var path in EnumerateFilesWithoutReparsePoints(logsRoot))
        {
            try
            {
                var info = new FileInfo(path);
                info.Refresh();
                if (!info.Exists)
                    continue;

                var candidate = (Path: path, LastWriteTimeUtc: info.LastWriteTimeUtc);
                candidates.Enqueue(candidate, candidate.LastWriteTimeUtc.Ticks);
                if (candidates.Count > MaxLogCandidates)
                    _ = candidates.Dequeue();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
            }
        }

        long totalBytes = 0;
        var included = 0;
        foreach (var candidate in candidates.UnorderedItems
                     .Select(item => item.Element)
                     .OrderByDescending(item => item.LastWriteTimeUtc)
                     .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (included >= MaxLogFiles)
                break;

            try
            {
                var info = new FileInfo(candidate.Path);
                info.Refresh();
                if (!info.Exists)
                    continue;

                var budget = Math.Min(info.Length, MaxLogBytes);
                if (totalBytes + budget > MaxTotalLogBytes)
                    continue;

                var text = ReadTailText(candidate.Path, MaxLogBytes, out var truncated);
                var relative = Path.GetRelativePath(logsRoot, candidate.Path).Replace('\\', '/');
                var archiveName = "logs/" + relative;
                var redacted = RedactText(text);
                if (truncated)
                    redacted = $"[TRUNCATED TO LAST {MaxLogBytes} BYTES]{Environment.NewLine}" + redacted;
                AddTextEntry(archive, entries, archiveName, redacted);
                totalBytes += budget;
                included++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException)
            {
                var safeName = "logs/" + Path.GetFileName(candidate.Path) + ".omitted.txt";
                AddTextEntry(archive, entries, safeName, "Omitted: " + RedactText(ex.Message));
            }
        }

        return included;
    }

    private string RedactJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64
            });
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                WriteRedactedJson(writer, document.RootElement);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return RedactText(value);
        }
    }

    private void WriteRedactedJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveProperty(property.Name))
                        writer.WriteStringValue("<REDACTED>");
                    else
                        WriteRedactedJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteRedactedJson(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(RedactText(element.GetString() ?? string.Empty));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private string RedactText(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var result = value;
        if (!string.IsNullOrWhiteSpace(_rootPath))
            result = result.Replace(_rootPath, "<DEVBOX_ROOT>", StringComparison.OrdinalIgnoreCase);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            result = result.Replace(userProfile, "<USERPROFILE>", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(Environment.UserName))
            result = result.Replace(Environment.UserName, "<USER>", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Environment.MachineName))
            result = result.Replace(Environment.MachineName, "<MACHINE>", StringComparison.OrdinalIgnoreCase);

        try
        {
            result = PrivateKeyRegex.Replace(result, "<REDACTED_PRIVATE_KEY>");
            result = AuthorizationRegex.Replace(result, "$1<REDACTED>");
            result = BearerRegex.Replace(result, "Bearer <REDACTED>");
            result = UrlCredentialRegex.Replace(result, "$1<REDACTED>@");
            result = JwtRegex.Replace(result, "<REDACTED_JWT>");
            result = SecretAssignmentRegex.Replace(result, match =>
                match.Groups[1].Value + match.Groups[2].Value + match.Groups[1].Value + match.Groups[3].Value + "<REDACTED>");
            return result;
        }
        catch (RegexMatchTimeoutException)
        {
            return "<REDACTION_FAILED>";
        }
    }

    private static bool IsSensitiveProperty(string name)
    {
        var normalized = name
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized.EndsWith("arguments", StringComparison.Ordinal) ||
               normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("passwd", StringComparison.Ordinal) ||
               normalized == "pwd" ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Contains("authorization", StringComparison.Ordinal) ||
               normalized.Contains("cookie", StringComparison.Ordinal) ||
               normalized.Contains("connectionstring", StringComparison.Ordinal) ||
               normalized.Contains("privatekey", StringComparison.Ordinal) ||
               normalized.Contains("credential", StringComparison.Ordinal);
    }

    private static string ReadTailText(string path, int maxBytes, out bool truncated)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var start = Math.Max(0, length - maxBytes);
        truncated = start > 0;
        stream.Seek(start, SeekOrigin.Begin);

        var targetLength = (int)Math.Min(maxBytes, Math.Max(0, length - start));
        var buffer = new byte[targetLength];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
                break;
            offset += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, offset);
    }

    private static IEnumerable<string> EnumerateFilesWithoutReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly).ToArray();
                directories = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _ = ex;
                continue;
            }

            foreach (var file in files)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _ = ex;
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    yield return file;
            }

            foreach (var directory in directories)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _ = ex;
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    pending.Push(directory);
            }
        }
    }

    private static void AddJsonEntry(ZipArchive archive, List<string> entries, string name, object value) =>
        AddTextEntry(archive, entries, name, JsonSerializer.Serialize(value, JsonOptions));

    private static void AddTextEntry(ZipArchive archive, List<string> entries, string name, string value)
    {
        var entryName = name.Replace('\\', '/');
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = DateTimeOffset.UtcNow;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(value);
        entries.Add(entryName);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; preserve the primary failure.
        }
    }
}
