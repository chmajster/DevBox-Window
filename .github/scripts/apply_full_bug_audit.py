from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8")
    if old not in text:
        raise RuntimeError(f"pattern not found in {path}: {old[:120]!r}")
    if text.count(old) != 1:
        raise RuntimeError(f"pattern occurs {text.count(old)} times in {path}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1) Shared bounded HTTP content reader.
replace_once(
    "src/DevBox.Core/Services/ArchiveSafety.cs",
    '''    public static void ExtractZipSafely(
''',
    '''    public static async Task<byte[]> ReadContentBytesWithLimitAsync(
        HttpResponseMessage response,
        long maximumBytes,
        string contentName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentName);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength.HasValue && declaredLength.Value > maximumBytes)
            throw new InvalidDataException($"{contentName} is too large ({declaredLength.Value} bytes; limit {maximumBytes} bytes).");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var target = new MemoryStream();
        await CopyToWithLimitAsync(source, target, maximumBytes, contentName, cancellationToken).ConfigureAwait(false);
        return target.ToArray();
    }

    public static void ExtractZipSafely(
''')

# 2) Update check: bound metadata and normalize malformed JSON.
replace_once(
    "src/DevBox.Core/Services/ApplicationUpdateService.cs",
    '''public sealed partial class ApplicationUpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/chmajster/DevBox-Window/releases/latest";
''',
    '''public sealed partial class ApplicationUpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/chmajster/DevBox-Window/releases/latest";
    private const long MaximumReleaseMetadataBytes = 2L * 1024 * 1024;
''')
replace_once(
    "src/DevBox.Core/Services/ApplicationUpdateService.cs",
    '''        using var response = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var root = document.RootElement;
''',
    '''        using var response = await _httpClient.GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await ArchiveSafety.ReadContentBytesWithLimitAsync(
            response,
            MaximumReleaseMetadataBytes,
            "GitHub release metadata",
            cancellationToken).ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("GitHub release response contains invalid JSON.", ex);
        }
        using (document)
        {
        var root = document.RootElement;
''')
replace_once(
    "src/DevBox.Core/Services/ApplicationUpdateService.cs",
    '''        return new UpdateCheckResult(
            _currentVersion,
            latestVersion,
            latestVersion > _currentVersion,
            releaseUri.AbsoluteUri);
    }
''',
    '''        return new UpdateCheckResult(
            _currentVersion,
            latestVersion,
            latestVersion > _currentVersion,
            releaseUri.AbsoluteUri);
        }
    }
''')

# 3) Self updater: bound metadata/checksum/installer downloads and normalize metadata errors.
replace_once(
    "src/DevBox.Core/Services/ApplicationSelfUpdateService.cs",
    '''using System.Security.Cryptography;
using System.Text.Json;
''',
    '''using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
''')
replace_once(
    "src/DevBox.Core/Services/ApplicationSelfUpdateService.cs",
    '''public sealed class ApplicationSelfUpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/chmajster/DevBox-Window/releases/latest";
''',
    '''public sealed class ApplicationSelfUpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/chmajster/DevBox-Window/releases/latest";
    private const long MaximumReleaseMetadataBytes = 2L * 1024 * 1024;
    private const long MaximumChecksumBytes = 1024 * 1024;
    private const long MaximumInstallerDownloadBytes = 1024L * 1024 * 1024;
''')
replace_once(
    "src/DevBox.Core/Services/ApplicationSelfUpdateService.cs",
    '''        using var releaseResponse = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
        releaseResponse.EnsureSuccessStatusCode();
        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var root = document.RootElement;
        var version = ApplicationUpdateService.ParseReleaseVersion(root.GetProperty("tag_name").GetString());
        if (version <= currentVersion)
            throw new InvalidOperationException("No newer stable DevBox release is available.");

        var releaseUrl = ValidateGitHubUrl(root.GetProperty("html_url").GetString(), "release URL");
''',
    '''        using var releaseResponse = await _httpClient.GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        releaseResponse.EnsureSuccessStatusCode();
        var releasePayload = await ArchiveSafety.ReadContentBytesWithLimitAsync(
            releaseResponse,
            MaximumReleaseMetadataBytes,
            "GitHub release metadata",
            cancellationToken).ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(releasePayload);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("GitHub release response contains invalid JSON.", ex);
        }
        using var releaseDocument = document;
        var root = releaseDocument.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("html_url", out var releaseUrlElement) || releaseUrlElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("GitHub release response is missing tag_name or html_url.");

        var version = ApplicationUpdateService.ParseReleaseVersion(tagElement.GetString());
        if (version <= currentVersion)
            throw new InvalidOperationException("No newer stable DevBox release is available.");

        var releaseUrl = ValidateGitHubUrl(releaseUrlElement.GetString(), "release URL");
''')
replace_once(
    "src/DevBox.Core/Services/ApplicationSelfUpdateService.cs",
    '''        var checksums = await _httpClient.GetStringAsync(checksumsUrl, cancellationToken).ConfigureAwait(false);
        var expectedSha256 = ParseChecksum(checksums, expectedInstallerName);
''',
    '''        using var checksumsResponse = await _httpClient.GetAsync(checksumsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        checksumsResponse.EnsureSuccessStatusCode();
        var checksumsPayload = await ArchiveSafety.ReadContentBytesWithLimitAsync(
            checksumsResponse,
            MaximumChecksumBytes,
            "SHA256SUMS.txt",
            cancellationToken).ConfigureAwait(false);
        var checksums = Encoding.UTF8.GetString(checksumsPayload);
        var expectedSha256 = ParseChecksum(checksums, expectedInstallerName);
''')
replace_once(
    "src/DevBox.Core/Services/ApplicationSelfUpdateService.cs",
    '''        try
        {
            using var response = await _httpClient.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

            VerifySha256(temporaryPath, expectedSha256);
''',
    '''        try
        {
            await ArchiveSafety.DownloadToFileAsync(
                _httpClient,
                new Uri(installerUrl),
                temporaryPath,
                MaximumInstallerDownloadBytes,
                "DevBox installer",
                cancellationToken).ConfigureAwait(false);

            VerifySha256(temporaryPath, expectedSha256);
''')
replace_once(
    "src/DevBox.Core/Services/ApplicationSelfUpdateService.cs",
    '''    internal static void VerifySha256(string path, string expectedSha256)
    {
        var expected = Convert.FromHexString(expectedSha256);
''',
    '''    internal static void VerifySha256(string path, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Trim().Length != 64)
            throw new InvalidDataException("Expected installer SHA-256 must contain 64 hexadecimal characters.");
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedSha256.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Expected installer SHA-256 is invalid.", ex);
        }
''')

# 4) Update UI handles unsupported architecture cleanly.
replace_once(
    "src/DevBox.App/ViewModels/UpdateWindowViewModel.cs",
    '''        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
''',
    '''        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or Win32Exception or PlatformNotSupportedException)
''')

# 5) Project transfer: atomic export, no self-inclusion, bounded metadata, ZIP link/ratio hardening.
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''    private const long MaximumImportBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumImportEntries = 250_000;
''',
    '''    private const long MaximumImportBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumImportEntries = 250_000;
    private const long MaximumMetadataBytes = 1024 * 1024;
    private const long CompressionRatioCheckThreshold = 1024 * 1024;
    private const double MaximumCompressionRatio = 200d;
''')
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var transferManifest = new ProjectTransferManifest
''',
    '''        var destinationDirectory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(destinationDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryDestination = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        var transferManifest = new ProjectTransferManifest
''')
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''        using (var stream = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var file in EnumerateProjectFiles(root, options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, file).Replace('\\\\', '/');
                await AddFileAsync(archive, file, $"project/{relative}", cancellationToken).ConfigureAwait(false);
            }
            if (options.IncludeDatabase)
            {
                foreach (var backup in dbFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await AddFileAsync(archive, backup, $"database/{Path.GetFileName(backup)}", cancellationToken).ConfigureAwait(false);
                }
            }
            var entry = archive.CreateEntry("transfer.json", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(JsonSerializer.Serialize(transferManifest, JsonOptions)).ConfigureAwait(false);
        }

        return new ProjectTransferResult(destination, transferManifest, new FileInfo(destination).Length);
