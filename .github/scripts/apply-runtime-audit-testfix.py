from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:120]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


replace_once(
    "src/DevBox.Core/Services/ProcessManager.cs",
    "    private static readonly TimeSpan ProcessOnlyStartupDelay = TimeSpan.FromMilliseconds(250);",
    "    private static readonly TimeSpan ProcessOnlyStartupDelay = TimeSpan.FromSeconds(1);")

replace_once(
    "tests/DevBox.Tests/ProcessManagerTests.cs",
    '''        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");\n        var definition = new ServiceDefinition(\n            "early-exit", "Early exit", executable,\n            new[] { "-NoProfile", "-NonInteractive", "-Command", "exit 7" },\n''',
    '''        var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");\n        var definition = new ServiceDefinition(\n            "early-exit", "Early exit", executable,\n            new[] { "/d", "/c", "exit 7" },\n''')
