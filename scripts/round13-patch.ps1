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

function Remove-OptionalLine([string]$Text, [string]$Line) {
    return $Text.Replace($Line + "`n", '')
}

# Environment lock cancellation must stop before every following synchronous mutation.
$path = 'src/DevBox.Core/Services/EnvironmentLockService.cs'
$text = Read-Lf $path
$old = @'
        ThrowIfDisposed();
        var root = EnsureProjectRoot(projectPath);
        var profile = _profiles.GetProfile(profileKey);
'@
$new = @'
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var root = EnsureProjectRoot(projectPath);
        var profile = _profiles.GetProfile(profileKey);
'@
$text = Replace-Required $text $old $new 'EnvironmentLock.ApplyProfile pre-cancel'
$old = @'
        ThrowIfDisposed();
        var root = EnsureProjectRoot(projectPath);
        var desired = Load(root);
        return await ApplyDesiredStateAsync(root, desired, cancellationToken).ConfigureAwait(false);
'@
$new = @'
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var root = EnsureProjectRoot(projectPath);
        var desired = Load(root);
        return await ApplyDesiredStateAsync(root, desired, cancellationToken).ConfigureAwait(false);
'@
$text = Replace-Required $text $old $new 'EnvironmentLock.ApplyLock pre-cancel'
$old = @'
    {
        var applied = new List<string>();
        var warnings = new List<string>();

        foreach (var pin in desired.Runtimes)
'@
$new = @'
    {
        cancellationToken.ThrowIfCancellationRequested();
        var applied = new List<string>();
        var warnings = new List<string>();

        foreach (var pin in desired.Runtimes)
'@
$text = Replace-Required $text $old $new 'EnvironmentLock desired-state pre-cancel'
$old = @'
        if (!desired.Database.Engine.Equals("none", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(desired.Database.Version))
'@
$new = @'
        cancellationToken.ThrowIfCancellationRequested();
        if (!desired.Database.Engine.Equals("none", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(desired.Database.Version))
'@
$text = Replace-Required $text $old $new 'EnvironmentLock database cancellation checkpoint'
$old = @'
        foreach (var serviceKey in desired.Services)
        {
            try
'@
$new = @'
        foreach (var serviceKey in desired.Services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
'@
$text = Replace-Required $text $old $new 'EnvironmentLock managed-service cancellation checkpoint'
$old = @'
        var manifestPath = Path.Combine(root, ProjectWorkspaceService.ManifestFileName);
'@
$new = @'
        cancellationToken.ThrowIfCancellationRequested();
        var manifestPath = Path.Combine(root, ProjectWorkspaceService.ManifestFileName);
'@
$text = Replace-Required $text $old $new 'EnvironmentLock metadata cancellation checkpoint'
$old = @'
        try
        {
            SynchronizeSite(root, desired);
'@
$new = @'
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SynchronizeSite(root, desired);
'@
$text = Replace-Required $text $old $new 'EnvironmentLock site cancellation checkpoint'
$old = @'
            UpdateManifestFromLock(manifest, desired);
            AtomicWrite(manifestPath, manifest.ToJsonString(JsonOptions));
'@
$new = @'
            UpdateManifestFromLock(manifest, desired);
            cancellationToken.ThrowIfCancellationRequested();
            AtomicWrite(manifestPath, manifest.ToJsonString(JsonOptions));
'@
$text = Replace-Required $text $old $new 'EnvironmentLock manifest cancellation checkpoint'
$old = @'
            _actions.SetActions(root, desired.Actions);
'@
$new = @'
            cancellationToken.ThrowIfCancellationRequested();
            _actions.SetActions(root, desired.Actions);
'@
$text = Replace-Required $text $old $new 'EnvironmentLock action cancellation checkpoint'
Write-Utf8Lf $path $text

# Database registration must not be persisted for an already-cancelled initialization request.
$path = 'src/DevBox.Core/Services/DatabaseRuntimeService.cs'
$text = Read-Lf $path
$old = @'
    {
        ThrowIfDisposed();
        var instance = Register(engine, version, port);
        if (instance.Initialized)
'@
$new = @'
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var instance = Register(engine, version, port);
        if (instance.Initialized)
'@
$text = Replace-Required $text $old $new 'DatabaseRuntime.EnsureInitialized pre-cancel'
Write-Utf8Lf $path $text

# Native database clients must not start when the caller has already cancelled.
$path = 'src/DevBox.Core/Services/ProjectDatabaseProvisioner.cs'
$text = Read-Lf $path
$old = @'
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
'@
$new = @'
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(executable)
'@
$text = Replace-Required $text $old $new 'ProjectDatabaseProvisioner.RunAsync pre-cancel'
Write-Utf8Lf $path $text

# Project repair observes cancellation before mutating state. Existing project roots are never recursively cleared by rollback.
$path = 'src/DevBox.Core/Services/ProjectWorkspaceService.cs'
$text = Read-Lf $path
$old = @'
    {
        ArgumentNullException.ThrowIfNull(site);
        var repaired = new List<string>();
'@
$new = @'
    {
        ArgumentNullException.ThrowIfNull(site);
        cancellationToken.ThrowIfCancellationRequested();
        var repaired = new List<string>();
'@
$text = Replace-Required $text $old $new 'ProjectWorkspace.Repair pre-cancel'
$old = @'
        _siteManager.Update(site);
        repaired.Add("Regenerated Nginx vhost from site metadata.");
'@
$new = @'
        cancellationToken.ThrowIfCancellationRequested();
        _siteManager.Update(site);
        repaired.Add("Regenerated Nginx vhost from site metadata.");
'@
$text = Replace-Required $text $old $new 'ProjectWorkspace.Repair site checkpoint'
$old = @'
        if (site.HttpsEnabled)
        {
            _certificateManager.Ensure(site.Domain);
'@
$new = @'
        if (site.HttpsEnabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _certificateManager.Ensure(site.Domain);
'@
$text = Replace-Required $text $old $new 'ProjectWorkspace.Repair TLS checkpoint'
$old = @'
        if (!File.Exists(Path.Combine(projectRoot, ManifestFileName)))
        {
            SaveManifest(projectRoot, new DevBoxProjectManifest(
'@
$new = @'
        if (!File.Exists(Path.Combine(projectRoot, ManifestFileName)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveManifest(projectRoot, new DevBoxProjectManifest(
'@
$text = Replace-Required $text $old $new 'ProjectWorkspace.Repair manifest checkpoint'
$old = @'
        if (site.PhpVersion is null && detection.RequiredPhpExtensions.Count > 0)
        {
            var available = detection.RequiredPhpExtensions.Where(IsExtensionBinaryAvailable).ToArray();
'@
$new = @'
        if (site.PhpVersion is null && detection.RequiredPhpExtensions.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = detection.RequiredPhpExtensions.Where(IsExtensionBinaryAvailable).ToArray();
'@
$text = Replace-Required $text $old $new 'ProjectWorkspace.Repair extension checkpoint'
$old = @'
    private static void RollbackOwnedProjectDirectory(string projectRoot, bool existedBefore)
    {
        if (!Directory.Exists(projectRoot))
            return;
        if (!existedBefore)
        {
            Directory.Delete(projectRoot, recursive: true);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(projectRoot))
            File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(projectRoot))
            Directory.Delete(directory, recursive: true);
    }
'@
$new = @'
    private static void RollbackOwnedProjectDirectory(string projectRoot, bool existedBefore)
    {
        if (!Directory.Exists(projectRoot) || existedBefore)
            return;
        Directory.Delete(projectRoot, recursive: true);
    }
'@
$text = Replace-Required $text $old $new 'ProjectWorkspace safe rollback for pre-existing root'
Write-Utf8Lf $path $text

# Keep release/update documentation aligned with the current unsigned release workflow.
$path = 'README.md'
$text = Read-Lf $path
$old = 'Authenticode signing is applied only when `WINDOWS_SIGNING_CERTIFICATE_BASE64` and `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` are configured. Without them, artifacts remain unsigned and SHA-256 release checksums still provide integrity verification.'
$new = 'The current manual release workflow does not perform Authenticode signing. Release assets include SHA-256 checksums used by the self-updater for integrity verification; checksum verification does not establish publisher identity.'
$text = Replace-Required $text $old $new 'README Authenticode release description'
Write-Utf8Lf $path $text

$path = 'ARCHITECTURE.md'
$text = Read-Lf $path
$old = 'Release CI produces self-contained x64/ARM64 GUI and CLI artifacts, portable ZIPs, the Inno Setup installer and SHA-256 checksums. Optional Authenticode signing is applied only when signing secrets are configured.'
$new = 'Release CI produces self-contained x64/ARM64 GUI and CLI artifacts, portable ZIPs, the Inno Setup installer and SHA-256 checksums. The current release workflow does not perform Authenticode signing, so checksum verification provides integrity but not publisher identity.'
$text = Replace-Required $text $old $new 'ARCHITECTURE Authenticode release description'
Write-Utf8Lf $path $text

$path = 'CHANGELOG.md'
$text = Read-Lf $path
$marker = "### Fixed`n`n"
$index = $text.IndexOf($marker, [StringComparison]::Ordinal)
if ($index -lt 0) { throw 'CHANGELOG Unreleased Fixed section was not found.' }
$bullets = @'
- Path safety now preserves filesystem roots (`C:\`, UNC roots and `/`) instead of trimming them into drive-relative or empty paths before containment checks.
- ZIP extraction rejects Windows path aliases with trailing dots/spaces, non-canonical dot segments and reserved DOS device names before any file is written.
- Configuration validation enforces its 2 MiB limit on actual UTF-8 bytes, matching the read/restore limit for multibyte content.
- Project provisioning, project repair and environment-lock application honor cancellation before synchronous state mutations; cancelled operations no longer continue into Site/TLS/manifest/action/service updates.
- Database runtime initialization and native project-database clients honor pre-cancelled tokens before persisting a registration or starting a child process.
- Provisioning/workspace rollback no longer recursively clears a project root that existed before the operation, avoiding deletion of files that appear concurrently after the initial empty-directory check.
- Release/update documentation no longer claims Authenticode signing or publisher verification that the current release workflow and self-updater do not perform.
'@
$text = $text.Insert($index + $marker.Length, $bullets + "`n")
$text = Remove-OptionalLine $text '- Release workflow support for Authenticode signing of DevBox-owned binaries and the installer when `WINDOWS_SIGNING_CERTIFICATE_BASE64` and `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` secrets are configured.'
$text = Remove-OptionalLine $text '- Self-update verifies that a trusted installer is signed by the same publisher identity as the currently running signed DevBox executable and enables certificate revocation checks.'
$text = Remove-OptionalLine $text '- Authenticode releases require `WINDOWS_SIGNING_CERTIFICATE_BASE64` and `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` repository secrets; manual release fails closed when signing material is unavailable.'
$old = '- Self-update now requires both the published SHA-256 checksum and a valid trusted Authenticode signature before the downloaded installer can execute.'
$new = '- Self-update requires the published SHA-256 checksum before the downloaded installer can execute; current releases do not enforce Authenticode publisher identity.'
$text = Replace-Required $text $old $new 'CHANGELOG self-update security claim'
$old = '- Manual releases now require the Windows code-signing certificate; unsigned release artifacts are rejected instead of being published for an updater that will not trust them.'
$new = '- Manual releases publish SHA-256 checksums but do not currently require a Windows code-signing certificate.'
$text = Replace-Required $text $old $new 'CHANGELOG manual-release security claim'
Write-Utf8Lf $path $text

Write-Host 'Round 13 patches applied successfully.'