''',
    '''        try
        {
            using (var stream = new FileStream(temporaryDestination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    Path.GetFullPath(destination),
                    Path.GetFullPath(temporaryDestination)
                };
                foreach (var file in EnumerateProjectFiles(root, options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (excludedPaths.Contains(Path.GetFullPath(file)))
                        continue;
                    var relative = Path.GetRelativePath(root, file).Replace('\\\\', '/');
                    await AddFileAsync(archive, file, $"project/{relative}", cancellationToken).ConfigureAwait(false);
                }
                if (options.IncludeDatabase)
                {
                    foreach (var backup in dbFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (excludedPaths.Contains(Path.GetFullPath(backup)))
                            continue;
                        await AddFileAsync(archive, backup, $"database/{Path.GetFileName(backup)}", cancellationToken).ConfigureAwait(false);
                    }
                }
                var entry = archive.CreateEntry("transfer.json", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync(JsonSerializer.Serialize(transferManifest, JsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryDestination, destination, overwrite: true);
            return new ProjectTransferResult(destination, transferManifest, new FileInfo(destination).Length);
        }
        finally
        {
            TryDeleteFile(temporaryDestination);
        }
''')
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''                transfer = JsonSerializer.Deserialize<ProjectTransferManifest>(File.ReadAllText(transferPath), JsonOptions)
''',
    '''                transfer = JsonSerializer.Deserialize<ProjectTransferManifest>(ReadMetadataText(transferPath, "transfer.json"), JsonOptions)
''')
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''            manifest = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
''',
    '''            manifest = JsonNode.Parse(ReadMetadataText(path, "devbox.json")) as JsonObject
''')
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''                var lockFile = JsonSerializer.Deserialize<EnvironmentLockFile>(File.ReadAllText(lockPath), JsonOptions);
''',
    '''                var lockFile = JsonSerializer.Deserialize<EnvironmentLockFile>(ReadMetadataText(lockPath, EnvironmentLockService.LockFileName), JsonOptions);
''')
# second devbox.json read in ReadManifest
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
''',
    '''            return JsonNode.Parse(ReadMetadataText(path, "devbox.json")) as JsonObject
''')
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''            total = checked(total + Math.Max(0, entry.Length));
            if (total > MaximumImportBytes)
                throw new InvalidDataException("Project archive exceeds the maximum extracted size.");
''',
    '''            const int unixFileTypeMask = 0xF000;
            const int unixSymbolicLink = 0xA000;
            var unixFileType = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
            if (unixFileType == unixSymbolicLink)
                throw new InvalidDataException($"Project archive contains a symbolic link entry: {entry.FullName}");

            total = checked(total + Math.Max(0, entry.Length));
            if (total > MaximumImportBytes)
                throw new InvalidDataException("Project archive exceeds the maximum extracted size.");
            if (entry.Length >= CompressionRatioCheckThreshold && entry.CompressedLength > 0 &&
                (double)entry.Length / entry.CompressedLength > MaximumCompressionRatio)
                throw new InvalidDataException($"Project archive entry '{entry.FullName}' has a suspicious compression ratio.");
''')
replace_once(
    "src/DevBox.Core/Services/ProjectTransferService.cs",
    '''    private static string SafeFileName(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());

    private static async Task AddFileAsync''',
    '''    private static string SafeFileName(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());

    private static string ReadMetadataText(string path, string displayName)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException($"{displayName} was not found.", path);
        if (info.Length > MaximumMetadataBytes)
            throw new InvalidDataException($"{displayName} exceeds the {MaximumMetadataBytes} byte metadata limit.");
        return File.ReadAllText(path);
    }

    private static async Task AddFileAsync''')

# 6) DatabaseManager: atomic backup, reliable clone rollback, terminate child processes on cancellation/failure.
replace_once(
    "src/DevBox.Core/Services/DatabaseManager.cs",
    '''        var backupPath = ResolveBackupPath(safeName, destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

        try
        {
            await WithClientConfigAsync(options, configPath => RunAsync(
                _mysqlDumpExecutable,
                configPath,
                ["--single-transaction", "--routines", "--events", "--triggers", "--set-gtid-purged=OFF", safeName],
                backupPath,
                null,
                cancellationToken)).ConfigureAwait(false);
            return backupPath;
        }
        catch
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            throw;
        }
''',
    '''        var backupPath = ResolveBackupPath(safeName, destinationPath);
        var backupDirectory = Path.GetDirectoryName(backupPath)!;
        Directory.CreateDirectory(backupDirectory);
        var temporaryBackupPath = Path.Combine(
            backupDirectory,
            $".{Path.GetFileName(backupPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await WithClientConfigAsync(options, configPath => RunAsync(
                _mysqlDumpExecutable,
                configPath,
                ["--single-transaction", "--routines", "--events", "--triggers", "--set-gtid-purged=OFF", safeName],
                temporaryBackupPath,
                null,
                cancellationToken)).ConfigureAwait(false);
            File.Move(temporaryBackupPath, backupPath, overwrite: true);
            return backupPath;
        }
        finally
        {
            TryDeleteFile(temporaryBackupPath);
        }
''')
replace_once(
    "src/DevBox.Core/Services/DatabaseManager.cs",
    '''        catch
        {
            try
            {
                await DropDatabaseAsync(destination, options, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
''',
    '''        catch (Exception original)
        {
            try
            {
                using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await DropDatabaseAsync(destination, options, rollbackTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    $"Database clone failed and rollback of '{destination}' also failed.",
                    original,
                    rollbackError);
            }
            throw;
        }
''')
# Wrap RunAsync execution body in cancellation/error cleanup.
replace_once(
    "src/DevBox.Core/Services/DatabaseManager.cs",
    '''        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string>? outputTask = standardOutputPath is null
            ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : null;

        if (standardOutputPath is not null)
        {
            await using var output = new FileStream(standardOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var outputCopyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), outputCopyTask).ConfigureAwait(false);
        }
        else if (standardInputPath is not null)
        {
            await using var input = new FileStream(standardInputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        var error = await errorTask.ConfigureAwait(false);
        var outputText = outputTask is null ? string.Empty : await outputTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"{Path.GetFileName(executable)} exited with code {process.ExitCode}."
                : error.Trim());
        }

        return new ProcessResult(outputText);
''',
    '''        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string>? outputTask = standardOutputPath is null
            ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : null;

        try
        {
            if (standardOutputPath is not null)
            {
                await using var output = new FileStream(standardOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var outputCopyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
                await Task.WhenAll(process.WaitForExitAsync(cancellationToken), outputCopyTask).ConfigureAwait(false);
            }
            else if (standardInputPath is not null)
            {
                await using var input = new FileStream(standardInputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            var error = await errorTask.ConfigureAwait(false);
            var outputText = outputTask is null ? string.Empty : await outputTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"{Path.GetFileName(executable)} exited with code {process.ExitCode}."
                    : error.Trim());
            }

            return new ProcessResult(outputText);
        }
        catch
        {
            TryKill(process);
            throw;
        }
''')
replace_once(
    "src/DevBox.Core/Services/DatabaseManager.cs",
    '''    private static void EnsureExecutable(string path)
''',
    '''    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void EnsureExecutable(string path)
''')

# 7) External command helpers: kill child process on cancellation.
replace_once(
    "src/DevBox.Core/Services/DeveloperToolsService.cs",
    '''        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
''',
    '''        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
''')
replace_once(
    "src/DevBox.Core/Services/DeveloperToolsService.cs",
    '''    internal static ProcessStartInfo BuildStartInfo(string executable, IReadOnlyList<string> arguments)
''',
    '''    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, IReadOnlyList<string> arguments)
''')

replace_once(
    "src/DevBox.Core/Services/PhpExtensionInspector.cs",
    '''        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new PhpExtensionCheckResult(true, Array.Empty<string>(), required, "PHP extension check timed out.");
        }

        var output = await outputTask.ConfigureAwait(false);
''',
    '''        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new PhpExtensionCheckResult(true, Array.Empty<string>(), required, "PHP extension check timed out.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
''')
replace_once(
    "src/DevBox.Core/Services/PhpExtensionInspector.cs",
    '''        catch (InvalidOperationException)
        {
        }
''',
    '''        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
''')

replace_once(
    "src/DevBox.Core/Services/ConfigurationFileService.cs",
    '''        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return (false, "Configuration validator exceeded the 15 second timeout.");
        }
        var output = (await stdout.ConfigureAwait(false) + Environment.NewLine + await stderr.ConfigureAwait(false)).Trim();
''',
    '''        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return (false, "Configuration validator exceeded the 15 second timeout.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        var output = (await stdout.ConfigureAwait(false) + Environment.NewLine + await stderr.ConfigureAwait(false)).Trim();
''')

# 8) ProcessManager: distinguish cancellation from graceful-stop timeout and kill init helpers on cancellation.
replace_once(
    "src/DevBox.Core/Services/ProcessManager.cs",
    '''                managed.Process.Kill(entireProcessTree: true);
                await managed.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
''',
    '''                managed.Process.Kill(entireProcessTree: true);
                await managed.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
''')
replace_once(
    "src/DevBox.Core/Services/ProcessManager.cs",
    '''        var outputTask = stopProcess.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = stopProcess.StandardError.ReadToEndAsync(cancellationToken);
        var exited = await WaitForExitAsync(stopProcess, timeout, cancellationToken).ConfigureAwait(false);
        if (!exited)
        {
            try
            {
                stopProcess.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            return false;
        }

        _ = await outputTask.ConfigureAwait(false);
''',
    '''        var outputTask = stopProcess.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = stopProcess.StandardError.ReadToEndAsync(cancellationToken);
        bool exited;
        try
        {
            exited = await WaitForExitAsync(stopProcess, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminateStartedProcess(stopProcess);
            throw;
        }
        if (!exited)
        {
            TryTerminateStartedProcess(stopProcess);
            _ = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            return false;
        }

        _ = await outputTask.ConfigureAwait(false);
''')
replace_once(
    "src/DevBox.Core/Services/ProcessManager.cs",
    '''    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return true;
        }

        var exitTask = process.WaitForExitAsync(cancellationToken);
        var delayTask = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false);
        return completed == exitTask && process.HasExited;
    }
''',
    '''    internal static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (process.HasExited)
            return true;

        var exitTask = process.WaitForExitAsync(cancellationToken);
        var delayTask = Task.Delay(timeout, CancellationToken.None);
        var completed = await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (completed != exitTask)
            return false;

        await exitTask.ConfigureAwait(false);
        return process.HasExited;
    }
''')
replace_once(
    "src/DevBox.Core/Services/ProcessManager.cs",
    '''        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
''',
    '''        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminateStartedProcess(process);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
''')

# 9) Regression tests for export atomicity/self-inclusion, update metadata and process cancellation.
Path("tests/DevBox.Tests/BugAuditRegressionTests.cs").write_text(r'''using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class BugAuditRegressionTests
{
    [Fact]
    public async Task ProjectExport_DestinationInsideProject_DoesNotIncludeItself()
    {
        var root = TemporaryRoot();
        try
        {
            var project = CreateProject(root, "self-export");
            File.WriteAllText(Path.Combine(project, "index.php"), "<?php echo 'ok';");
            var destination = Path.Combine(project, "export.devbox-project.zip");
            var service = new ProjectTransferService(root);

            var result = await service.ExportAsync(project, destinationPath: destination);

            Assert.Equal(destination, result.ArchivePath);
            using var archive = ZipFile.OpenRead(destination);
            Assert.Contains(archive.Entries, entry => entry.FullName == "project/index.php");
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("export.devbox-project.zip", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ProjectExport_PreCancelledOperation_PreservesExistingDestination()
    {
        var root = TemporaryRoot();
        try
        {
            var project = CreateProject(root, "cancel-export");
            var destination = Path.Combine(project, "existing.devbox-project.zip");
            await File.WriteAllTextAsync(destination, "existing-good-file");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var service = new ProjectTransferService(root);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ExportAsync(project, destinationPath: destination, cancellationToken: cancellation.Token));

            Assert.Equal("existing-good-file", await File.ReadAllTextAsync(destination));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ProjectImport_RejectsOversizedTransferMetadata()
    {
        var root = TemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "oversized.devbox-project.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var transfer = archive.CreateEntry("transfer.json", CompressionLevel.NoCompression);
                await using (var stream = transfer.Open())
                await using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: false))
                    await writer.WriteAsync(new string(' ', 1024 * 1024 + 1));

                var projectManifest = archive.CreateEntry("project/devbox.json");
                await using var manifestStream = projectManifest.Open();
                await using var manifestWriter = new StreamWriter(manifestStream);
                await manifestWriter.WriteAsync("{\"Name\":\"x\",\"Domain\":\"x.test\",\"DatabaseEngine\":\"none\",\"Https\":false}");
            }

            var service = new ProjectTransferService(root);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(archivePath));
            Assert.Contains("metadata limit", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ApplicationUpdate_MalformedJson_IsReportedAsInvalidData()
    {
        using var http = new HttpClient(new StaticHandler("{"u8.ToArray()));
        using var service = new ApplicationUpdateService(new Version(1, 0, 0), http);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task SelfUpdate_MalformedJson_IsReportedAsInvalidData()
    {
        var root = TemporaryRoot();
        try
        {
            using var http = new HttpClient(new StaticHandler("{"u8.ToArray()));
            using var service = new ApplicationSelfUpdateService(root, http);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadLatestInstallerAsync(new Version(0, 0, 0)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SelfUpdate_RejectsOversizedReleaseMetadataBeforeReadingBody()
    {
        var root = TemporaryRoot();
        try
        {
            using var http = new HttpClient(new StaticHandler("{}"u8.ToArray(), declaredLength: 10L * 1024 * 1024));
            using var service = new ApplicationSelfUpdateService(root, http);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadLatestInstallerAsync(new Version(0, 0, 0)));
            Assert.Contains("too large", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ProcessWait_CancellationIsNotTreatedAsTimeout()
    {
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }
        }) ?? throw new InvalidOperationException("Unable to start cancellation fixture process.");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ProcessManager.WaitForExitAsync(process, TimeSpan.FromSeconds(30), cancellation.Token));
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
    }

    private static string CreateProject(string root, string name)
    {
        var project = Path.Combine(root, "www", name);
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, ProjectWorkspaceService.ManifestFileName),
            JsonSerializer.Serialize(new { Name = name, Domain = $"{name}.test", DatabaseEngine = "none", Https = false }));
        return project;
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-bug-audit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "www"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root))
            return;
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class StaticHandler(byte[] payload, long? declaredLength = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(payload);
            if (declaredLength.HasValue)
                content.Headers.ContentLength = declaredLength.Value;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
''', encoding="utf-8")

print("full bug audit patch applied")
