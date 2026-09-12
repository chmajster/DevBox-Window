from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected exactly one match, got {count}: {old[:140]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")

# Shared bounded process-output capture: continue draining after the retained prefix so
# child processes cannot deadlock, but do not let noisy output grow DevBox memory without bound.
Path("src/DevBox.Core/Services/ProcessOutputCapture.cs").write_text('''using System.Text;\n\nnamespace DevBox.Core.Services;\n\ninternal static class ProcessOutputCapture\n{\n    public const int DefaultMaximumCharacters = 1_048_576;\n\n    public static async Task<string> ReadBoundedAsync(\n        TextReader reader,\n        int maximumCharacters = DefaultMaximumCharacters,\n        CancellationToken cancellationToken = default)\n    {\n        ArgumentNullException.ThrowIfNull(reader);\n        if (maximumCharacters <= 0)\n            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));\n\n        var buffer = new char[8192];\n        var builder = new StringBuilder(Math.Min(maximumCharacters, 64 * 1024));\n        var truncated = false;\n        while (true)\n        {\n            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);\n            if (read == 0)\n                break;\n\n            var remaining = maximumCharacters - builder.Length;\n            if (remaining > 0)\n                builder.Append(buffer, 0, Math.Min(remaining, read));\n            if (read > remaining)\n                truncated = true;\n        }\n\n        if (truncated)\n            builder.Append(Environment.NewLine).Append("[output truncated by DevBox]");\n        return builder.ToString();\n    }\n}\n''', encoding="utf-8")

# PathSafety must reject a protected root that is itself a junction/symlink, and some
# callers (managed-service working directory '.') legitimately need candidate == root.
replace_once(
    "src/DevBox.Core/Services/PathSafety.cs",
    '''    public static string EnsureUnderRootWithoutReparsePoints(string rootPath, string candidatePath, string message)\n    {\n        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);\n        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);\n\n        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        var fullCandidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        var prefix = fullRoot + Path.DirectorySeparatorChar;\n        if (!fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException(message);\n\n        var relative = Path.GetRelativePath(fullRoot, fullCandidate);\n''',
    '''    public static string EnsureUnderRootWithoutReparsePoints(\n        string rootPath,\n        string candidatePath,\n        string message,\n        bool allowRoot = false)\n    {\n        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);\n        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);\n\n        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        var fullCandidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        if ((Directory.Exists(fullRoot) || File.Exists(fullRoot)) &&\n            (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)\n            throw new InvalidOperationException($"{message} Protected root is a reparse point: {fullRoot}");\n\n        if (fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))\n        {\n            if (!allowRoot)\n                throw new InvalidOperationException(message);\n            return fullCandidate;\n        }\n\n        var prefix = fullRoot + Path.DirectorySeparatorChar;\n        if (!fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))\n            throw new InvalidOperationException(message);\n\n        var relative = Path.GetRelativePath(fullRoot, fullCandidate);\n''')

# Managed-service paths were only lexically constrained; a junction inside DevBox could
# point executables/logs/working directories outside the installation root.
replace_once(
    "src/DevBox.Core/Services/ManagedServiceCatalog.cs",
    '''        var root = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        var full = Path.GetFullPath(Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)))\n            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        var isRoot = full.Equals(root, StringComparison.OrdinalIgnoreCase);\n        if ((!allowRoot || !isRoot) && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))\n        {\n            throw new InvalidDataException($"{name} escapes the DevBox root.");\n        }\n        return full;\n''',
    '''        var full = Path.GetFullPath(Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)))\n            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n        try\n        {\n            return PathSafety.EnsureUnderRootWithoutReparsePoints(\n                _rootPath, full, $"{name} escapes the DevBox root or traverses a reparse point.", allowRoot);\n        }\n        catch (InvalidOperationException ex)\n        {\n            throw new InvalidDataException(ex.Message, ex);\n        }\n''')

# ZIP extraction must be deterministic on Windows: duplicate/case-alias targets cannot
# be allowed to overwrite a previously validated entry.
replace_once(
    "src/DevBox.Core/Services/ArchiveSafety.cs",
    '''        long totalUncompressedBytes = 0;\n        foreach (var entry in archive.Entries)\n        {\n            ValidateEntryPath(entry, destinationPath, destinationRoot, packageName);\n            RejectSymbolicLink(entry, packageName);\n''',
    '''        long totalUncompressedBytes = 0;\n        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);\n        foreach (var entry in archive.Entries)\n        {\n            ValidateEntryPath(entry, destinationPath, destinationRoot, packageName);\n            RejectSymbolicLink(entry, packageName);\n            var outputPath = ResolveOutputPath(entry, destinationPath)\n                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);\n            if (!outputPaths.Add(outputPath))\n                throw new InvalidDataException($"{packageName} archive contains multiple entries targeting the same output path: {entry.FullName}");\n''')

# Project-action validation is the canonical policy. Profiles must not accept actions
# that will later be rejected while applying the profile.
replace_once(
    "src/DevBox.Core/Services/ProjectActionService.cs",
    '''    private static void ValidateActions(IEnumerable<ProjectActionDefinition?> actions)\n''',
    '''    internal static void ValidateDefinitions(IEnumerable<ProjectActionDefinition?> actions) => ValidateActions(actions);\n\n    private static void ValidateActions(IEnumerable<ProjectActionDefinition?> actions)\n''')
replace_once(
    "src/DevBox.Core/Services/EnvironmentProfileService.cs",
    '''        var duplicateAction = profile.Actions\n            .Where(action => action is not null)\n            .GroupBy(action => action!.Key, StringComparer.OrdinalIgnoreCase)\n            .FirstOrDefault(group => group.Count() > 1);\n        if (duplicateAction is not null)\n            throw new InvalidDataException($"Environment profile contains duplicate action key '{duplicateAction.Key}'.");\n''',
    '''        var duplicateAction = profile.Actions\n            .Where(action => action is not null)\n            .GroupBy(action => action!.Key, StringComparer.OrdinalIgnoreCase)\n            .FirstOrDefault(group => group.Count() > 1);\n        if (duplicateAction is not null)\n            throw new InvalidDataException($"Environment profile contains duplicate action key '{duplicateAction.Key}'.");\n        ProjectActionService.ValidateDefinitions(profile.Actions.Cast<ProjectActionDefinition?>());\n''')

# Domain validation must be consistent with Site/TLS validation rather than accepting
# malformed labels that only fail after work has already started.
replace_once(
    "src/DevBox.Core/Services/GitProjectBootstrapService.cs",
    '''    private static string NormalizeDomain(string value)\n    {\n        ArgumentException.ThrowIfNullOrWhiteSpace(value);\n        var normalized = value.Trim().ToLowerInvariant();\n        if (!normalized.EndsWith(".test", StringComparison.Ordinal) || normalized.Length > 253 || normalized.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '.'))\n            throw new ArgumentException("Project domain must be a normalized .test domain.", nameof(value));\n        return normalized;\n    }\n''',
    '''    private static string NormalizeDomain(string value) => LocalCertificateManager.NormalizeDomain(value);\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''    private static string NormalizeDomain(string value)\n    {\n        ArgumentException.ThrowIfNullOrWhiteSpace(value);\n        var normalized = value.Trim().ToLowerInvariant();\n        if (!normalized.EndsWith(".test", StringComparison.Ordinal) || normalized.Length > 253 || normalized.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '.'))\n            throw new ArgumentException("Project domain must be a valid .test domain.", nameof(value));\n        return normalized;\n    }\n''',
    '''    private static string NormalizeDomain(string value) => LocalCertificateManager.NormalizeDomain(value);\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectWorkspaceService.cs",
    '''        if (!manifest.Domain.EndsWith(".test", StringComparison.OrdinalIgnoreCase))\n        {\n            throw new InvalidDataException("Manifest domain must end with .test.");\n        }\n''',
    '''        try\n        {\n            _ = LocalCertificateManager.NormalizeDomain(manifest.Domain);\n        }\n        catch (ArgumentException ex)\n        {\n            throw new InvalidDataException("Manifest domain is not a valid .test domain.", ex);\n        }\n''')

# Preserve the primary operation failure when rollback/cleanup also fails.
replace_once(
    "src/DevBox.Core/Services/GitProjectBootstrapService.cs",
    '''        catch\n        {\n            if (Directory.Exists(destination))\n''',
    '''        catch (Exception original)\n        {\n            if (Directory.Exists(destination))\n''')
replace_once(
    "src/DevBox.Core/Services/GitProjectBootstrapService.cs",
    '''            catch (Exception rollbackError)\n            {\n                throw new AggregateException("Git bootstrap failed and TLS rollback was incomplete.", rollbackError);\n            }\n            throw;\n''',
    '''            catch (Exception rollbackError)\n            {\n                throw new AggregateException(\n                    "Git bootstrap failed and TLS rollback was incomplete.",\n                    new[] { original, rollbackError });\n            }\n            throw;\n''')
replace_once(
    "src/DevBox.Core/Services/WordPressToolkitService.cs",
    '''        catch\n        {\n            var cleanupErrors = new List<Exception>();\n''',
    '''        catch (Exception original)\n        {\n            var cleanupErrors = new List<Exception>();\n''')
replace_once(
    "src/DevBox.Core/Services/WordPressToolkitService.cs",
    '''            if (cleanupErrors.Count > 0)\n                throw new AggregateException("WordPress setup failed and cleanup was incomplete.", cleanupErrors);\n            throw;\n''',
    '''            if (cleanupErrors.Count > 0)\n                throw new AggregateException(\n                    "WordPress setup failed and cleanup was incomplete.",\n                    new[] { original }.Concat(cleanupErrors));\n            throw;\n''')

# Configuration APIs must enforce the same 2 MiB limit before reading a potentially huge file.
replace_once(
    "src/DevBox.Core/Services/ConfigurationFileService.cs",
    '''public sealed class ConfigurationFileService\n{\n    private readonly string _rootPath;\n''',
    '''public sealed class ConfigurationFileService\n{\n    private const long MaximumConfigurationBytes = 2L * 1024 * 1024;\n    private readonly string _rootPath;\n''')
replace_once(
    "src/DevBox.Core/Services/ConfigurationFileService.cs",
    '''        return File.ReadAllText(path);\n    }\n''',
    '''        return ReadConfigurationText(path, $"Configuration '{key}'");\n    }\n''')
replace_once(
    "src/DevBox.Core/Services/ConfigurationFileService.cs",
    '''        if (content.Length > 2 * 1024 * 1024)\n''',
    '''        if (content.Length > MaximumConfigurationBytes)\n''')
replace_once(
    "src/DevBox.Core/Services/ConfigurationFileService.cs",
    '''        var content = File.ReadAllText(source);\n        var validation = ValidateAsync(normalized, content).ConfigureAwait(false).GetAwaiter().GetResult();\n''',
    '''        var content = ReadConfigurationText(source, "Configuration backup");\n        var validation = ValidateAsync(normalized, content).ConfigureAwait(false).GetAwaiter().GetResult();\n''')
replace_once(
    "src/DevBox.Core/Services/ConfigurationFileService.cs",
    '''    private static string NormalizeKey(string key)\n''',
    '''    private static string ReadConfigurationText(string path, string displayName)\n    {\n        var info = new FileInfo(path);\n        if (info.Length > MaximumConfigurationBytes)\n            throw new InvalidDataException($"{displayName} exceeds the {MaximumConfigurationBytes} byte safety limit.");\n        return File.ReadAllText(path);\n    }\n\n    private static string NormalizeKey(string key)\n''')

# Bound process output in remaining command wrappers. The reader continues draining after
# the retained prefix, preserving process liveness while bounding memory.
for path, pairs in {
    "src/DevBox.Core/Services/DeveloperToolsService.cs": [
        ("var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);", "var outputTask = ProcessOutputCapture.ReadBoundedAsync(process.StandardOutput, cancellationToken: cancellationToken);"),
        ("var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);", "var errorTask = ProcessOutputCapture.ReadBoundedAsync(process.StandardError, cancellationToken: cancellationToken);")],
    "src/DevBox.Core/Services/PhpExtensionInspector.cs": [
        ("var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);", "var outputTask = ProcessOutputCapture.ReadBoundedAsync(process.StandardOutput, cancellationToken: cancellationToken);"),
        ("var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);", "var errorTask = ProcessOutputCapture.ReadBoundedAsync(process.StandardError, cancellationToken: cancellationToken);")],
    "src/DevBox.Core/Services/ConfigurationFileService.cs": [
        ("var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);", "var stdout = ProcessOutputCapture.ReadBoundedAsync(process.StandardOutput, 16 * 1024, cancellationToken);"),
        ("var stderr = process.StandardError.ReadToEndAsync(cancellationToken);", "var stderr = ProcessOutputCapture.ReadBoundedAsync(process.StandardError, 16 * 1024, cancellationToken);")],
    "src/DevBox.Core/Services/GitProjectBootstrapService.cs": [
        ("var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);", "var stdout = ProcessOutputCapture.ReadBoundedAsync(process.StandardOutput, cancellationToken: cancellationToken);"),
        ("var stderr = process.StandardError.ReadToEndAsync(cancellationToken);", "var stderr = ProcessOutputCapture.ReadBoundedAsync(process.StandardError, cancellationToken: cancellationToken);")],
    "src/DevBox.Core/Services/ProjectDatabaseProvisioner.cs": [
        ("var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);", "var stdout = ProcessOutputCapture.ReadBoundedAsync(process.StandardOutput, cancellationToken: cancellationToken);"),
        ("var stderr = process.StandardError.ReadToEndAsync(cancellationToken);", "var stderr = ProcessOutputCapture.ReadBoundedAsync(process.StandardError, cancellationToken: cancellationToken);")],
    "src/DevBox.Core/Services/ProcessManager.cs": [
        ("var outputTask = stopProcess.StandardOutput.ReadToEndAsync(cancellationToken);", "var outputTask = ProcessOutputCapture.ReadBoundedAsync(stopProcess.StandardOutput, 64 * 1024, cancellationToken);"),
        ("var errorTask = stopProcess.StandardError.ReadToEndAsync(cancellationToken);", "var errorTask = ProcessOutputCapture.ReadBoundedAsync(stopProcess.StandardError, 64 * 1024, cancellationToken);")],
}.items():
    for old, new in pairs:
        replace_once(path, old, new)

# DatabaseManager textual results/errors need bounds, while database dumps continue to
# stream directly to the destination file.
replace_once(
    "src/DevBox.Core/Services/DatabaseManager.cs",
    '''        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);\n        Task<string>? outputTask = standardOutputPath is null\n            ? process.StandardOutput.ReadToEndAsync(cancellationToken)\n            : null;\n''',
    '''        var errorTask = ProcessOutputCapture.ReadBoundedAsync(process.StandardError, cancellationToken: cancellationToken);\n        Task<string>? outputTask = standardOutputPath is null\n            ? ProcessOutputCapture.ReadBoundedAsync(process.StandardOutput, cancellationToken: cancellationToken)\n            : null;\n''')

# WP-CLI: start bounded draining immediately and include stdin writes in the cancellation
# kill scope, so cancellation cannot strand an interactive child process.
replace_once(
    "src/DevBox.Core/Services/WordPressToolkitService.cs",
    '''        if (standardInput is not null)\n        {\n            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);\n            process.StandardInput.Close();\n        }\n        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);\n        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);\n        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);\n        timeout.CancelAfter(TimeSpan.FromMinutes(10));\n        try\n        {\n            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);\n        }\n''',
    '''        var stdout = ProcessOutputCapture.ReadBoundedAsync(process.StandardOutput, cancellationToken: cancellationToken);\n        var stderr = ProcessOutputCapture.ReadBoundedAsync(process.StandardError, cancellationToken: cancellationToken);\n        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);\n        timeout.CancelAfter(TimeSpan.FromMinutes(10));\n        try\n        {\n            if (standardInput is not null)\n            {\n                await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeout.Token).ConfigureAwait(false);\n                process.StandardInput.Close();\n            }\n            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);\n        }\n''')
replace_once(
    "src/DevBox.Core/Services/WordPressToolkitService.cs",
    '''        return new WpCliResult(process.ExitCode, Truncate(await stdout.ConfigureAwait(false)), Truncate(await stderr.ConfigureAwait(false)));\n''',
    '''        return new WpCliResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));\n''')

# Composer installation cleanup is non-critical and must not turn a successful install
# into failure or replace the primary exception.
replace_once(
    "src/DevBox.Core/Services/DeveloperToolsService.cs",
    '''        finally\n        {\n            if (Directory.Exists(tempRoot))\n            {\n                Directory.Delete(tempRoot, recursive: true);\n            }\n        }\n''',
    '''        finally\n        {\n            TryDeleteDirectory(tempRoot);\n        }\n''')
replace_once(
    "src/DevBox.Core/Services/DeveloperToolsService.cs",
    '''    private static void TryKill(Process process)\n''',
    '''    private static void TryDeleteDirectory(string path)\n    {\n        try\n        {\n            if (Directory.Exists(path))\n                Directory.Delete(path, recursive: true);\n        }\n        catch (IOException) { }\n        catch (UnauthorizedAccessException) { }\n    }\n\n    private static void TryKill(Process process)\n''')

# Regression coverage for the concrete bugs in this round.
Path("tests/DevBox.Tests/FinalBugSweepRound10Tests.cs").write_text('''using System.IO.Compression;\nusing DevBox.Core.Models;\nusing DevBox.Core.Services;\nusing Xunit;\n\nnamespace DevBox.Tests;\n\npublic sealed class FinalBugSweepRound10Tests\n{\n    [Fact]\n    public void ArchiveSafety_RejectsCaseInsensitiveDuplicateOutputPath()\n    {\n        var root = NewRoot();\n        try\n        {\n            var archivePath = Path.Combine(root, "duplicate.zip");\n            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))\n            {\n                WriteEntry(archive, "payload/tool.txt", "one");\n                WriteEntry(archive, "PAYLOAD/TOOL.TXT", "two");\n            }\n\n            Assert.Throws<InvalidDataException>(() => ArchiveSafety.ExtractZipSafely(\n                archivePath, Path.Combine(root, "out"), 1024 * 1024, 100, "fixture"));\n        }\n        finally { Delete(root); }\n    }\n\n    [Fact]\n    public void PathSafety_RejectsProtectedRootThatIsAReparsePoint_WhenSupported()\n    {\n        var parent = NewRoot();\n        var target = Path.Combine(parent, "target");\n        var link = Path.Combine(parent, "linked-root");\n        Directory.CreateDirectory(target);\n        try\n        {\n            try { Directory.CreateSymbolicLink(link, target); }\n            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }\n            var child = Path.Combine(link, "child");\n            Directory.CreateDirectory(child);\n            Assert.Throws<InvalidOperationException>(() =>\n                PathSafety.EnsureUnderRootWithoutReparsePoints(link, child, "unsafe"));\n        }\n        finally { Delete(parent); }\n    }\n\n    [Fact]\n    public void ManagedServiceCatalog_RejectsExecutableThroughReparsePoint_WhenSupported()\n    {\n        var root = NewRoot();\n        var external = Path.Combine(Path.GetTempPath(), "devbox-round10-external", Guid.NewGuid().ToString("N"));\n        Directory.CreateDirectory(external);\n        File.WriteAllText(Path.Combine(external, "tool.exe"), "fixture");\n        var link = Path.Combine(root, "linked");\n        try\n        {\n            try { Directory.CreateSymbolicLink(link, external); }\n            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }\n            var manifest = new ManagedServiceManifest(\n                ManagedServiceManifest.CurrentSchemaVersion, "fixture", "Fixture",\n                "linked/tool.exe", Array.Empty<string>(), ".", 8123, "1.0");\n            Assert.Throws<InvalidDataException>(() => new ManagedServiceCatalog(root).Save([manifest]));\n        }\n        finally { Delete(root); Delete(external); }\n    }\n\n    [Fact]\n    public void EnvironmentProfile_RejectsActionUnsupportedByProjectActionPolicy()\n    {\n        var root = NewRoot();\n        try\n        {\n            var profile = new EnvironmentProfile\n            {\n                Key = "bad-action",\n                DisplayName = "Bad action",\n                Kind = ProjectKind.Php,\n                Actions = [new ProjectActionDefinition("run", "Run", "powershell", ["-NoProfile"])]\n            };\n            Assert.Throws<InvalidDataException>(() => new EnvironmentProfileService(root).SaveCustomProfile(profile));\n        }\n        finally { Delete(root); }\n    }\n\n    [Fact]\n    public async Task GitBootstrap_RejectsMalformedTestDomainBeforeResolvingGit()\n    {\n        var root = NewRoot();\n        try\n        {\n            var service = new GitProjectBootstrapService(root);\n            await Assert.ThrowsAsync<ArgumentException>(() => service.BootstrapAsync(new GitBootstrapRequest(\n                "https://example.com/repository.git", "demo", Domain: "bad..test")));\n        }\n        finally { Delete(root); }\n    }\n\n    [Fact]\n    public void ConfigurationRestore_RejectsOversizedBackupBeforeReadingIt()\n    {\n        var root = NewRoot();\n        try\n        {\n            var backupRoot = Path.Combine(root, "backups", "configuration");\n            Directory.CreateDirectory(backupRoot);\n            var backup = Path.Combine(backupRoot, "nginx-oversized.conf.bak");\n            using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write))\n                stream.SetLength(2L * 1024 * 1024 + 1);\n            Assert.Throws<InvalidDataException>(() => new ConfigurationFileService(root).RestoreBackup("nginx", backup));\n        }\n        finally { Delete(root); }\n    }\n\n    [Fact]\n    public async Task ProcessOutputCapture_DrainsButBoundsRetainedText()\n    {\n        var input = new string('x', 100_000);\n        var output = await ProcessOutputCapture.ReadBoundedAsync(new StringReader(input), 1024);\n        Assert.StartsWith(new string('x', 1024), output, StringComparison.Ordinal);\n        Assert.Contains("[output truncated by DevBox]", output, StringComparison.Ordinal);\n        Assert.True(output.Length < 1200);\n    }\n\n    private static void WriteEntry(ZipArchive archive, string name, string content)\n    {\n        var entry = archive.CreateEntry(name);\n        using var writer = new StreamWriter(entry.Open());\n        writer.Write(content);\n    }\n\n    private static string NewRoot()\n    {\n        var root = Path.Combine(Path.GetTempPath(), "devbox-round10", Guid.NewGuid().ToString("N"));\n        Directory.CreateDirectory(root);\n        Directory.CreateDirectory(Path.Combine(root, "www"));\n        Directory.CreateDirectory(Path.Combine(root, "config"));\n        return root;\n    }\n\n    private static void Delete(string path)\n    {\n        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }\n    }\n}\n''', encoding="utf-8")

# Changelog: document every concrete issue fixed in this audit round.
changelog = Path("CHANGELOG.md")
text = changelog.read_text(encoding="utf-8")
marker = "## Unreleased\n"
fixed = "### Fixed\n"
start = text.index(marker)
pos = text.index(fixed, start) + len(fixed)
entries = '''\n- ZIP extraction rejects duplicate and case-insensitive alias output paths so later archive entries cannot overwrite previously validated runtime/ADDON files.\n- Protected-path validation rejects a `www`/backup/service root that is itself a junction or symbolic link; managed-service executable, working-directory, stop-executable and log paths now use the same reparse-aware boundary checks.\n- Environment profiles reuse the canonical Project Action policy, preventing profiles with unsupported executables or incompatible action definitions from being saved and then failing only during apply.\n- Git bootstrap, project transfer and project manifests use the same strict `.test` domain validation as Sites/TLS, rejecting empty labels, oversized labels and leading/trailing hyphens before mutation begins.\n- Git bootstrap and WordPress setup preserve the original operation exception when TLS/database/site cleanup also fails, instead of reporting only the rollback failure.\n- Developer tools, PHP extension checks, configuration validators, Git bootstrap, WordPress CLI, project database clients, MySQL text commands and service stop helpers retain bounded stdout/stderr while continuing to drain child-process pipes.\n- WP-CLI cancellation now covers stdin writes as well as process waiting, terminating the child process instead of leaving an interactive command running after cancellation.\n- Configuration read/restore enforces the 2 MiB safety limit before loading a configuration or backup into memory.\n- Composer installer temporary-directory cleanup is best-effort so an antivirus/lock cleanup error cannot replace the primary installation result.\n'''
text = text[:pos] + entries + text[pos:]
changelog.write_text(text, encoding="utf-8")
