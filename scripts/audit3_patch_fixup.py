from pathlib import Path

path = Path('scripts/audit3_patch.py')
text = path.read_text(encoding='utf-8')

# Ensure(): consume the original normalization line in the regex capture so the new
# wrapper owns normalization and locking without generating duplicate locals.
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

# Delete(): likewise consume the existing normalization line inside the capture.
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

# RuntimeManager Install(): consume the original public validation lines in the regex
# so only the wrapper performs them and the under-lock body starts at actual mutation work.
old_runtime = r'''r''' + "'''" + r'''    public async Task InstallAsync\(RuntimeDefinition definition, CancellationToken cancellationToken = default\)\n    \{(.*?)\n    \}\n\n    public Task ActivateAsync''' + "'''"
new_runtime = r'''r''' + "'''" + r'''    public async Task InstallAsync\(RuntimeDefinition definition, CancellationToken cancellationToken = default\)\n    \{\n        ThrowIfDisposed\(\);\n        ArgumentNullException.ThrowIfNull\(definition\);\n        ValidateDefinition\(definition\);(.*?)\n    \}\n\n    public Task ActivateAsync''' + "'''"
if old_runtime not in text:
    raise RuntimeError('RuntimeManager install matcher was not found in audit3_patch.py')
text = text.replace(old_runtime, new_runtime, 1)
start = text.find('# Strip duplicated guards from moved Install body.')
end = text.find('# Public Install must not re-enter lock through Activate.', start)
if start < 0 or end < 0:
    raise RuntimeError('RuntimeManager install cleanup block was not found in audit3_patch.py')
text = text[:start] + text[end:]

path.write_text(text, encoding='utf-8', newline='')
print('Fixed LocalCertificateManager and RuntimeManager transformation matchers.')
