from pathlib import Path

path = Path('scripts/audit3_patch.py')
text = path.read_text(encoding='utf-8')

# Ensure(): consume original normalization inside the match.
old = r'''r''' + "'''" + r'''    public LocalCertificate Ensure\(string domain\)\n    \{(.*?)\n    \}\n\n    public bool IsMaterialValid''' + "'''"
new = r'''r''' + "'''" + r'''    public LocalCertificate Ensure\(string domain\)\n    \{\n        var normalizedDomain = NormalizeDomain\(domain\);(.*?)\n    \}\n\n    public bool IsMaterialValid''' + "'''"
if old not in text:
    raise RuntimeError('Ensure matcher was not found in audit3_patch.py')
text = text.replace(old, new, 1)
start = text.find('# Remove duplicate normalization introduced inside the moved body.')
end = text.find('regex_once(\n    cert_path,', start)
if start < 0 or end < 0:
    raise RuntimeError('Ensure cleanup block was not found in audit3_patch.py')
text = text[:start] + text[end:]

# Delete(): consume original normalization inside the match.
old_delete = r'''r''' + "'''" + r'''    public void TrustForCurrentUser\(string domain\)\n    \{.*?\n    \}\n\n    public void UntrustForCurrentUser\(string domain\)\n    \{.*?\n    \}\n\n    public void Delete\(string domain\)\n    \{(.*?)\n    \}\n\n    private static void RemoveTrustedThumbprint''' + "'''"
new_delete = r'''r''' + "'''" + r'''    public void TrustForCurrentUser\(string domain\)\n    \{.*?\n    \}\n\n    public void UntrustForCurrentUser\(string domain\)\n    \{.*?\n    \}\n\n    public void Delete\(string domain\)\n    \{\n        var normalizedDomain = NormalizeDomain\(domain\);(.*?)\n    \}\n\n    private static void RemoveTrustedThumbprint''' + "'''"
if old_delete not in text:
    raise RuntimeError('Delete matcher was not found in audit3_patch.py')
text = text.replace(old_delete, new_delete, 1)
start = text.find('# Remove duplicate normalization inside Delete body.')
end = text.find("replace_once(cert_path, '''    private string CertificatePath", start)
if start < 0 or end < 0:
    raise RuntimeError('Delete cleanup block was not found in audit3_patch.py')
text = text[:start] + text[end:]

# Correct regex replacement backreferences.
text = text.replace(r'{\1', r'{\g<1>')
text = text.replace(r'{\2', r'{\g<2>')
text = text.replace(r'{\3', r'{\g<3>')
text = text.replace(r';\1', r';\g<1>')

# Duplicate validation in the private RuntimeManager method is harmless.
start = text.find('# Strip duplicated guards from moved Install body.')
end = text.find('# Public Install must not re-enter lock through Activate.', start)
if start < 0 or end < 0:
    raise RuntimeError('RuntimeManager cleanup block was not found in audit3_patch.py')
text = text[:start] + text[end:]

# ProjectSnapshotService contains two C# CopyTo calls with 16-space indentation.
# Replace both in one deterministic operation. The later 12-space generator matcher
# was intended for the same extraction call and must therefore be removed.
first_stmt = "replace_once(snapshot, '''                input.CopyTo(output);''', '''                await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);''')"
if first_stmt not in text:
    raise RuntimeError('Primary snapshot CopyTo patch statement was not found.')
replacement = """snapshot_text = read(snapshot)\nif snapshot_text.count('                input.CopyTo(output);') != 2:\n    raise RuntimeError('ProjectSnapshotService: expected two 16-space CopyTo calls')\nwrite(snapshot, snapshot_text.replace('                input.CopyTo(output);', '                await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);'))"""
text = text.replace(first_stmt, replacement, 1)
late_stmt = "replace_once(snapshot, '''            input.CopyTo(output);''', '''            await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);''')"
if late_stmt not in text:
    raise RuntimeError('Late snapshot CopyTo patch statement was not found.')
text = text.replace(late_stmt, '', 1)

path.write_text(text, encoding='utf-8', newline='')
print('Fixed TLS/runtime backreferences and snapshot async transformation.')
