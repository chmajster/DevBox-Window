from pathlib import Path

path = Path(__file__).resolve().parent / "audit4_patch.py"
text = path.read_text(encoding="utf-8")

# Fix the changelog matcher so only the Unreleased/Fixed section is edited.
start = text.index("# 12. Changelog.")
end = text.index('print("Audit round 4 patch applied successfully.")')
replacement = '''# 12. Changelog.\nchangelog = read("CHANGELOG.md")\nunreleased = changelog.index("## Unreleased")\nfixed = changelog.index("### Fixed\\n\\n", unreleased) + len("### Fixed\\n\\n")\nnotes = """- Local TLS and Local CA mutations now avoid re-entrant lock deadlocks, serialize CA lifecycle operations, and roll back failed CA creation/rotation transactionally.\n- ADDON install/repair/uninstall operations and mutable environment/runtime/service catalogs are serialized across GUI and CLI processes.\n- phpMyAdmin Repair now refreshes a stale MySQL/MariaDB port instead of accepting an otherwise complete obsolete configuration.\n- Project provisioning, WordPress setup and Git bootstrap restore pre-existing TLS material and trust state when a later setup stage fails.\n- Explicit database ports are rejected when another process already owns the listener, and database autodiscovery no longer persists registrations outside the registration lock.\n- Managed services reject HTTPS, standard database and currently registered database ports reserved by DevBox core services.\n- Cancelled project database client operations terminate their native child process instead of leaving it running in the background.\n- Self-update verifies that a trusted installer is signed by the same publisher identity as the currently running signed DevBox executable and enables certificate revocation checks.\n- Project/Site path validation rejects junctions and other reparse points that could escape the DevBox `www` tree.\n- Runtime activation/import copy loops now observe cancellation between files and directories.\n\n"""\nwrite("CHANGELOG.md", changelog[:fixed] + notes + changelog[fixed:])\n\n'''
text = text[:start] + replacement + text[end:]

# ProjectDatabaseProvisioner now catches Win32Exception when terminating cancelled native clients.
marker = "# 8. Kill cancelled database client processes.\n"
insert = '''replace_once(\n    "src/DevBox.Core/Services/ProjectDatabaseProvisioner.cs",\n    "using System.Diagnostics;",\n    "using System.ComponentModel;\\nusing System.Diagnostics;")\n\n'''
if insert not in text:
    text = text.replace(marker, marker + insert, 1)

path.write_text(text, encoding="utf-8", newline="\n")
