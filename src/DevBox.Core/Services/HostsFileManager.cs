using System.Net;

namespace DevBox.Core.Services;

public sealed class HostsFileManager
{
    private readonly string _hostsPath;

    public HostsFileManager(string? hostsPath = null)
    {
        _hostsPath = Path.GetFullPath(hostsPath ?? DefaultHostsPath);
    }

    public static string DefaultHostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public string HostsPath => _hostsPath;

    public bool HasMapping(string ipAddress, string domain)
    {
        Validate(ipAddress, domain);
        if (!File.Exists(_hostsPath))
        {
            return false;
        }

        return File.ReadLines(_hostsPath).Any(line => LineContainsMapping(line, ipAddress, domain));
    }

    public void EnsureMapping(string ipAddress, string domain)
    {
        Validate(ipAddress, domain);
        Directory.CreateDirectory(Path.GetDirectoryName(_hostsPath)!);

        var lines = File.Exists(_hostsPath)
            ? File.ReadAllLines(_hostsPath).ToList()
            : new List<string>();

        var rewritten = lines
            .Select(line => RemoveDomainFromLine(line, domain))
            .Where(line => line is not null)
            .Select(line => line!)
            .ToList();

        rewritten.Add($"{ipAddress} {domain} # DevBox");
        if (lines.SequenceEqual(rewritten, StringComparer.Ordinal))
        {
            return;
        }

        AtomicWrite(rewritten);
    }

    public void RemoveMapping(string domain)
    {
        ValidateDomain(domain);
        if (!File.Exists(_hostsPath))
        {
            return;
        }

        var original = File.ReadAllLines(_hostsPath);
        var rewritten = original
            .Select(line => RemoveDomainFromLine(line, domain))
            .Where(line => line is not null)
            .Select(line => line!)
            .ToList();

        if (original.SequenceEqual(rewritten, StringComparer.Ordinal))
        {
            return;
        }

        AtomicWrite(rewritten);
    }

    private void AtomicWrite(IReadOnlyCollection<string> lines)
    {
        var directory = Path.GetDirectoryName(_hostsPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_hostsPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllLines(tempPath, lines);
            if (File.Exists(_hostsPath))
            {
                File.Replace(tempPath, _hostsPath, null);
            }
            else
            {
                File.Move(tempPath, _hostsPath);
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

    private static bool LineContainsMapping(string line, string ipAddress, string domain)
    {
        var parsed = ParseLine(line);
        return parsed is not null &&
               parsed.Value.Address.Equals(ipAddress, StringComparison.OrdinalIgnoreCase) &&
               parsed.Value.Hosts.Contains(domain, StringComparer.OrdinalIgnoreCase);
    }

    private static string? RemoveDomainFromLine(string line, string domain)
    {
        var parsed = ParseLine(line);
        if (parsed is null || !parsed.Value.Hosts.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            return line;
        }

        var remaining = parsed.Value.Hosts
            .Where(host => !host.Equals(domain, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (remaining.Length == 0)
        {
            return null;
        }

        var rebuilt = $"{parsed.Value.Address} {string.Join(' ', remaining)}";
        return string.IsNullOrWhiteSpace(parsed.Value.Comment)
            ? rebuilt
            : $"{rebuilt} #{parsed.Value.Comment}";
    }

    private static (string Address, string[] Hosts, string Comment)? ParseLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return null;
        }

        var hashIndex = line.IndexOf('#');
        var data = hashIndex >= 0 ? line[..hashIndex] : line;
        var comment = hashIndex >= 0 ? line[(hashIndex + 1)..].Trim() : string.Empty;
        var parts = data.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return (parts[0], parts[1..], comment);
    }

    private static void Validate(string ipAddress, string domain)
    {
        if (!IPAddress.TryParse(ipAddress, out var address) || !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException("Only loopback IP addresses are allowed for DevBox hosts entries.", nameof(ipAddress));
        }

        ValidateDomain(domain);
    }

    private static void ValidateDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        if (domain.Length > 253 || domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid domain name.", nameof(domain));
        }

        foreach (var label in domain.Split('.'))
        {
            if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-') ||
                label.Any(character => !char.IsLetterOrDigit(character) && character != '-'))
            {
                throw new ArgumentException("Invalid domain name.", nameof(domain));
            }
        }
    }
}
