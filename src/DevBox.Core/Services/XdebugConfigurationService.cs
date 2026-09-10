using System.Text.RegularExpressions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed partial class XdebugConfigurationService
{
    private readonly string _phpIniPath;
    private readonly string _xdebugBinaryPath;

    public XdebugConfigurationService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = Path.GetFullPath(rootPath);
        _phpIniPath = Path.Combine(root, "config", "php", "php.ini");
        _xdebugBinaryPath = Path.Combine(root, "runtime", "php", "current", "ext", "php_xdebug.dll");
    }

    public XdebugStatus GetStatus()
    {
        var binaryAvailable = File.Exists(_xdebugBinaryPath);
        if (!File.Exists(_phpIniPath))
        {
            return new XdebugStatus(binaryAvailable, false, "debug,develop", 9003, "trigger", _phpIniPath, _xdebugBinaryPath);
        }

        var lines = File.ReadAllLines(_phpIniPath);
        var enabled = lines.Any(IsEnabledZendExtension);
        var mode = ReadDirective(lines, "xdebug.mode") ?? "debug,develop";
        var startWithRequest = ReadDirective(lines, "xdebug.start_with_request") ?? "trigger";
        var portValue = ReadDirective(lines, "xdebug.client_port");
        var port = int.TryParse(portValue, out var parsed) && parsed is >= 1 and <= 65535 ? parsed : 9003;

        return new XdebugStatus(binaryAvailable, enabled, mode, port, startWithRequest, _phpIniPath, _xdebugBinaryPath);
    }

    public XdebugStatus Configure(XdebugConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateConfiguration(configuration);

        if (!File.Exists(_phpIniPath))
        {
            throw new FileNotFoundException("php.ini was not found.", _phpIniPath);
        }
        if (configuration.Enabled && !File.Exists(_xdebugBinaryPath))
        {
            throw new FileNotFoundException("php_xdebug.dll is not available in the active PHP runtime.", _xdebugBinaryPath);
        }

        var lines = File.ReadAllLines(_phpIniPath).ToList();
        ReplaceDirective(lines, "zend_extension", configuration.Enabled ? "php_xdebug.dll" : null, IsXdebugZendExtension);
        ReplaceDirective(lines, "xdebug.mode", configuration.Mode, line => IsNamedDirective(line, "xdebug.mode"));
        ReplaceDirective(lines, "xdebug.client_port", configuration.ClientPort.ToString(System.Globalization.CultureInfo.InvariantCulture), line => IsNamedDirective(line, "xdebug.client_port"));
        ReplaceDirective(lines, "xdebug.start_with_request", configuration.StartWithRequest, line => IsNamedDirective(line, "xdebug.start_with_request"));

        AtomicWrite(_phpIniPath, lines);
        return GetStatus();
    }

    internal static bool IsXdebugZendExtension(string line)
    {
        var normalized = StripCommentPrefix(line).Trim();
        var separator = normalized.IndexOf('=');
        if (separator < 0 || !normalized[..separator].Trim().Equals("zend_extension", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = normalized[(separator + 1)..].Trim().Trim('"', '\'');
        return value.EndsWith("php_xdebug.dll", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith("xdebug.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnabledZendExtension(string line) =>
        !line.TrimStart().StartsWith(';') && IsXdebugZendExtension(line);

    private static string? ReadDirective(IEnumerable<string> lines, string name)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator < 0 || !trimmed[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed[(separator + 1)..];
            var comment = value.IndexOf(';');
            if (comment >= 0)
            {
                value = value[..comment];
            }
            return value.Trim().Trim('"', '\'');
        }
        return null;
    }

    private static bool IsNamedDirective(string line, string name)
    {
        var normalized = StripCommentPrefix(line).Trim();
        var separator = normalized.IndexOf('=');
        return separator >= 0 && normalized[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripCommentPrefix(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith(';') ? trimmed[1..].TrimStart() : trimmed;
    }

    private static void ReplaceDirective(
        List<string> lines,
        string name,
        string? value,
        Func<string, bool> predicate)
    {
        var matching = Enumerable.Range(0, lines.Count).Where(index => predicate(lines[index])).ToArray();
        var replacement = value is null ? $";{name}=php_xdebug.dll" : $"{name}={value}";

        if (matching.Length == 0)
        {
            lines.Add(replacement);
            return;
        }

        lines[matching[0]] = replacement;
        for (var index = matching.Length - 1; index >= 1; index--)
        {
            lines.RemoveAt(matching[index]);
        }
    }

    private static void ValidateConfiguration(XdebugConfiguration configuration)
    {
        if (configuration.ClientPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration.ClientPort), "Xdebug client port must be between 1 and 65535.");
        }

        var modes = configuration.Mode.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (modes.Length == 0 || modes.Any(mode => !AllowedModes.Contains(mode)))
        {
            throw new ArgumentException("Xdebug mode contains an unsupported value.", nameof(configuration.Mode));
        }

        if (!AllowedStartModes.Contains(configuration.StartWithRequest))
        {
            throw new ArgumentException("xdebug.start_with_request must be yes, no, trigger or default.", nameof(configuration.StartWithRequest));
        }
    }

    private static void AtomicWrite(string path, IReadOnlyCollection<string> lines)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(tempPath, lines);
            File.Replace(tempPath, path, null);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static readonly HashSet<string> AllowedModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "off", "develop", "coverage", "debug", "gcstats", "profile", "trace"
    };

    private static readonly HashSet<string> AllowedStartModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "no", "trigger", "default"
    };
}
