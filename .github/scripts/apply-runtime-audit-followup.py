from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:120]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


app = Path("src/DevBox.App/App.xaml.cs")
text = app.read_text(encoding="utf-8")
count = text.count("MessageBox.Show(")
if count != 4:
    raise RuntimeError(f"App.xaml.cs: expected 4 unqualified MessageBox.Show calls, got {count}")
app.write_text(text.replace("MessageBox.Show(", "System.Windows.MessageBox.Show("), encoding="utf-8")

replace_once(
    "src/DevBox.Core/Services/DatabaseRuntimeService.cs",
    '''        if (version.Length > 64 || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.Contains(Path.DirectorySeparatorChar) || version.Contains(Path.AltDirectorySeparatorChar))\n            throw new ArgumentException("Database runtime version contains unsupported characters.", nameof(version));\n''',
    '''        if (version.Length > 64 || version is "." or ".." || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.Contains(Path.DirectorySeparatorChar) || version.Contains(Path.AltDirectorySeparatorChar))\n            throw new ArgumentException("Database runtime version contains unsupported characters.", nameof(version));\n''')

test = Path("tests/DevBox.Tests/StartupRuntimeAuditTests.cs")
text = test.read_text(encoding="utf-8")
marker = '''    [Fact]\n    public void RuntimeCatalog_RejectsArchiveRootTraversal()\n'''
insert = '''    [Fact]\n    public void DatabaseRuntime_RejectsParentDirectoryAsVersion()\n    {\n        var root = Path.Combine(Path.GetTempPath(), "devbox-database-runtime", Guid.NewGuid().ToString("N"));\n        Directory.CreateDirectory(root);\n        try\n        {\n            using var service = new DatabaseRuntimeService(root);\n            Assert.Throws<ArgumentException>(() => service.Register("mysql", ".."));\n        }\n        finally\n        {\n            if (Directory.Exists(root)) Directory.Delete(root, true);\n        }\n    }\n\n'''
if marker not in text or "DatabaseRuntime_RejectsParentDirectoryAsVersion" in text:
    raise RuntimeError("StartupRuntimeAuditTests database insertion marker mismatch")
test.write_text(text.replace(marker, insert + marker, 1), encoding="utf-8")
