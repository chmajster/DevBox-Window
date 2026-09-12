from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, got {count}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")

# ADDONS: local URLs must match the vhost DevBox actually generates (plain HTTP/80),
# keys need a Windows-safe bounded length, and paths must not traverse junctions/symlinks.
replace_once(
    "src/DevBox.Core/Services/AddonCatalog.cs",
    '''        if (!Uri.TryCreate(entry.LocalUrl, UriKind.Absolute, out var localUri) ||\n            localUri.Scheme is not ("http" or "https") ||\n            !localUri.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase))\n        {\n            throw new InvalidDataException($"Addon '{entry.Key}' localUrl must be an absolute .test URL.");\n        }\n''',
    '''        if (!Uri.TryCreate(entry.LocalUrl, UriKind.Absolute, out var localUri) ||\n            localUri.Scheme != Uri.UriSchemeHttp ||\n            !localUri.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||\n            !localUri.IsDefaultPort)\n        {\n            throw new InvalidDataException($"Addon '{entry.Key}' localUrl must be an absolute http://*.test URL on the default HTTP port.");\n        }\n''')
replace_once(
    "src/DevBox.Core/Services/AddonCatalog.cs",
    '''        if (string.IsNullOrWhiteSpace(entry.Key) ||\n            entry.Key.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))\n''',
    '''        if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 64 ||\n            entry.Key.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))\n''')
replace_once(
    "src/DevBox.Core/Services/AddonCatalog.cs",
    '''        var installPath = ResolveRelativePath(entry.InstallRelativePath);\n        var wwwRoot = Path.GetFullPath(Path.Combine(_rootPath, "www"))\n            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;\n        var normalizedInstall = installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;\n        if (!normalizedInstall.StartsWith(wwwRoot, StringComparison.OrdinalIgnoreCase) ||\n            normalizedInstall.Equals(wwwRoot, StringComparison.OrdinalIgnoreCase))\n        {\n            throw new InvalidDataException($"Addon '{entry.Key}' install path must be a child of the DevBox www directory.");\n        }\n\n        var entryPointPath = ResolveRelativePath(entry.EntryPointRelativePath);\n        if (!entryPointPath.StartsWith(normalizedInstall, StringComparison.OrdinalIgnoreCase))\n        {\n            throw new InvalidDataException($"Addon '{entry.Key}' entry point must be inside its install directory.");\n        }\n''',
    '''        var installPath = ResolveRelativePath(entry.InstallRelativePath);\n        var wwwRoot = Path.GetFullPath(Path.Combine(_rootPath, "www"));\n        try\n        {\n            installPath = PathSafety.EnsureUnderRootWithoutReparsePoints(\n                wwwRoot, installPath, $"Addon '{entry.Key}' install path must be a child of the DevBox www directory and cannot traverse a reparse point.");\n        }\n        catch (InvalidOperationException ex)\n        {\n            throw new InvalidDataException(ex.Message, ex);\n        }\n\n        var entryPointPath = ResolveRelativePath(entry.EntryPointRelativePath);\n        try\n        {\n            entryPointPath = PathSafety.EnsureUnderRootWithoutReparsePoints(\n                installPath, entryPointPath, $"Addon '{entry.Key}' entry point must be inside its install directory and cannot traverse a reparse point.");\n        }\n        catch (InvalidOperationException ex)\n        {\n            throw new InvalidDataException(ex.Message, ex);\n        }\n''')

# ADDON installer must enforce the same safety when called directly and observe
# cancellation through the local copy stage instead of committing after cancellation.
replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''            CopyDirectory(sourcePath, stagingPath);\n\n            if (!File.Exists(Path.Combine(stagingPath, "index.php")))\n''',
    '''            CopyDirectory(sourcePath, stagingPath, cancellationToken);\n            cancellationToken.ThrowIfCancellationRequested();\n\n            if (!File.Exists(Path.Combine(stagingPath, "index.php")))\n''')
replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''            var backupPath = SwapInStagingDirectory(stagingPath, addon.InstallPath);\n''',
    '''            cancellationToken.ThrowIfCancellationRequested();\n            var backupPath = SwapInStagingDirectory(stagingPath, addon.InstallPath);\n''')
replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''        if (!Uri.TryCreate(addon.LocalUrl, UriKind.Absolute, out var uri) ||\n            !uri.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase))\n        {\n            throw new InvalidDataException("Addon local URL must use a valid .test host.");\n        }\n''',
    '''        if (!Uri.TryCreate(addon.LocalUrl, UriKind.Absolute, out var uri) ||\n            uri.Scheme != Uri.UriSchemeHttp ||\n            !uri.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||\n            !uri.IsDefaultPort)\n        {\n            throw new InvalidDataException("Addon local URL must use http:// on a valid .test host and the default HTTP port.");\n        }\n''')
replace_once(
    "src/DevBox.Core/Services/AddonInstaller.cs",
    '''    private void EnsureInstallPathIsSafe(AddonDefinition addon)\n    {\n        var wwwRoot = Path.GetFullPath(Path.Combine(_rootPath, "www")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;\n        var installPath = Path.GetFullPath(addon.InstallPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;\n        if (!installPath.StartsWith(wwwRoot, StringComparison.OrdinalIgnoreCase) || installPath.Equals(wwwRoot, StringComparison.OrdinalIgnoreCase))\n        {\n            throw new InvalidOperationException("Addon install path must be a child of the DevBox www directory.");\n        }\n    }\n\n    private static void CopyDirectory(string sourcePath, string destinationPath)\n    {\n        Directory.CreateDirectory(destinationPath);\n        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))\n        {\n            var relative = Path.GetRelativePath(sourcePath, directory);\n            Directory.CreateDirectory(Path.Combine(destinationPath, relative));\n        }\n\n        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))\n        {\n            var relative = Path.GetRelativePath(sourcePath, file);\n            var target = Path.Combine(destinationPath, relative);\n            Directory.CreateDirectory(Path.GetDirectoryName(target)!);\n            File.Copy(file, target, overwrite: true);\n        }\n    }\n''',
    '''    private void EnsureInstallPathIsSafe(AddonDefinition addon)\n    {\n        if (string.IsNullOrWhiteSpace(addon.Key) || addon.Key.Length > 64 ||\n            addon.Key.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))\n            throw new InvalidDataException("Addon key contains invalid characters or exceeds 64 characters.");\n        _ = PathSafety.EnsureUnderRootWithoutReparsePoints(\n            Path.Combine(_rootPath, "www"),\n            addon.InstallPath,\n            "Addon install path must be a child of the DevBox www directory and cannot traverse a reparse point.");\n        _ = PathSafety.EnsureUnderRootWithoutReparsePoints(\n            addon.InstallPath,\n            addon.EntryPointPath,\n            "Addon entry point must remain inside the addon install directory and cannot traverse a reparse point.");\n        if (!Uri.TryCreate(addon.LocalUrl, UriKind.Absolute, out var uri) ||\n            uri.Scheme != Uri.UriSchemeHttp || !uri.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort)\n            throw new InvalidDataException("Addon local URL must use http:// on a valid .test host and the default HTTP port.");\n    }\n\n    private static void CopyDirectory(string sourcePath, string destinationPath, CancellationToken cancellationToken)\n    {\n        Directory.CreateDirectory(destinationPath);\n        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))\n        {\n            cancellationToken.ThrowIfCancellationRequested();\n            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)\n                throw new InvalidDataException("Addon package contains a reparse point.");\n            var relative = Path.GetRelativePath(sourcePath, directory);\n            Directory.CreateDirectory(Path.Combine(destinationPath, relative));\n        }\n\n        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))\n        {\n            cancellationToken.ThrowIfCancellationRequested();\n            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)\n                throw new InvalidDataException("Addon package contains a reparse point.");\n            var relative = Path.GetRelativePath(sourcePath, file);\n            var target = Path.Combine(destinationPath, relative);\n            Directory.CreateDirectory(Path.GetDirectoryName(target)!);\n            File.Copy(file, target, overwrite: true);\n        }\n    }\n''')

# Configuration restore must not follow a reparse point out of the backup tree.
replace_once(
    "src/DevBox.Core/Services/ConfigurationFileService.cs",
    '''        var source = Path.GetFullPath(backupPath);\n        var allowedRoot = Path.GetFullPath(_backupRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;\n        if (!source.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException("Configuration backups can only be restored from the DevBox backup directory.");\n''',
    '''        var source = PathSafety.EnsureUnderRootWithoutReparsePoints(\n            _backupRoot,\n            backupPath,\n            "Configuration backups can only be restored from the DevBox backup directory and cannot traverse a reparse point.");\n''')

# Project operation services previously had lexical-only root checks even though
# Site/Workspace/WordPress already use reparse-aware validation.
replacements = [
("src/DevBox.Core/Services/ProjectCommandService.cs",
'''        var www = _wwwRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;\n        if (!root.StartsWith(www, StringComparison.OrdinalIgnoreCase))\n        {\n            throw new InvalidOperationException("Project commands are restricted to projects inside the DevBox www directory.");\n        }\n        return root;\n''',
'''        return PathSafety.EnsureUnderRootWithoutReparsePoints(\n            _wwwRoot, root, "Project commands are restricted to projects inside the DevBox www directory and cannot traverse a reparse point.");\n'''),
("src/DevBox.Core/Services/ProjectTransferService.cs",
'''        var www = Path.GetFullPath(_wwwRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;\n        if (!root.StartsWith(www, StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException("Project transfer is restricted to the DevBox www directory.");\n        return root;\n''',
'''        return PathSafety.EnsureUnderRootWithoutReparsePoints(\n            _wwwRoot, root, "Project transfer is restricted to the DevBox www directory and cannot traverse a reparse point.");\n'''),
("src/DevBox.Core/Services/ProjectSnapshotService.cs",
'''        var www = Path.GetFullPath(_wwwRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;\n        if (!root.StartsWith(www, StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException("Project snapshots are restricted to the DevBox www directory.");\n        return root;\n''',
'''        return PathSafety.EnsureUnderRootWithoutReparsePoints(\n            _wwwRoot, root, "Project snapshots are restricted to the DevBox www directory and cannot traverse a reparse point.");\n'''),
("src/DevBox.Core/Services/EnvironmentLockService.cs",
'''        var www = Path.GetFullPath(_wwwRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        if (!root.Equals(www, StringComparison.OrdinalIgnoreCase) && !root.StartsWith(www + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException("Environment operations are restricted to the DevBox www directory.");\n        return root;\n''',
'''        return PathSafety.EnsureUnderRootWithoutReparsePoints(\n            _wwwRoot, root, "Environment operations are restricted to the DevBox www directory and cannot traverse a reparse point.");\n''')]
for path, old, new in replacements:
    replace_once(path, old, new)

replace_once(
    "src/DevBox.Core/Services/ProjectActionService.cs",
    '''        EnsureUnder(root, _wwwRoot, "Project actions are restricted to the DevBox www directory.");\n        return root;\n''',
    '''        return PathSafety.EnsureUnderRootWithoutReparsePoints(\n            _wwwRoot, root, "Project actions are restricted to the DevBox www directory and cannot traverse a reparse point.");\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectActionService.cs",
    '''        var path = Path.GetFullPath(Path.Combine(projectRoot, relative));\n        EnsureUnder(path, projectRoot, "Project action working directory escapes the project root.");\n        if (!Directory.Exists(path))\n            throw new DirectoryNotFoundException($"Project action working directory does not exist: {path}");\n        return path;\n''',
    '''        var path = Path.GetFullPath(Path.Combine(projectRoot, relative));\n        if (!Directory.Exists(path))\n            throw new DirectoryNotFoundException($"Project action working directory does not exist: {path}");\n        if (path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(\n                projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))\n            return projectRoot;\n        return PathSafety.EnsureUnderRootWithoutReparsePoints(\n            projectRoot, path, "Project action working directory escapes the project root or traverses a reparse point.");\n''')

# Bound captured child-process output while continuing to drain both streams.
helper = '''\n    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)\n    {\n        var buffer = new char[8192];\n        var builder = new System.Text.StringBuilder(Math.Min(maximumCharacters, 64 * 1024));\n        var truncated = false;\n        while (true)\n        {\n            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);\n            if (read == 0)\n                break;\n            var remaining = maximumCharacters - builder.Length;\n            if (remaining > 0)\n                builder.Append(buffer, 0, Math.Min(remaining, read));\n            if (read > remaining)\n                truncated = true;\n        }\n        if (truncated)\n            builder.Append(Environment.NewLine).Append("[output truncated by DevBox]");\n        return builder.ToString();\n    }\n'''
replace_once(
    "src/DevBox.Core/Services/ProjectCommandService.cs",
    '''        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);\n        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);\n''',
    '''        var outputTask = ReadBoundedAsync(process.StandardOutput, 1_048_576, cancellationToken);\n        var errorTask = ReadBoundedAsync(process.StandardError, 1_048_576, cancellationToken);\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectCommandService.cs",
    '''    private static string RequireFile(string path, string message) =>\n''', helper + '''\n    private static string RequireFile(string path, string message) =>\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectActionService.cs",
    '''        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);\n        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);\n''',
    '''        var stdoutTask = ReadBoundedAsync(process.StandardOutput, 1_048_576, cancellationToken);\n        var stderrTask = ReadBoundedAsync(process.StandardError, 1_048_576, cancellationToken);\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectActionService.cs",
    '''    private static string Truncate(string value) => value.Length <= 1_048_576 ? value : value[..1_048_576] + Environment.NewLine + "[output truncated by DevBox]";\n\n''', helper + '''\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectActionService.cs",
    '''        var stdout = Truncate(await stdoutTask.ConfigureAwait(false));\n        var stderr = Truncate(await stderrTask.ConfigureAwait(false));\n''',
    '''        var stdout = await stdoutTask.ConfigureAwait(false);\n        var stderr = await stderrTask.ConfigureAwait(false);\n''')

# Regression tests for deterministic validation paths and output bounding helper behavior.
test = r'''using System.Reflection;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound9Tests
{
    [Theory]
    [InlineData("https://addon.test")]
    [InlineData("http://addon.test:8080")]
    public void AddonCatalog_RejectsLocalUrlsThatCannotMatchGeneratedVhost(string localUrl)
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "addons.json"), Catalog(localUrl, "addon"));
            Assert.Throws<InvalidDataException>(() => new AddonCatalog(root).GetAddons());
        }
        finally { Delete(root); }
    }

    [Fact]
    public void AddonCatalog_RejectsOverlongKey()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "addons.json"), Catalog("http://addon.test", new string('a', 65)));
            Assert.Throws<InvalidDataException>(() => new AddonCatalog(root).GetAddons());
        }
        finally { Delete(root); }
    }

    [Fact]
    public void PathSafety_RejectsCandidateOutsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round9", Guid.NewGuid().ToString("N"));
        var outside = root + "-outside";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            Assert.Throws<InvalidOperationException>(() => PathSafety.EnsureUnderRootWithoutReparsePoints(root, outside, "unsafe"));
        }
        finally { Delete(root); Delete(outside); }
    }

    [Fact]
    public void PathSafety_RejectsReparsePointWhenSupportedByRunner()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round9", Guid.NewGuid().ToString("N"));
        var outside = root + "-outside";
        var link = Path.Combine(root, "linked");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
            Assert.Throws<InvalidOperationException>(() => PathSafety.EnsureUnderRootWithoutReparsePoints(root, Path.Combine(link, "child"), "unsafe"));
        }
        finally { Delete(root); Delete(outside); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round9", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "www"));
        return root;
    }

    private static string Catalog(string localUrl, string key) => $$"""
    [{"Key":"{{key}}","DisplayName":"Addon","Description":"fixture","InstallRelativePath":"www/addon","EntryPointRelativePath":"www/addon/index.php","LocalUrl":"{{localUrl}}","RequiredPhpExtensions":[],"Version":"1.0","DownloadUrl":"https://example.test/addon.zip","Sha256":"{{new string('a',64)}}","ArchiveRootDirectory":"package"}]
    """;

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
'''
Path("tests/DevBox.Tests/FinalBugSweepRound9Tests.cs").write_text(test, encoding="utf-8")

# CHANGELOG: record this round and the immediately preceding PR #37 fixes that were
# merged after the previous changelog update.
path = Path("CHANGELOG.md")
text = path.read_text(encoding="utf-8")
marker = "### Fixed\n\n"
entries = '''### Fixed\n\n- ADDONS install/entry-point paths now reject junctions, symbolic links and other reparse-point traversal, including direct installer calls that bypass the catalog.\n- ADDONS local URLs are restricted to plain `http://*.test` on port 80, matching the Nginx vhost DevBox actually generates; unsupported HTTPS/custom-port URLs are rejected instead of producing unreachable addons.\n- ADDON keys are capped at 64 characters so hand-edited or marketplace catalogs cannot generate invalid Windows lock/temp paths.\n- ADDON installation now observes cancellation while copying staged files and immediately before the irreversible directory swap, and rejects reparse points in staged payloads.\n- Configuration backup restore now rejects reparse-point traversal out of `backups/configuration`.\n- Project actions, command presets, snapshots, transfers and environment-lock operations now consistently reject project roots that traverse a junction/symbolic link outside the DevBox `www` tree; project-action working directories receive the same protection.\n- Project action and preset command output is drained with a bounded in-memory capture, preventing noisy child processes from growing DevBox memory without limit.\n- Command event subscribers are isolated so failing `CanExecuteChanged`/`ExecutionFailed` handlers cannot corrupt asynchronous command state.\n- Persisted project-action, environment-profile, runtime-catalog, secret-store and Task Center duplicate identities are rejected deterministically instead of being silently overwritten or surfacing collection exceptions.\n- ADDONS catalogs reject conflicting install directories/domains, initialize safely across competing processes and report malformed marketplace keys as controlled data errors.\n- Managed-service database-port reservations now parse registration property names case-insensitively and reject malformed registration records.\n- Service shutdown re-checks cancellation immediately before forced process-tree termination, closing the graceful-timeout/kill race.\n\n'''
if text.count(marker) != 1:
    raise RuntimeError("CHANGELOG Fixed marker was not unique")
path.write_text(text.replace(marker, entries, 1), encoding="utf-8")
