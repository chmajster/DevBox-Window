from pathlib import Path
p = Path('.github/scripts/audit_round2_repair.py')
text = p.read_text(encoding='utf-8')
old = "marker = '''    private static string DisplayEngine(string engine) =>\\n'''"
new = "marker = '    private static string DisplayEngine(string engine) =>'"
if old not in text:
    raise RuntimeError('ProjectProvisioning marker expression not found in repair script')
p.write_text(text.replace(old, new, 1), encoding='utf-8', newline='\n')
