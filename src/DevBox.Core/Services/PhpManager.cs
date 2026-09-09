using System.Text.RegularExpressions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed partial class PhpManager
{
    private readonly string _rootPath;
    private readonly string _phpIniPath;
    private readonly string _extensionDirectory;
    private readonly string _activeRuntimeDirectory;

    public PhpManager(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _phpIniPath = Path.Combine(_rootPath, "config", "php", "php.ini");
        _activeRuntimeDirectory = Path.Combine(_rootPath, "runtime", "php", "current");
        _extensionDirectory = Path.Combine(_activeRuntimeDirectory, "ext");
    }

    public string PhpIniPath => _phpIniPath;

    public string? GetActiveVersion()
    {
        var marker = Path.Combine(_activeRuntimeDirectory, ".devbox-version");
        if (File.Exists(marker))
        {
            var version = File.ReadAllText(marker).Trim();
            return version.Length == 0 ? null : version;
        }

        return null;
    }

    public IReadOnlyList<PhpExtensionState> GetExtensions()
    {
        var configured = ReadConfiguredExtensions();
        var available = Directory.Exists(_extensionDirectory)
            ? Directory.GetFiles(_extensionDirectory, "php_*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null)
                .Select(name => name![4..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var names = available
            .Concat(configured.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return names
            .Select(name => new PhpExtensionState(
                name,
                configured.TryGetValue(name, out var enabled) && enabled,
                available.Contains(name)))
            .ToArray();
    }

    public bool SetExtensionEnabled(string extension, bool enabled)
    {
        var normalized = NormalizeExtension(extension);
        if (!File.Exists(_phpIniPath))
        {
            throw new FileNotFoundException("php.ini was not found.", _phpIniPath);
        }

        var lines = File.ReadAllLines(_phpIniPath).ToList();
        var matched = false;
        var changed = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var parsed = ParseExtensionLine(lines[index]);
            if (parsed is null || !parsed.Value.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matched = true;
            var desired = enabled ? $"extension={normalized}" : $";extension={normalized}";
            if (!lines[index].Equals(desired, StringComparison.Ordinal))
            {
                lines[index] = desired;
                changed = true;
            }
        }

        if (!matched && enabled)
        {
            lines.Add($"extension={normalized}");
            changed = true;
        }

        if (changed)
        {
            AtomicWrite(lines);
        }

        return changed;
    }

    private Dictionary<string, bool> ReadConfiguredExtensions()
    {
        var configured = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_phpIniPath))
        {
            return configured;
        }

        foreach (var line in File.ReadAllLines(_phpIniPath))
        {
            var parsed = ParseExtensionLine(line);
            if (parsed.HasValue)
            {
                configured[parsed.Value.Name] = parsed.Value.Enabled;
            }
        }

        return configured;
    }

    private static (string Name, bool Enabled)? ParseExtensionLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var enabled = !trimmed.StartsWith(';');
        if (!enabled)
        {
            trimmed = trimmed[1..].TrimStart();
        }

        if (!trimmed.StartsWith("extension", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var equalsIndex = trimmed.IndexOf('=');
        if (equalsIndex < 0)
        {
            return null;
        }

        var value = trimmed[(equalsIndex + 1)..].Trim().Trim('"', '\'');
        if (value.StartsWith("php_", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }
        if (value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return ExtensionNameRegex().IsMatch(value) ? (value.ToLowerInvariant(), enabled) : null;
    }

    private static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var normalized = extension.Trim().ToLowerInvariant();
        if (normalized.StartsWith("php_", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[4..];
        }
        if (normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }
        if (!ExtensionNameRegex().IsMatch(normalized))
        {
            throw new ArgumentException("Invalid PHP extension name.", nameof(extension));
        }
        return normalized;
    }

    private void AtomicWrite(IReadOnlyCollection<string> lines)
    {
        var directory = Path.GetDirectoryName(_phpIniPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_phpIniPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(tempPath, lines);
            if (File.Exists(_phpIniPath))
            {
                File.Replace(tempPath, _phpIniPath, null);
            }
            else
            {
                File.Move(tempPath, _phpIniPath);
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

    [GeneratedRegex("^[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionNameRegex();
}
