from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:120]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


def replace_between(path: str, start: str, end: str, replacement: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    i = text.index(start)
    j = text.index(end, i)
    file.write_text(text[:i] + replacement + text[j:], encoding="utf-8")


# A corrupt pre-bundled/versioned runtime must not permanently block repair when a
# verified remote package is available.
replace_once(
    "src/DevBox.Core/Services/RuntimeManager.cs",
    '''        var bundledPath = VersionPath(definition.Key, definition.Version);\n        if (Directory.Exists(bundledPath))\n        {\n            ValidateRuntimeExecutable(bundledPath, definition.ExecutableRelativePath);\n            await ActivateUnderLockAsync(\n                definition.Key,\n                definition.Version,\n                definition.ExecutableRelativePath,\n                cancellationToken).ConfigureAwait(false);\n            return;\n        }\n\n        if (!definition.HasRemotePackage)\n''',
    '''        var bundledPath = VersionPath(definition.Key, definition.Version);\n        if (Directory.Exists(bundledPath))\n        {\n            try\n            {\n                ValidateRuntimeExecutable(bundledPath, definition.ExecutableRelativePath);\n                await ActivateUnderLockAsync(\n                    definition.Key,\n                    definition.Version,\n                    definition.ExecutableRelativePath,\n                    cancellationToken).ConfigureAwait(false);\n                return;\n            }\n            catch (InvalidDataException) when (definition.HasRemotePackage)\n            {\n                QuarantineInvalidRuntime(bundledPath);\n            }\n        }\n\n        if (!definition.HasRemotePackage)\n''')
replace_once(
    "src/DevBox.Core/Services/RuntimeManager.cs",
    '''    private static void TryDeleteDirectory(string path)\n    {\n''',
    '''    private static void QuarantineInvalidRuntime(string path)\n    {\n        if (!Directory.Exists(path))\n            return;\n        var parent = Path.GetDirectoryName(Path.GetFullPath(path))\n            ?? throw new InvalidOperationException("Runtime path has no parent directory.");\n        var name = Path.GetFileName(path);\n        var quarantine = Path.Combine(parent, $".invalid-{name}-{Guid.NewGuid():N}");\n        Directory.Move(path, quarantine);\n        TryDeleteDirectory(quarantine);\n    }\n\n    private static void TryDeleteDirectory(string path)\n    {\n''')

# Harden custom runtime catalog path segments. Previously '..' could escape the
# intended runtime directory and relative executable/archive paths could contain
# parent traversal.
replace_between(
    "src/DevBox.Core/Services/RuntimePlatformService.cs",
    "    private static void ValidatePackage(RuntimePackageEntry package)",
    "    private static void VerifySha256(string path, string expected)",
    '''    private static void ValidatePackage(RuntimePackageEntry package)\n    {\n        if (!IsSafePathSegment(package.Key))\n            throw new InvalidDataException("Runtime key is invalid.");\n        if (!IsSafePathSegment(package.Version))\n            throw new InvalidDataException("Runtime version is invalid.");\n        ValidateRelativePackagePath(package.ExecutableRelativePath, "Runtime executable path");\n        if (!string.IsNullOrWhiteSpace(package.ArchiveRootDirectory))\n            ValidateRelativePackagePath(package.ArchiveRootDirectory, "Runtime archive root");\n        if (string.IsNullOrWhiteSpace(package.Architecture) || package.Architecture.Length > 32)\n            throw new InvalidDataException("Runtime architecture is invalid.");\n        if (!string.IsNullOrWhiteSpace(package.DownloadUrl))\n        {\n            if (!Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)\n                throw new InvalidDataException("Runtime catalog download URL must use HTTPS.");\n            if (string.IsNullOrWhiteSpace(package.Sha256) || package.Sha256.Trim().Length != 64 || !package.Sha256.Trim().All(Uri.IsHexDigit))\n                throw new InvalidDataException("Remote runtime catalog entries require a valid pinned SHA-256 digest.");\n        }\n    }\n\n    private static bool IsSafePathSegment(string value) =>\n        !string.IsNullOrWhiteSpace(value) &&\n        value is not "." and not ".." &&\n        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&\n        !value.Contains(Path.DirectorySeparatorChar) &&\n        !value.Contains(Path.AltDirectorySeparatorChar);\n\n    private static void ValidateRelativePackagePath(string value, string description)\n    {\n        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))\n            throw new InvalidDataException($"{description} must be relative.");\n        var normalized = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);\n        if (normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))\n            throw new InvalidDataException($"{description} cannot contain parent traversal.");\n    }\n\n''')

# CLI invalid ports should be a controlled user error, never an unhandled
# FormatException/OverflowException.
replace_once(
    "src/DevBox.Cli/Program.cs",
    '''                case "register" when args.Count is 4 or 5:\n                {\n                    var port = args.Count == 5 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : (int?)null;\n                    var value = runtime.Register(args[2], args[3], port);\n''',
    '''                case "register" when args.Count is 4 or 5:\n                {\n                    int? port = null;\n                    if (args.Count == 5)\n                    {\n                        if (!int.TryParse(args[4], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedPort) || parsedPort is < 1 or > 65535)\n                            return Fail("Database runtime port must be a number from 1 to 65535.", json);\n                        port = parsedPort;\n                    }\n                    var value = runtime.Register(args[2], args[3], port);\n''')
replace_once(
    "src/DevBox.Cli/Program.cs",
    '''        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or ArgumentException or NotSupportedException or KeyNotFoundException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)\n''',
    '''        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or ArgumentException or NotSupportedException or KeyNotFoundException or FormatException or OverflowException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)\n''')

# Regression tests for runtime repair and process startup validation.
runtime_tests = Path("tests/DevBox.Tests/RuntimeManagerTests.cs")
text = runtime_tests.read_text(encoding="utf-8")
marker = "    private static byte[] CreateArchive"
insert = '''    [Fact]\n    public async Task InstallAsync_CorruptExistingRuntime_RedownloadsVerifiedPackage()\n    {\n        var root = TemporaryRoot();\n        try\n        {\n            var broken = Path.Combine(root, "runtime", "php", "8.4.0");\n            Directory.CreateDirectory(broken);\n            File.WriteAllText(Path.Combine(broken, "incomplete.txt"), "broken");\n            var package = CreateArchive(("package/php.exe", "repaired"));\n            var checksum = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();\n            using var client = new HttpClient(new StaticHandler(package));\n            using var manager = new RuntimeManager(root, client);\n            var definition = new RuntimeDefinition(\n                "php", "PHP", "8.4.0", "https://example.test/php.zip", checksum, "php.exe", "package");\n\n            await manager.InstallAsync(definition);\n\n            Assert.Equal("repaired", File.ReadAllText(Path.Combine(root, "runtime", "php", "8.4.0", "php.exe")));\n            Assert.True(File.Exists(Path.Combine(root, "runtime", "php", "current", "php.exe")));\n        }\n        finally\n        {\n            DeleteRoot(root);\n        }\n    }\n\n'''
if marker not in text or "InstallAsync_CorruptExistingRuntime_RedownloadsVerifiedPackage" in text:
    raise RuntimeError("RuntimeManagerTests insertion marker mismatch")
runtime_tests.write_text(text.replace(marker, insert + marker, 1), encoding="utf-8")

process_tests = Path("tests/DevBox.Tests/ProcessManagerTests.cs")
text = process_tests.read_text(encoding="utf-8")
marker = "    [Fact]\n    public void RuntimeLayout_CreatesExpectedConfigFiles()"
insert = '''    [Fact]\n    public async Task StartAsync_ProcessThatExitsImmediately_IsNotReportedRunning()\n    {\n        using var manager = new ProcessManager();\n        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");\n        var definition = new ServiceDefinition(\n            "early-exit", "Early exit", executable,\n            new[] { "-NoProfile", "-NonInteractive", "-Command", "exit 7" },\n            Path.GetTempPath(), 0, "test");\n\n        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(definition));\n\n        Assert.Contains("exited during startup", error.Message, StringComparison.OrdinalIgnoreCase);\n        Assert.Equal(ServiceState.Stopped, manager.GetStatus(definition).State);\n    }\n\n    [Fact]\n    public async Task StartAsync_UnwritableLogPath_DoesNotCrashServiceLifecycle()\n    {\n        var root = Path.Combine(Path.GetTempPath(), "devbox-process-tests", Guid.NewGuid().ToString("N"));\n        try\n        {\n            Directory.CreateDirectory(root);\n            var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");\n            var definition = new ServiceDefinition(\n                "bad-log", "Bad log", executable,\n                new[] { "-NoProfile", "-NonInteractive", "-Command", "Write-Output ok; Start-Sleep -Seconds 5" },\n                root, 0, "test",\n                ShutdownTimeout: TimeSpan.FromMilliseconds(100),\n                LogPath: root);\n            using var manager = new ProcessManager();\n\n            var started = await manager.StartAsync(definition);\n            Assert.Equal(ServiceState.Running, started.State);\n            await manager.StopAsync(definition);\n        }\n        finally\n        {\n            if (Directory.Exists(root)) Directory.Delete(root, true);\n        }\n    }\n\n'''
if marker not in text or "StartAsync_ProcessThatExitsImmediately_IsNotReportedRunning" in text:
    raise RuntimeError("ProcessManagerTests insertion marker mismatch")
process_tests.write_text(text.replace(marker, insert + marker, 1), encoding="utf-8")
