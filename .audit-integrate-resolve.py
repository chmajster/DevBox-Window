from pathlib import Path
import subprocess

MAIN = 'ad7d7e51f24aa20f63f477346f02f79b776c9914'
AUDIT = 'ff6e3ae1e87b7c3ce31243f05432e790021744f9'


def git(*args):
    return subprocess.check_output(['git', *args], text=True, encoding='utf-8')


def original(ref, path):
    return git('show', f'{ref}:{path}')


def write(path, content):
    if any(marker in content for marker in ('<<<<<<<', '=======', '>>>>>>>')):
        raise RuntimeError(f'Unresolved conflict markers in {path}')
    Path(path).write_text(content, encoding='utf-8', newline='\n')


def replace_once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError(f'Expected exactly one anchor: {old[:100]}')
    return text.replace(old, new)


expected = {'CHANGELOG.md', 'src/DevBox.Core/Services/ArchiveSafety.cs', 'src/DevBox.Core/Services/LogReader.cs'}
conflicts = set(git('diff', '--name-only', '--diff-filter=U').splitlines())
if conflicts != expected:
    raise RuntimeError(f'Unexpected conflict set: {conflicts}')
if git('rev-parse', 'HEAD').strip() != MAIN:
    raise RuntimeError('The integration must start from the reviewed main commit')

path = 'CHANGELOG.md'
new_entries = original(AUDIT, path).split('### Fixed\n\n', 1)[1].split('\n\n', 1)[0]
new_entries = new_entries.replace('Added round-13 regression coverage', 'Added regression coverage')
main_changelog = original(MAIN, path)
if main_changelog.index('## Unreleased') > main_changelog.index('### Fixed'):
    raise RuntimeError('Unexpected changelog structure')
write(path, main_changelog.replace('### Fixed\n\n', '### Fixed\n\n' + new_entries + '\n\n', 1))

path = 'src/DevBox.Core/Services/ArchiveSafety.cs'
start = '    private static void ValidateEntryPath('
end = '    private static string ResolveOutputPath('
reviewed = original(AUDIT, path)
section = reviewed[reviewed.index(start):reviewed.index(end)]
merged = Path(path).read_text(encoding='utf-8')
write(path, merged[:merged.index(start)] + section + merged[merged.index(end):])

path = 'src/DevBox.Core/Services/LogReader.cs'
text = original(MAIN, path)
text = replace_once(text,
    '            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)',
    '            .Where(IsRegularLogFile)')
reviewed = original(AUDIT, path)
start = '    internal static bool IsRegularLogFile('
end = '    public IReadOnlyList<string> ReadTail('
helper = reviewed[reviewed.index(start):reviewed.index(end)]
text = replace_once(text, end, helper + end)
text = replace_once(text,
    '    private string ResolveSafePath(string fileName)\n    {\n',
    '    private string ResolveSafePath(string fileName)\n    {\n        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);\n')
write(path, text)
subprocess.run(['git', 'add', *sorted(expected)], check=True)
print('Resolved only the three reviewed conflicts; retained main download and log-root protections.')
