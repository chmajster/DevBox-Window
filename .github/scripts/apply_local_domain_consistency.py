from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding='utf-8')
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{path}: expected one match, got {count}: {old[:120]!r}')
    file.write_text(text.replace(old, new, 1), encoding='utf-8')


def replace_between(path: str, start: str, end: str, replacement: str) -> None:
    file = Path(path)
    text = file.read_text(encoding='utf-8')
    start_index = text.index(start)
    end_index = text.index(end, start_index)
    file.write_text(text[:start_index] + replacement + text[end_index:], encoding='utf-8')


# Site names and project workspace names already have a public/project-level 80 character contract.
# Keep those limits aligned and generate DNS-safe local domains separately.
replace_once(
    'src/DevBox.Core/Services/SiteManager.cs',
    '        var normalizedDomain = NormalizeDomain(domain ?? $"{normalizedName}.test");',
    '        var normalizedDomain = NormalizeDomain(domain ?? LocalDomainName.FromName(normalizedName));')
replace_once(
    'src/DevBox.Core/Services/SiteManager.cs',
    '[GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,62}$", RegexOptions.CultureInvariant)]',
    '[GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant)]')

replace_once(
    'src/DevBox.Core/Services/ProjectWorkspaceService.cs',
    '        var rollbackDomain = request.Domain ?? $"{NormalizeProjectDirectoryName(request.Name)}.test";',
    '        var rollbackDomain = request.Domain ?? LocalDomainName.FromName(NormalizeProjectDirectoryName(request.Name));')
replace_once(
    'src/DevBox.Core/Services/ProjectWorkspaceService.cs',
    '[GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,62}$", RegexOptions.CultureInvariant)]',
    '[GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,79}$", RegexOptions.CultureInvariant)]')

replace_once(
    'src/DevBox.Core/Services/ProjectProvisioningService.cs',
    '        var rollbackDomain = request.Domain ?? $"{request.Name.Trim().ToLowerInvariant()}.test";',
    '        var rollbackDomain = request.Domain ?? LocalDomainName.FromName(request.Name);')

replace_once(
    'src/DevBox.Core/Services/WordPressToolkitService.cs',
    '        var tlsState = tlsRollback.Capture(request.Domain ?? $"{request.Name.Trim().ToLowerInvariant()}.test");',
    '        var tlsState = tlsRollback.Capture(request.Domain ?? LocalDomainName.FromName(request.Name));')

replace_once(
    'src/DevBox.Core/Services/GitProjectBootstrapService.cs',
    '        var domain = NormalizeDomain(request.Domain ?? $"{projectName}.test");',
    '        var domain = NormalizeDomain(request.Domain ?? LocalDomainName.FromName(projectName));')

replace_once(
    'src/DevBox.Core/Services/ProjectTransferService.cs',
    '        var domain = GetString(manifestObject, "Domain") ?? $"{projectName}.test";',
    '        var domain = GetString(manifestObject, "Domain") ?? LocalDomainName.FromName(projectName);')
replace_once(
    'src/DevBox.Core/Services/ProjectTransferService.cs',
    '            var domain = NormalizeDomain(targetDomain ?? (name.Equals(transfer.ProjectName, StringComparison.OrdinalIgnoreCase) ? transfer.Domain : $"{name}.test"));',
    '            var domain = NormalizeDomain(targetDomain ?? (name.Equals(transfer.ProjectName, StringComparison.OrdinalIgnoreCase) ? transfer.Domain : LocalDomainName.FromName(name)));')

replace_once(
    'src/DevBox.Core/Services/EnvironmentLockService.cs',
    '        var domain = GetString(manifest, "Domain") ?? $"{projectName}.test";',
    '        var domain = GetString(manifest, "Domain") ?? LocalDomainName.FromName(projectName);')

# The first snapshot hardening pass adds a local hashing implementation. Use the shared policy instead.
replace_once(
    'src/DevBox.Core/Services/ProjectSnapshotService.cs',
    'using System.IO.Compression;\nusing System.Security.Cryptography;\nusing System.Text;\nusing System.Text.Json;\n',
    'using System.IO.Compression;\nusing System.Text.Json;\n')
replace_between(
    'src/DevBox.Core/Services/ProjectSnapshotService.cs',
    '    private static string BuildDomain(string projectName)',
    '    private static string NormalizeProjectName(string value)',
    '''    private static string BuildDomain(string projectName) => LocalDomainName.FromName(projectName);\n\n''')
