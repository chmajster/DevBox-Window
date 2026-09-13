from pathlib import Path


def replace(path, old, new):
    target = Path(path)
    text = target.read_text(encoding="utf-8")
    if text.count(old) != 1:
        raise RuntimeError(f"Expected one exact patch anchor in {path}")
    target.write_text(text.replace(old, new), encoding="utf-8", newline="\n")

core = 'src/DevBox.Core/Services/'

replace(core + 'ManagedServiceCatalog.cs',
    '    private void SaveUnderLock(IReadOnlyCollection<ManagedServiceManifest> manifests)\n    {\n        Directory.CreateDirectory',
    '    private void SaveUnderLock(IReadOnlyCollection<ManagedServiceManifest> manifests)\n    {\n        // Validate the resulting catalog, not just the item supplied to Upsert.\n        // A conflicting write must never replace a previously readable catalog.\n        ValidateAll(manifests);\n        Directory.CreateDirectory')
replace(core + 'ManagedServiceCatalog.cs',
    '    private void Validate(ManagedServiceManifest manifest)\n    {\n        if (manifest.SchemaVersion',
    '    private void Validate(ManagedServiceManifest manifest)\n    {\n        if (manifest is null)\n            throw new InvalidDataException("Managed service catalog contains a null entry.");\n        if (manifest.SchemaVersion')
replace(core + 'ManagedServiceCatalog.cs',
    'if (portValue is null || !portValue.Value.TryGetInt32(out var registeredPort) || registeredPort is < 1 or > 65535)',
    'if (portValue is null || portValue.Value.ValueKind != JsonValueKind.Number ||\n                    !portValue.Value.TryGetInt32(out var registeredPort) || registeredPort is < 1 or > 65535)')
replace(core + 'PhpManager.cs',
    '                configured[parsed.Value.Name] = parsed.Value.Enabled;',
    '                // A commented example does not unload an earlier active extension.\n                configured[parsed.Value.Name] = parsed.Value.Enabled ||\n                    (configured.TryGetValue(parsed.Value.Name, out var enabled) && enabled);')
replace(core + 'XdebugConfigurationService.cs',
    r'''        var value = normalized[(separator + 1)..].Trim().Trim('"', '\'');
        return value.EndsWith("php_xdebug.dll", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith("xdebug.dll", StringComparison.OrdinalIgnoreCase);''',
    r'''        var value = ParseDirectiveValue(normalized[(separator + 1)..]);
        // Match a complete basename, not unrelated modules such as not_xdebug.dll.
        var name = value.Replace('\\', '/').Split('/')[^1];
        return name.Equals("php_xdebug.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("xdebug.dll", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("xdebug", StringComparison.OrdinalIgnoreCase);''')
replace(core + 'XdebugConfigurationService.cs',
    '    private static string? ReadDirective(IEnumerable<string> lines, string name)\n    {\n        foreach',
    '    private static string? ReadDirective(IEnumerable<string> lines, string name)\n    {\n        string? result = null;\n        foreach')
replace(core + 'XdebugConfigurationService.cs',
    r'''            var value = trimmed[(separator + 1)..];
            var comment = value.IndexOf(';');
            if (comment >= 0)
            {
                value = value[..comment];
            }
            return value.Trim().Trim('"', '\'');
        }
        return null;
    }
''',
    r'''            // PHP uses the last active value for scalar INI directives.
            result = ParseDirectiveValue(trimmed[(separator + 1)..]);
        }
        return result;
    }

    private static string ParseDirectiveValue(string value)
    {
        char quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
            }
            else if (character is '\'' or '"')
                quote = character;
            else if (character == ';')
            {
                value = value[..index];
                break;
            }
        }
        return value.Trim().Trim('"', '\'');
    }
''')
replace(core + 'PathSafety.cs',
    '        if ((Directory.Exists(fullRoot) || File.Exists(fullRoot)) &&\n            (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)',
    '        if (IsReparsePoint(fullRoot))')
replace(core + 'PathSafety.cs',
    '            if (!Directory.Exists(current) && !File.Exists(current))\n                continue;\n            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)',
    '            if (IsReparsePoint(current))')
