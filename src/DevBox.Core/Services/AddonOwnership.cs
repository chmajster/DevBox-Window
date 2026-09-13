using DevBox.Core.Models;

namespace DevBox.Core.Services;

internal static class AddonOwnership
{
    internal const string MarkerFileName = ".devbox-addon";
    private const long MaximumMarkerBytes = 1024;

    public static string MarkerPath(AddonDefinition addon) =>
        Path.Combine(addon.InstallPath, MarkerFileName);

    public static void WriteMarker(AddonDefinition addon)
    {
        ArgumentNullException.ThrowIfNull(addon);
        Directory.CreateDirectory(addon.InstallPath);
        var markerPath = MarkerPath(addon);
        var tempPath = markerPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, $"{addon.Key}{Environment.NewLine}{addon.Version}{Environment.NewLine}");
            if (File.Exists(markerPath))
                File.Replace(tempPath, markerPath, null);
            else
                File.Move(tempPath, markerPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public static bool IsOwned(string rootPath, AddonDefinition addon, bool allowLegacyVhost = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(addon);

        try
        {
            var root = Path.GetFullPath(rootPath);
            var markerPath = PathSafety.EnsureUnderRootWithoutReparsePoints(
                root,
                MarkerPath(addon),
                "Addon ownership marker cannot escape the DevBox root or traverse a reparse point.");
            if (File.Exists(markerPath))
                return HasValidMarker(markerPath, addon.Key);

            if (!allowLegacyVhost)
                return false;

            var host = LocalCertificateManager.NormalizeDomain(new Uri(addon.LocalUrl).Host);
            var vhostPath = PathSafety.EnsureUnderRootWithoutReparsePoints(
                root,
                Path.Combine(root, "config", "nginx", "sites-enabled", $"{host}.conf"),
                "Addon ownership vhost cannot escape the DevBox root or traverse a reparse point.");
            if (!File.Exists(vhostPath))
                return false;

            if (new FileInfo(vhostPath).Length > 1024 * 1024)
                return false;

            var content = File.ReadAllText(vhostPath);
            var installPath = PathSafety.EnsureUnderRootWithoutReparsePoints(
                root,
                addon.InstallPath,
                "Addon install path cannot escape the DevBox root or traverse a reparse point.");
            var relativeRoot = Path.GetRelativePath(root, installPath).Replace('\\', '/');
            return HasExactDirective(content, $"server_name {host};") &&
                   HasExactDirective(content, $"root {relativeRoot};");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasValidMarker(string markerPath, string expectedKey)
    {
        var info = new FileInfo(markerPath);
        if (info.Length <= 0 || info.Length > MaximumMarkerBytes)
            return false;

        var lines = File.ReadAllLines(markerPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        if (lines.Length != 2)
            return false;
        if (!string.Equals(lines[0], expectedKey, StringComparison.OrdinalIgnoreCase))
            return false;

        var version = lines[1];
        return version.Length <= 128 &&
               version.Any(char.IsLetterOrDigit) &&
               version.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '+');
    }

    private static bool HasExactDirective(string content, string directive)
    {
        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#'))
                continue;

            var commentIndex = line.IndexOf('#');
            if (commentIndex >= 0)
                line = line[..commentIndex].TrimEnd();

            foreach (var segment in line.Split(['{', '}', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(segment + ";", directive, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
