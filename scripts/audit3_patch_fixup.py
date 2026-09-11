from pathlib import Path
import re

path = Path('scripts/audit3_patch.py')
text = path.read_text(encoding='utf-8')
old = r'''r''' + "'''" + r'''    public LocalCertificate Ensure\(string domain\)\n    \{(.*?)\n    \}\n\n    public bool IsMaterialValid''' + "'''"
new = r'''r''' + "'''" + r'''    public LocalCertificate Ensure\(string domain\)\n    \{\n        var normalizedDomain = NormalizeDomain\(domain\);(.*?)\n    \}\n\n    public bool IsMaterialValid''' + "'''"
if old not in text:
    raise RuntimeError('Ensure matcher was not found in audit3_patch.py')
text = text.replace(old, new, 1)
text, count = re.subn(
    r'''# Remove duplicate normalization introduced inside the moved body\.\nreplace_once\(cert_path, .*?\n\s+''' + "'''" + r'''    private LocalCertificate EnsureCore\(string normalizedDomain\)\\n    \\{''' + "'''" + r'''\)\n''',
    '',
    text,
    count=1,
    flags=re.S)
if count != 1:
    # Use a broader, deterministic marker-to-marker removal if quote escaping differs.
    start = text.find('# Remove duplicate normalization introduced inside the moved body.')
    end = text.find('regex_once(\n    cert_path,', start)
    if start < 0 or end < 0:
        raise RuntimeError('Ensure cleanup block was not found in audit3_patch.py')
    text = text[:start] + text[end:]
path.write_text(text, encoding='utf-8', newline='')
print('Fixed LocalCertificateManager Ensure transformation matcher.')
