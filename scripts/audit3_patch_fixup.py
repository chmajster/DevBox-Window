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

# RuntimeManager: duplicate validation inside the private under-lock method is harmless.
# Remove the brittle cleanup matcher and let compilation validate the generated method.
start = text.find('# Strip duplicated guards from moved Install body.')
end = text.find('# Public Install must not re-enter lock through Activate.', start)
if start < 0 or end < 0:
    raise RuntimeError('RuntimeManager cleanup block was not found in audit3_patch.py')
text = text[:start] + text[end:]

path.write_text(text, encoding='utf-8', newline='')
print('Fixed LocalCertificateManager matchers and removed brittle RuntimeManager cleanup matcher.')
