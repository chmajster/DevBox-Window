namespace DevBox.Core.Models;

public sealed record SupportBundleResult(
    string ArchivePath,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc,
    int IncludedFileCount,
    int IncludedLogCount,
    IReadOnlyList<string> IncludedEntries);
