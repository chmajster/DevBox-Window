from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:180]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


# Close the narrow cancellation race between graceful timeout and the irreversible
# process-tree kill. A cancellation requested after WaitForExitAsync returns false
# must still leave the managed service running.
replace_once(
    "src/DevBox.Core/Services/ProcessManager.cs",
    '''                if (!managed.Process.HasExited)\n                {\n                    AppendLog(managed, "APP", "Graceful shutdown was unavailable or timed out; killing managed process tree.");\n                    managed.Process.Kill(entireProcessTree: true);\n''',
    '''                if (!managed.Process.HasExited)\n                {\n                    cancellationToken.ThrowIfCancellationRequested();\n                    AppendLog(managed, "APP", "Graceful shutdown was unavailable or timed out; killing managed process tree.");\n                    managed.Process.Kill(entireProcessTree: true);\n''')

# The regression used a 500ms timeout versus 200ms cancellation, which can invert on
# a heavily loaded CI runner because timer callbacks are scheduling-dependent. Keep a
# wide separation so the test exercises cancellation rather than scheduler jitter.
replace_once(
    "tests/DevBox.Tests/ProcessManagerTests.cs",
    '''                StopArguments: new[] { "/d", "/c", "exit 0" },\n                ShutdownTimeout: TimeSpan.FromMilliseconds(500));\n''',
    '''                StopArguments: new[] { "/d", "/c", "exit 0" },\n                ShutdownTimeout: TimeSpan.FromSeconds(5));\n''')
replace_once(
    "tests/DevBox.Tests/ProcessManagerTests.cs",
    '''            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));\n''',
    '''            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));\n''')