replace(core + 'PathSafety.cs',
    '        return fullCandidate;\n    }\n}',
    '''        return fullCandidate;
    }

    private static bool IsReparsePoint(string path)
    {
        // Exists() follows links and returns false for dangling links. Inspect the
        // entry itself before deciding that a path segment is safe to create.
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}''')
replace(core + 'LogReader.cs',
    '    public IReadOnlyList<string> GetAvailableLogs()\n    {\n        if',
    '    public IReadOnlyList<string> GetAvailableLogs()\n    {\n        _ = PathSafety.EnsureUnderRootWithoutReparsePoints(\n            _logsRoot, _logsRoot, "The logs directory cannot be a reparse point.", allowRoot: true);\n        if')
replace(core + 'LogReader.cs',
    '        return Directory.GetFiles(_logsRoot, "*.log", SearchOption.TopDirectoryOnly)\n            .Select',
    '        return Directory.GetFiles(_logsRoot, "*.log", SearchOption.TopDirectoryOnly)\n            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)\n            .Select')
replace(core + 'LogReader.cs',
    '    private string ResolveSafePath(string fileName)\n    {\n        if',
    '    private string ResolveSafePath(string fileName)\n    {\n        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);\n        if')
replace(core + 'LogReader.cs',
    '        return path;\n    }\n}',
    '        return PathSafety.EnsureUnderRootWithoutReparsePoints(\n            _logsRoot, path, "Log paths cannot traverse a reparse point.");\n    }\n}')
replace(core + 'ApplicationUpdateService.cs',
    '        if (!root.TryGetProperty("tag_name", out var tagElement) ||\n            !root.TryGetProperty("html_url", out var urlElement))',
    '        if (root.ValueKind != JsonValueKind.Object ||\n            !root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String ||\n            !root.TryGetProperty("html_url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)')
replace(core + 'ApplicationSelfUpdateService.cs',
    '        if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String ||',
    '        if (root.ValueKind != JsonValueKind.Object ||\n            !root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String ||')
replace(core + 'ApplicationSelfUpdateService.cs',
    '''            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            if (name is null || url is null)
                continue;
''',
    '''            if (asset.ValueKind != JsonValueKind.Object ||
                !asset.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                !asset.TryGetProperty("browser_download_url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("GitHub release contains an invalid asset entry.");
            var name = nameElement.GetString()!;
            var url = urlElement.GetString()!;
''')
replace('src/DevBox.App/ViewModels/UpdateWindowViewModel.cs',
    'ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)',
    'ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or TimeoutException)')
replace('src/DevBox.App/ViewModels/UpdateWindowViewModel.cs',
    'ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or Win32Exception or PlatformNotSupportedException)',
    'ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or Win32Exception or PlatformNotSupportedException or OperationCanceledException or TimeoutException)')
replace('src/DevBox.App/ViewModels/UpdateWindowViewModel.cs',
    '            Status = "Checking GitHub Releases...";\n            var result',
    '            Status = "Checking GitHub Releases...";\n            UpdateAvailable = false;\n            ReleaseUrl = null;\n            var result')
replace('src/DevBox.Cli/Program.cs',
    '''            var root = ResolveRoot();
            RuntimeLayout.EnsureInitialized(root);
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            return''',
    '''            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }
            var root = ResolveRoot();
            RuntimeLayout.EnsureInitialized(root);

            return''')
replace('src/DevBox.Cli/Program.cs',
    'or System.Security.Cryptography.CryptographicException)',
    'or System.Security.Cryptography.CryptographicException or OperationCanceledException or TimeoutException)')
replace('src/DevBox.Cli/Program.cs',
    'ex is IOException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException or System.ComponentModel.Win32Exception)',
    'ex is IOException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException or System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)')
replace(core + 'ArchiveSafety.cs',
    '''        Directory.CreateDirectory(destinationPath);
        foreach (var entry in archive.Entries)
        {
            var outputPath = ResolveOutputPath(entry, destinationPath);''',
    '''        EnsureSafeOutputPath(destinationPath, destinationPath, packageName, allowRoot: true);
        Directory.CreateDirectory(destinationPath);
        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var outputPath = ResolveOutputPath(entry, destinationPath);
            EnsureSafeOutputPath(destinationPath, outputPath, packageName);''')
