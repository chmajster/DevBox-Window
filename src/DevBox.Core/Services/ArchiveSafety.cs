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
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        progress?.Report(0);
        using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength.HasValue && declaredLength.Value > maximumBytes)
        {
            throw new InvalidDataException($"{packageName} download is too large ({declaredLength.Value} bytes; limit {maximumBytes} bytes).");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await CopyToWithLimitAsync(source, target, maximumBytes, declaredLength, packageName, cancellationToken, progress).ConfigureAwait(false);
        progress?.Report(100);
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
        {
            throw new ArgumentOutOfRangeException(nameof(maximumUncompressedBytes));
        }
        if (maximumEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        var destinationRoot = Path.GetFullPath(destinationPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > maximumEntries)
        {
            throw new InvalidDataException($"{packageName} archive contains too many entries ({archive.Entries.Count}; limit {maximumEntries}).");
        }

        long totalUncompressedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateEntryPath(entry, destinationPath, destinationRoot, packageName);
            RejectSymbolicLink(entry, packageName);

            try
            {
                totalUncompressedBytes = checked(totalUncompressedBytes + entry.Length);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException($"{packageName} archive declares an invalid uncompressed size.", ex);
            }

            if (totalUncompressedBytes > maximumUncompressedBytes)
            {
                throw new InvalidDataException($"{packageName} archive expands beyond the allowed size ({maximumUncompressedBytes} bytes).");
            }

            if (entry.Length >= CompressionRatioCheckThreshold && entry.CompressedLength > 0)
            {
                var ratio = (double)entry.Length / entry.CompressedLength;
                if (ratio > MaximumCompressionRatio)
                {
                    throw new InvalidDataException($"{packageName} archive entry '{entry.FullName}' has a suspicious compression ratio ({ratio:F1}:1).");
                }
            }
        }

        Directory.CreateDirectory(destinationPath);
        foreach (var entry in archive.Entries)
        {
            var outputPath = ResolveOutputPath(entry, destinationPath);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            entry.ExtractToFile(outputPath, overwrite: true);
        }
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
            {
                return;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException($"{packageName} download exceeded the allowed size ({maximumBytes} bytes).");
            }

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
        var normalizedEntry = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedEntry) || normalizedEntry.Contains(':'))
        {
            throw new InvalidDataException($"Unsafe ZIP entry detected in {packageName}: {entry.FullName}");
        }

        var outputPath = ResolveOutputPath(entry, destinationPath);
        if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsafe ZIP entry detected in {packageName}: {entry.FullName}");
        }
    }

    private static string ResolveOutputPath(ZipArchiveEntry entry, string destinationPath) =>
        Path.GetFullPath(Path.Combine(destinationPath, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));

    private static void RejectSymbolicLink(ZipArchiveEntry entry, string packageName)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixFileType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (unixFileType == UnixSymbolicLink)
        {
            throw new InvalidDataException($"{packageName} archive contains a symbolic link entry: {entry.FullName}");
        }
    }
}
