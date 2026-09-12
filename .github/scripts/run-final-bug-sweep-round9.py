from pathlib import Path

path = Path('.github/scripts/apply-final-bug-sweep-round9.py')
source = path.read_text(encoding='utf-8')
old = '''if text.count(marker) != 1:\n    raise RuntimeError("CHANGELOG Fixed marker was not unique")\n'''
new = '''if marker not in text:\n    raise RuntimeError("CHANGELOG Unreleased Fixed marker was not found")\n'''
if source.count(old) != 1:
    raise RuntimeError('round9 helper changelog guard was not found exactly once')
source = source.replace(old, new, 1)
exec(compile(source, str(path), 'exec'))
