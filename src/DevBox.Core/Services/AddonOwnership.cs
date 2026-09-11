using DevBox.Core.Models;

namespace DevBox.Core.Services;

internal static class AddonOwnership
{
    internal const string MarkerFileName = ".devbox-addon";

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
            var markerPath = MarkerPath(addon);
            if (File.Exists(markerPath))
            {
                var firstLine = File.ReadLines(markerPath).FirstOrDefault()?.Trim();
                return string.Equals(firstLine, addon.Key, StringComparison.OrdinalIgnoreCase);
            }

            if (!allowLegacyVhost)
                return false;

            var host = new Uri(addon.LocalUrl).Host;
            var vhostPath = Path.Combine(rootPath, "config", "nginx", "sites-enabled", $"{host}.conf");
            if (!File.Exists(vhostPath))
                return false;

            var content = File.ReadAllText(vhostPath);
            var relativeRoot = Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(addon.InstallPath))
                .Replace('\\', '/');
            return content.Contains($"server_name {host}", StringComparison.OrdinalIgnoreCase) &&
                   content.Contains($"root {relativeRoot}", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException or ArgumentException)
        {
            return false;
        }
    }
}
