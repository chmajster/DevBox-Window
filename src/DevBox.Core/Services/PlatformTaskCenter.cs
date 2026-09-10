using System.Collections.Concurrent;
using System.Text.Json;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class PlatformTaskCenter : IDisposable
{
    private readonly string _historyPath;
    private readonly ConcurrentDictionary<Guid, TaskEntry> _entries = new();
    private readonly SemaphoreSlim _parallelism;
    private readonly object _historySync = new();
    private bool _disposed;

    public PlatformTaskCenter(string rootPath, int maxParallelism = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (maxParallelism is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(maxParallelism), "Task Center parallelism must be between 1 and 8.");
        _historyPath = Path.Combine(Path.GetFullPath(rootPath), "logs", "task-center-history.json");
        _parallelism = new SemaphoreSlim(maxParallelism, maxParallelism);
        foreach (var snapshot in LoadHistory())
            _entries[snapshot.Id] = new TaskEntry(snapshot, null);
    }

    public event EventHandler<PlatformTaskSnapshot>? TaskChanged;

    public IReadOnlyList<PlatformTaskSnapshot> GetTasks(int limit = 200)
    {
        ThrowIfDisposed();
        return _entries.Values
            .Select(entry => ReadSnapshot(entry))
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToArray();
    }

    public PlatformTaskSnapshot? GetTask(Guid id)
    {
        ThrowIfDisposed();
        return _entries.TryGetValue(id, out var entry) ? ReadSnapshot(entry) : null;
    }

    public Guid Enqueue(
        string name,
        Func<IProgress<(double Progress, string? Message)>, CancellationToken, Task> operation)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        if (name.Length > 160)
            throw new ArgumentException("Task name is too long.", nameof(name));

        var id = Guid.NewGuid();
        var cancellation = new CancellationTokenSource();
        var snapshot = new PlatformTaskSnapshot(
            id,
            name.Trim(),
            PlatformTaskState.Queued,
            0,
            null,
            DateTimeOffset.UtcNow,
            null,
            null,
            null);
        var entry = new TaskEntry(snapshot, cancellation);
        _entries[id] = entry;
        Publish(entry, persist: true);
        entry.Execution = ExecuteAsync(entry, operation);
        return id;
    }

    public bool Cancel(Guid id)
    {
        ThrowIfDisposed();
        if (!_entries.TryGetValue(id, out var entry) || entry.Cancellation is null)
            return false;
        lock (entry.Sync)
        {
            if (IsTerminal(entry.Snapshot.State))
                return false;
        }
        entry.Cancellation.Cancel();
        return true;
    }

    public async Task<PlatformTaskSnapshot> WaitAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_entries.TryGetValue(id, out var entry))
            throw new KeyNotFoundException($"Task '{id}' was not found.");
        if (entry.Execution is not null)
            await entry.Execution.WaitAsync(cancellationToken).ConfigureAwait(false);
        return ReadSnapshot(entry);
    }

    public void ClearCompleted()
    {
        ThrowIfDisposed();
        foreach (var pair in _entries.ToArray())
        {
            if (!IsTerminal(ReadSnapshot(pair.Value).State))
                continue;
            if (_entries.TryRemove(pair.Key, out var removed))
                removed.Cancellation?.Dispose();
        }
        PersistHistory();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var entry in _entries.Values)
            entry.Cancellation?.Cancel();
        _parallelism.Dispose();
    }

    private async Task ExecuteAsync(
        TaskEntry entry,
        Func<IProgress<(double Progress, string? Message)>, CancellationToken, Task> operation)
    {
        var token = entry.Cancellation!.Token;
        var acquired = false;
        try
        {
            await _parallelism.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
        }
        catch (OperationCanceledException)
        {
            Update(entry, PlatformTaskState.Cancelled, ReadSnapshot(entry).Progress, "Cancelled before execution.", null, finished: true);
            return;
        }

        try
        {
            Update(entry, PlatformTaskState.Running, Math.Max(0, ReadSnapshot(entry).Progress), "Running", null, started: true);
            var progress = new InlineProgress<(double Progress, string? Message)>(value =>
            {
                var current = ReadSnapshot(entry);
                if (IsTerminal(current.State))
                    return;
                var normalized = double.IsFinite(value.Progress) ? Math.Clamp(value.Progress, 0, 100) : current.Progress;
                Update(entry, PlatformTaskState.Running, normalized, value.Message, null);
            });
            await operation(progress, token).ConfigureAwait(false);
            Update(entry, PlatformTaskState.Completed, 100, "Completed", null, finished: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Update(entry, PlatformTaskState.Cancelled, ReadSnapshot(entry).Progress, "Cancelled", null, finished: true);
        }
        catch (Exception ex)
        {
            Update(entry, PlatformTaskState.Failed, ReadSnapshot(entry).Progress, "Failed", SanitizeError(ex), finished: true);
        }
        finally
        {
            if (acquired)
                _parallelism.Release();
        }
    }

    private void Update(
        TaskEntry entry,
        PlatformTaskState state,
        double progress,
        string? message,
        string? error,
        bool started = false,
        bool finished = false)
    {
        PlatformTaskSnapshot published;
        lock (entry.Sync)
        {
            var current = entry.Snapshot;
            if (IsTerminal(current.State) && !IsTerminal(state))
                return;
            if (IsTerminal(current.State) && IsTerminal(state) && current.State != state)
                return;

            entry.Snapshot = current with
            {
                State = state,
                Progress = Math.Clamp(progress, 0, 100),
                Message = message,
                Error = error,
                StartedAtUtc = started && current.StartedAtUtc is null ? DateTimeOffset.UtcNow : current.StartedAtUtc,
                FinishedAtUtc = finished ? DateTimeOffset.UtcNow : current.FinishedAtUtc
            };
            published = entry.Snapshot;
        }
        Publish(published, persist: finished);
    }

    private void Publish(TaskEntry entry, bool persist) => Publish(ReadSnapshot(entry), persist);

    private void Publish(PlatformTaskSnapshot snapshot, bool persist)
    {
        TaskChanged?.Invoke(this, snapshot);
        if (persist)
            PersistHistory();
    }

    private IReadOnlyList<PlatformTaskSnapshot> LoadHistory()
    {
        if (!File.Exists(_historyPath))
            return Array.Empty<PlatformTaskSnapshot>();
        try
        {
            var items = JsonSerializer.Deserialize<List<PlatformTaskSnapshot>>(File.ReadAllText(_historyPath), JsonOptions)
                ?? new List<PlatformTaskSnapshot>();
            return items.Select(item => item.State is PlatformTaskState.Queued or PlatformTaskState.Running
                    ? item with
                    {
                        State = PlatformTaskState.Failed,
                        Error = "DevBox exited before this task finished.",
                        Message = "Interrupted",
                        FinishedAtUtc = DateTimeOffset.UtcNow
                    }
                    : item)
                .OrderByDescending(item => item.CreatedAtUtc)
                .Take(500)
                .ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Task Center history contains invalid JSON.", ex);
        }
    }

    private void PersistHistory()
    {
        lock (_historySync)
        {
            var values = _entries.Values
                .Select(ReadSnapshot)
                .OrderByDescending(item => item.CreatedAtUtc)
                .Take(500)
                .ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            var temp = _historyPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(values, JsonOptions));
                if (File.Exists(_historyPath))
                    File.Replace(temp, _historyPath, null);
                else
                    File.Move(temp, _historyPath);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
    }

    private static PlatformTaskSnapshot ReadSnapshot(TaskEntry entry)
    {
        lock (entry.Sync)
            return entry.Snapshot;
    }

    private static bool IsTerminal(PlatformTaskState state) =>
        state is PlatformTaskState.Completed or PlatformTaskState.Failed or PlatformTaskState.Cancelled;

    private static string SanitizeError(Exception ex)
    {
        var message = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 2000 ? message : message[..2000];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        private readonly Action<T> _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        public void Report(T value) => _callback(value);
    }

    private sealed class TaskEntry
    {
        public TaskEntry(PlatformTaskSnapshot snapshot, CancellationTokenSource? cancellation)
        {
            Snapshot = snapshot;
            Cancellation = cancellation;
        }

        public object Sync { get; } = new();
        public PlatformTaskSnapshot Snapshot { get; set; }
        public CancellationTokenSource? Cancellation { get; }
        public Task? Execution { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
