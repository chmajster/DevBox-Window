namespace DevBox.Core.Services;

public sealed class LogReader
{
    private readonly string _logsRoot;

    public LogReader(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _logsRoot = Path.GetFullPath(Path.Combine(rootPath, "logs"));
    }

    public IReadOnlyList<string> GetAvailableLogs()
    {
        if (!Directory.Exists(_logsRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFiles(_logsRoot, "*.log", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> ReadTail(string fileName, int maxLines = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (maxLines is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines), "maxLines must be between 1 and 10000.");
        }

        var path = ResolveSafePath(fileName);
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        var queue = new Queue<string>(maxLines);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (queue.Count == maxLines)
            {
                queue.Dequeue();
            }
            queue.Enqueue(line);
        }
        return queue.ToArray();
    }

    public void Clear(string fileName)
    {
        var path = ResolveSafePath(fileName);
        Directory.CreateDirectory(_logsRoot);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    }

    private string ResolveSafePath(string fileName)
    {
        if (!fileName.Equals(Path.GetFileName(fileName), StringComparison.Ordinal) || !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only .log files directly inside the DevBox logs directory are allowed.", nameof(fileName));
        }

        var logsRoot = _logsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(_logsRoot, fileName));
        if (!path.StartsWith(logsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Log path escaped the DevBox logs directory.");
        }
        return path;
    }
}
