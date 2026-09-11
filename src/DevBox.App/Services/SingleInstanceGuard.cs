using System.Security.Cryptography;
using System.Text;

namespace DevBox.App.Services;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static SingleInstanceGuard? TryAcquire(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var mutex = new Mutex(initiallyOwned: false, BuildMutexName(rootPath), out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex);
    }

    internal static string BuildMutexName(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var normalized = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"Local\\DevBoxWindows-{Convert.ToHexString(hash.AsSpan(0, 16))}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _mutex.Dispose();
    }
}