replace(core + 'ArchiveSafety.cs',
    '            entry.ExtractToFile(outputPath, overwrite: true);',
    '''            using (var source = entry.Open())
            using (var target = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long entryBytes = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // ZIP size fields are untrusted: enforce limits before every write.
                    if (read > maximumUncompressedBytes - extractedBytes || read > entry.Length - entryBytes)
                        throw new InvalidDataException($"{packageName} archive expands beyond its declared or allowed size.");
                    target.Write(buffer, 0, read);
                    extractedBytes += read;
                    entryBytes += read;
                }
                if (entryBytes != entry.Length)
                    throw new InvalidDataException($"{packageName} archive contains a truncated entry: {entry.FullName}");
            }
            File.SetLastWriteTime(outputPath, entry.LastWriteTime.DateTime);''')
replace(core + 'ArchiveSafety.cs',
    "        var normalizedEntry = entry.FullName.Replace('/', Path.DirectorySeparatorChar);\n        if (Path.IsPathRooted(normalizedEntry) || normalizedEntry.Contains(':'))",
    "        var normalizedEntry = entry.FullName.Replace('\\\\', '/');\n        if (Path.IsPathRooted(normalizedEntry) || normalizedEntry.StartsWith('/') || normalizedEntry.Contains(':'))")
replace(core + 'ArchiveSafety.cs',
    '        var outputPath = ResolveOutputPath(entry, destinationPath);\n        if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))',
    r'''        foreach (var segment in normalizedEntry.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(character => character < 32 || "<>\"|?*".Contains(character)) || IsWindowsDeviceName(segment))
                throw new InvalidDataException($"Unsafe Windows ZIP entry detected in {packageName}: {entry.FullName}");
        }

        var outputPath = ResolveOutputPath(entry, destinationPath);
        if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))''')
replace(core + 'ArchiveSafety.cs',
    '''            throw new InvalidDataException($"Unsafe ZIP entry detected in {packageName}: {entry.FullName}");
        }
    }

    private static string ResolveOutputPath''',
    '''            throw new InvalidDataException($"Unsafe ZIP entry detected in {packageName}: {entry.FullName}");
        }
        EnsureSafeOutputPath(destinationPath, outputPath, packageName);
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var name = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(name[3]));
    }

    private static void EnsureSafeOutputPath(string root, string output, string packageName, bool allowRoot = false)
    {
        try
        {
            _ = PathSafety.EnsureUnderRootWithoutReparsePoints(
                root, output, $"{packageName} extraction cannot traverse a reparse point.", allowRoot);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    private static string ResolveOutputPath''')
replace(core + 'ArchiveSafety.cs',
    "Path.GetFullPath(Path.Combine(destinationPath, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));",
    "Path.GetFullPath(Path.Combine(destinationPath, entry.FullName.Replace('\\\\', '/').Replace('/', Path.DirectorySeparatorChar)));")
replace(core + 'ArchiveSafety.cs',
    '            if (string.IsNullOrEmpty(entry.Name))',
    "            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\\\'))")
replace('CHANGELOG.md', '### Fixed\n\n- Task Center creates',
    '''### Fixed

- Managed-service upserts validate the complete resulting catalog before an atomic write, preventing duplicate enabled ports from corrupting `services.json`; null entries and non-numeric database reservation ports produce controlled validation errors.
- PHP extension status no longer treats a later commented example as disabling an earlier active extension.
- Xdebug detection understands inline comments, quoted Windows paths and the `xdebug` module name, matches exact module basenames, and reads the last active scalar INI directive; disabling Xdebug removes all active matching declarations without touching unrelated modules.
- Log listing, reading and clearing reject reparse-point paths; path validation also checks dangling links instead of assuming a failed `Exists()` call makes them safe. Empty log names are rejected consistently.
- ZIP extraction rejects Windows trailing-dot/space aliases, reserved device names and unsafe path components before writing, and refuses pre-existing reparse-point destinations.
- ZIP extraction enforces actual decompressed-byte limits before every write and rejects entries whose streamed size differs from their declared size.
- Update checks and installer downloads validate JSON object/string kinds before reading release and asset properties, returning controlled errors for malformed metadata.
- Update UI handles network cancellation/timeouts and clears stale update actions when a new check starts instead of leaving a failed check stuck or retaining an outdated installer action.
- CLI help no longer initializes or writes to the runtime root; timeout/cancellation failures return the normal CLI error response rather than an unhandled exception.
- Added round-13 regression coverage for these catalog, INI, ZIP, path and updater boundaries; Windows baseline/reproduction/full-suite results are recorded in the pull request.

- Task Center creates''')
print('Applied all round-13 production and changelog fixes.')
