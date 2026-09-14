namespace DevBox.Core.Services;

internal static class PathSafety
{
    public static string EnsureUnderRootWithoutReparsePoints(
        string rootPath,
        string candidatePath,
        string message,
        bool allowRoot = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var fullRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (IsReparsePoint(fullRoot))
            throw new InvalidOperationException($"{message} Protected root is a reparse point: {fullRoot}");

        if (fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (!allowRoot)
                throw new InvalidOperationException(message);
            return fullCandidate;
        }

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(message);

        var relative = Path.GetRelativePath(fullRoot, fullCandidate);
        var current = fullRoot;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
                throw new InvalidOperationException($"{message} Reparse-point path segment is not allowed: {current}");
        }

        return fullCandidate;
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
}
