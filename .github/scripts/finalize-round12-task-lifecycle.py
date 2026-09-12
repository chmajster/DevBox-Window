from pathlib import Path

p = Path('src/DevBox.Core/Services/PlatformTaskCenter.cs')
text = p.read_text(encoding='utf-8')
old = '''        var entry = new TaskEntry(snapshot, cancellation);\n        _entries[id] = entry;\n        Publish(entry, persist: true);\n        entry.Execution = ExecuteAsync(entry, operation);\n        return id;\n'''
new = '''        var entry = new TaskEntry(snapshot, cancellation);\n        var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);\n        _entries[id] = entry;\n        entry.Execution = ExecuteAsync(entry, operation, startSignal.Task);\n        try\n        {\n            Publish(entry, persist: true);\n        }\n        finally\n        {\n            startSignal.TrySetResult();\n        }\n        return id;\n'''
if text.count(old) != 1:
    raise RuntimeError(f'Enqueue anchor count={text.count(old)}')
text = text.replace(old, new, 1)
old = '''    private async Task ExecuteAsync(\n        TaskEntry entry,\n        Func<IProgress<(double Progress, string? Message)>, CancellationToken, Task> operation)\n    {\n        try\n        {\n            var token = entry.Cancellation!.Token;\n'''
new = '''    private async Task ExecuteAsync(\n        TaskEntry entry,\n        Func<IProgress<(double Progress, string? Message)>, CancellationToken, Task> operation,\n        Task startSignal)\n    {\n        try\n        {\n            await startSignal.ConfigureAwait(false);\n            var token = entry.Cancellation!.Token;\n'''
if text.count(old) != 1:
    raise RuntimeError(f'Execute anchor count={text.count(old)}')
text = text.replace(old, new, 1)
p.write_text(text, encoding='utf-8')

# Record the lifecycle race in the existing changelog entry rather than adding noise.
p = Path('CHANGELOG.md')
text = p.read_text(encoding='utf-8')
old = '- Task Center `WaitAsync` now waits on a completion handle created before the initial queued notification, closing a race where subscribers could observe completion before execution had even been assigned.\n'
new = '- Task Center creates and assigns its execution/completion handles before the initial queued notification, closing races where subscribers could observe premature completion or dispose synchronization before execution was registered.\n'
if text.count(old) != 1:
    raise RuntimeError(f'changelog anchor count={text.count(old)}')
p.write_text(text.replace(old, new, 1), encoding='utf-8')
