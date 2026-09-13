$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Lf([string]$Path) {
    return (Get-Content -LiteralPath $Path -Raw).Replace("`r`n", "`n")
}

function Write-Utf8Lf([string]$Path, [string]$Text) {
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Text.Replace("`r`n", "`n"), $utf8)
}

function Replace-Required([string]$Text, [string]$Old, [string]$New, [string]$Label) {
    if (-not $Text.Contains($Old)) {
        throw "Patch anchor was not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

# Git bootstrap: stop before side effects/native process start and preserve pre-existing roots on rollback.
$path = 'src/DevBox.Core/Services/GitProjectBootstrapService.cs'
$text = Read-Lf $path
$old = @'
    {
        ArgumentNullException.ThrowIfNull(request);
        var repository = ValidateRepositoryUrl(request.RepositoryUrl);
'@
$new = @'
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var repository = ValidateRepositoryUrl(request.RepositoryUrl);
'@
$text = Replace-Required $text $old $new 'Git bootstrap pre-cancel'
$old = @'
    private static async Task<ProcessResult> RunGitAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = DeveloperToolsService.BuildStartInfo(executable, arguments);
'@
$new = @'
    private static async Task<ProcessResult> RunGitAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = DeveloperToolsService.BuildStartInfo(executable, arguments);
'@
$text = Replace-Required $text $old $new 'Git process pre-cancel'
$old = @'
    private static void TryRollbackDestination(string path, bool existedBefore)
    {
        try
        {
            if (!Directory.Exists(path))
                return;
            if (!existedBefore)
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            foreach (var file in Directory.EnumerateFiles(path))
                File.Delete(file);
            foreach (var directory in Directory.EnumerateDirectories(path))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
'@
$new = @'
    private static void TryRollbackDestination(string path, bool existedBefore)
    {
        try
        {
            if (!Directory.Exists(path) || existedBefore)
                return;
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
'@
$text = Replace-Required $text $old $new 'Git bootstrap safe rollback for pre-existing root'
Write-Utf8Lf $path $text

# Legacy MySQL client wrapper: never start a native process for an already-cancelled request.
$path = 'src/DevBox.Core/Services/DatabaseManager.cs'
$text = Read-Lf $path
$old = @'
        string? standardInputPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
'@
$new = @'
        string? standardInputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(executable)
'@
$text = Replace-Required $text $old $new 'DatabaseManager native client pre-cancel'
Write-Utf8Lf $path $text

# Runtime database tools: pre-cancel before starting init/backup/restore native executables.
$path = 'src/DevBox.Core/Services/DatabaseRuntimeService.cs'
$text = Read-Lf $path
$old = @'
        CancellationToken cancellationToken,
        string? stdinFile = null)
    {
        EnsureExecutable(executable);
        var startInfo = new ProcessStartInfo
'@
$new = @'
        CancellationToken cancellationToken,
        string? stdinFile = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureExecutable(executable);
        var startInfo = new ProcessStartInfo
'@
$text = Replace-Required $text $old $new 'DatabaseRuntime native process pre-cancel'
Write-Utf8Lf $path $text

# Developer tool installers/version checks: do not spawn winget/npm/php/composer helpers after cancellation.
$path = 'src/DevBox.Core/Services/DeveloperToolsService.cs'
$text = Read-Lf $path
$old = @'
    private static async Task<string> RunCaptureAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo(executable, arguments);
'@
$new = @'
    private static async Task<string> RunCaptureAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = BuildStartInfo(executable, arguments);
'@
$text = Replace-Required $text $old $new 'Developer tools native process pre-cancel'
Write-Utf8Lf $path $text

# Expand changelog wording to cover the additional instances found in the final sweep.
$path = 'CHANGELOG.md'
$text = Read-Lf $path
$old = '- Database runtime initialization and native project-database clients honor pre-cancelled tokens before persisting a registration or starting a child process.'
$new = '- Database runtime initialization plus native database, developer-tool and Git helpers honor pre-cancelled tokens before persisting state or starting a child process.'
$text = Replace-Required $text $old $new 'CHANGELOG native helper cancellation summary'
$old = '- Provisioning/workspace rollback no longer recursively clears a project root that existed before the operation, avoiding deletion of files that appear concurrently after the initial empty-directory check.'
$new = '- Provisioning, workspace and Git-bootstrap rollback no longer recursively clear a project root that existed before the operation, avoiding deletion of files that appear concurrently after the initial empty-directory check.'
$text = Replace-Required $text $old $new 'CHANGELOG rollback summary'
Write-Utf8Lf $path $text

Write-Host 'Round 13 extra patches applied successfully.'
