from pathlib import Path

path = Path("src/DevBox.Core/Services/ProjectWorkspaceService.cs")
text = path.read_text(encoding="utf-8")
old = '''        var tlsRollback = new TlsRollbackStateService(_rootPath);\n        var tlsState = tlsRollback.Capture(request.Domain);\n'''
new = '''        var rollbackDomain = request.Domain ?? $"{NormalizeProjectDirectoryName(request.Name)}.test";\n        var tlsRollback = new TlsRollbackStateService(_rootPath);\n        var tlsState = tlsRollback.Capture(rollbackDomain);\n'''
count = text.count(old)
if count != 2:
    raise RuntimeError(f"Expected two nullable rollback-domain sites, found {count}.")
path.write_text(text.replace(old, new), encoding="utf-8", newline="\n")
