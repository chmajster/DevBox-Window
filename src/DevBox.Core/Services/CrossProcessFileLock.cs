namespace DevBox.Core.Services;

internal static class CrossProcessFileLock
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static FileStream Acquire(string path, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            try
            {
                return new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"Timed out waiting for DevBox lock '{fullPath}'. Another DevBox process may still be using this resource.", ex);
            }
        }
    }

    public static async Task<FileStream> AcquireAsync(
        string path,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new TimeoutException($"Timed out waiting for DevBox lock '{fullPath}'. Another DevBox process may still be using this resource.", ex);
            }
        }
    }
}
