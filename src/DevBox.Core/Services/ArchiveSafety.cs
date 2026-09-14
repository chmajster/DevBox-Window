using System.IO.Compression;

namespace DevBox.Core.Services;

internal static class ArchiveSafety
{
    private const long CompressionRatioCheckThreshold = 1024 * 1024;
    private const double MaximumCompressionRatio = 200d;

    public static async Task DownloadToFileAsync(
        HttpClient httpClient,
        Uri uri,
        string destination,
        long maximumBytes,
        string packageName,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        progress?.Report(0);
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength.HasValue && declaredLength.Value > maximumBytes)
            throw new InvalidDataException($"{packageName} download is too large ({declaredLength.Value} bytes; limit {maximumBytes} bytes).");

        var destinationCreated = false;
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            destinationCreated = true;
            await CopyToWithLimitAsync(source, target, maximumBytes, declaredLength, packageName, cancellationToken, progress).ConfigureAwait(false);
            progress?.Report(100);
        }
        catch
        {
            if (destinationCreated)
                TryDeletePartialDownload(destination);
            throw;
        }
    }

    public static async Task<byte[]> ReadContentBytesWithLimitAsync(
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
        await CopyToWithLimitAsync(source, target, maximumBytes, declaredLength, contentName, cancellationToken, progress: null).ConfigureAwait(false);
        return target.ToArray();
    }

    public static void ExtractZipSafely(
        string archivePath,
        string destinationPath,
        long maximumUncompressedBytes,
        int maximumEntries,
        string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        if (maximumUncompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumUncompressedBytes));
        if (maximumEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));

        var destinationRoot = Path.GetFullPath(destinationPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > maximumEntries)
            throw new InvalidDataException($"{packageName} archive contains too many entries ({archive.Entries.Count}; limit {maximumEntries}).");

        long totalUncompressedBytes = 0;
        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            ValidateEntryPath(entry, destinationPath, destinationRoot, packageName);
            RejectSymbolicLink(entry, packageName);
            var outputPath = ResolveOutputPath(entry, destinationPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!outputPaths.Add(outputPath))
                throw new InvalidDataException($"{packageName} archive contains multiple entries targeting the same output path: {entry.FullName}");

            try
            {
                totalUncompressedBytes = checked(totalUncompressedBytes + entry.Length);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException($"{packageName} archive declares an invalid uncompressed size.", ex);
            }

            if (totalUncompressedBytes > maximumUncompressedBytes)
                throw new InvalidDataException($"{packageName} archive expands beyond the allowed size ({maximumUncompressedBytes} bytes).");

            if (entry.Length >= CompressionRatioCheckThreshold && entry.CompressedLength > 0)
            {
                var ratio = (double)entry.Length / entry.CompressedLength;
                if (ratio > MaximumCompressionRatio)
                    throw new InvalidDataException($"{packageName} archive entry '{entry.FullName}' has a suspicious compression ratio ({ratio:F1}:1).");
            }
        }

        EnsureSafeOutputPath(destinationPath, destinationPath, packageName, allowRoot: true);
        Directory.CreateDirectory(destinationPath);
        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var outputPath = ResolveOutputPath(entry, destinationPath);
            // Validate the exact normalized path used below, including the trailing
            // directory separator so sibling directories cannot pass a prefix check.
            if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe ZIP output path detected in {packageName}: {entry.FullName}");
            EnsureSafeOutputPath(destinationPath, outputPath, packageName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using (var source = entry.Open())
            using (var target = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long entryBytes = 0;
                uint crc32 = uint.MaxValue;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // ZIP size fields are untrusted: enforce limits before every write.
                    if (read > maximumUncompressedBytes - extractedBytes || read > entry.Length - entryBytes)
                        throw new InvalidDataException($"{packageName} archive expands beyond its declared or allowed size.");
                    crc32 = UpdateCrc32(crc32, buffer.AsSpan(0, read));
                    target.Write(buffer, 0, read);
                    extractedBytes += read;
                    entryBytes += read;
                }
                if (entryBytes != entry.Length)
                    throw new InvalidDataException($"{packageName} archive contains a truncated entry: {entry.FullName}");
                // Some framework versions cap reads at the declared size. CRC32
                // also detects silently truncated or otherwise corrupted payloads.
                if (~crc32 != entry.Crc32)
                    throw new InvalidDataException($"{packageName} archive entry failed CRC32 validation: {entry.FullName}");
            }
            File.SetLastWriteTime(outputPath, entry.LastWriteTime.DateTime);
        }
    }

    private static readonly uint[] Crc32Table = CreateCrc32Table();

    private static uint[] CreateCrc32Table()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320u : 0u);
            table[index] = value;
        }
        return table;
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            crc = Crc32Table[(byte)(crc ^ value)] ^ (crc >> 8);
        return crc;
    }

    private static async Task CopyToWithLimitAsync(
        Stream source,
        Stream target,
        long maximumBytes,
        long? declaredLength,
        string packageName,
        CancellationToken cancellationToken,
        IProgress<int>? progress)
    {
        var buffer = new byte[81920];
        long total = 0;
        var lastProgress = -1;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return;

            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException($"{packageName} download exceeded the allowed size ({maximumBytes} bytes).");

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            if (progress is not null && declaredLength is > 0)
            {
                var percentage = (int)Math.Min(100L, total * 100L / declaredLength.Value);
                if (percentage != lastProgress)
                {
                    lastProgress = percentage;
                    progress.Report(percentage);
                }
            }
        }
    }

    private static void ValidateEntryPath(ZipArchiveEntry entry, string destinationPath, string destinationRoot, string packageName)
    {
        var normalizedEntry = entry.FullName.Replace('\\', '/');
        if (Path.IsPathRooted(normalizedEntry) || normalizedEntry.StartsWith('/') || normalizedEntry.Contains(':'))
        {
            throw new InvalidDataException($"Unsafe ZIP entry detected in {packageName}: {entry.FullName}");
        }

        foreach (var segment in normalizedEntry.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(character => character < 32 || "<>\"|?*".Contains(character)) || IsWindowsDeviceName(segment))
                throw new InvalidDataException($"Unsafe Windows ZIP entry detected in {packageName}: {entry.FullName}");
        }

        var outputPath = ResolveOutputPath(entry, destinationPath);
        if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsafe ZIP entry detected in {packageName}: {entry.FullName}");
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

    private static string ResolveOutputPath(ZipArchiveEntry entry, string destinationPath) =>
        Path.GetFullPath(Path.Combine(destinationPath, entry.FullName.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));

    private static void RejectSymbolicLink(ZipArchiveEntry entry, string packageName)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixFileType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (unixFileType == UnixSymbolicLink)
            throw new InvalidDataException($"{packageName} archive contains a symbolic link entry: {entry.FullName}");
    }

    private static void TryDeletePartialDownload(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
