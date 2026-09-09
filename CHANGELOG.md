# Changelog

All notable changes to DevBox Windows are documented here.

## Unreleased

### Added

- New DevBox application icon with editable SVG source and Windows ICO asset.
- DevBox branding is now embedded in the executable, applied to WPF windows and used by the Inno Setup installer.
- Installer now always exposes the installation directory so the target path can be changed.
- Installer options now control desktop and Start menu shortcuts and whether DevBox starts after installation.
- Existing DevBox installations are detected and presented with upgrade/update, reinstall, or uninstall maintenance actions.
- Reinstall removes the existing package before continuing, while upgrade/update keeps the installation and replaces application files.
- CI now compiles the Inno Setup script on pull requests to catch installer regressions before merge.

### Fixed

- DevBox now stops managed Nginx, PHP and MySQL processes during application exit and can re-adopt processes recorded by a previous DevBox session.
- Fresh MySQL data directories are initialized automatically before first startup, while partial/non-empty initialization states fail safely.
- First Run is shown whenever any required environment component is incomplete, including manual-action items.
- Composer, npm and pnpm `.cmd`/`.bat` launchers are executed through the Windows command processor.
- Runtime versions are sorted semantically rather than lexicographically.
- Runtime activation restores a service that was running before the switch, and PHP runtimes assigned to Sites cannot be removed.
- Runtime discovery ignores transactional `.backup-*` directories left behind after interrupted replacement/rollback operations.
- Bulk service actions continue after individual failures and report aggregate failures/missing runtimes.
- Service shutdown falls back to managed process-tree termination when a configured graceful-stop executable is corrupt or cannot be launched.
- Invalid service executables now fail with a controlled DevBox startup error instead of leaking the underlying Windows process-start exception.
- Versioned PHP pool start/stop operations are serialized per version and PHP FastCGI port collisions are rejected when assigning runtimes to Sites.
- ADDONS installation keeps the previous version until configuration succeeds and rolls back on configuration failure.
- ADDONS checksum verification consistently accepts valid SHA-256 values with surrounding whitespace.
- ADDONS manifests validate required fields before duplicate-key processing and reject installation directly into the shared `www` root.
- phpMyAdmin Repair now regenerates an incomplete or damaged `config.inc.php`.
- phpMyAdmin local cookie authentication permits the intentionally passwordless local MySQL root account.
- Conflicting duplicate hosts-file entries for a DevBox domain are normalized to one loopback mapping.
- Expired/replaced local TLS certificates are removed from the current-user trusted root store during rotation.
- The Windows autostart setting is synchronized with the actual `HKCU\...\Run` registration.
- Site document roots are constrained to the DevBox `www` directory.
- PHP extension configuration now matches only the exact `extension=` directive, so `extension_dir=` is never mistaken for a loaded module.
- PHP extension parsing recognizes inline `php.ini` comments and extension toggling collapses duplicate entries to a single canonical directive.
- PHP extension checks report the runtime as unavailable when PHP CLI cannot be started, including corrupt or non-executable runtime files.
- Default MySQL shutdown explicitly uses the local root account and a bounded connection timeout before process termination fallback.

## 0.2.1 - 2026-09-09

### Fixed

- Closing the First Run dialog no longer shuts down DevBox before the main dashboard is shown.
- `Continue to DevBox` now closes only the setup dialog and continues into the main window.
- First Run runtime `Install` buttons now use an explicit ancestor binding to the setup ViewModel command.
- `Install` is disabled for checks that are already ready or require manual action, including MySQL when no verified automatic package is available.

## 0.2.0 - 2026-09-09

### Added

- WPF dashboard for native Nginx, PHP FastCGI and MySQL lifecycle.
- MVVM presentation structure with dependency injection.
- Versioned runtime manager with HTTPS, pinned SHA-256, safe extraction, staging, activation and rollback.
- First Run environment readiness wizard.
- `.test` Sites Manager with hosts integration and generated Nginx vhosts.
- PHP Manager with `php.ini` and extension management.
- Per-site PHP version pinning with dedicated FastCGI pools.
- Local SSL certificate generation, current-user trust management and TLS Nginx configuration.
- MySQL database list/create/backup/restore tools.
- Manifest-driven ADDONS lifecycle and phpMyAdmin 5.2.3 default addon.
- Logs and Diagnostics screens.
- Developer Tools screen for Composer, Node.js LTS, npm and pnpm.
- GitHub Releases update checker.
- System tray, minimize-to-tray, current-user Windows autostart and automatic service startup settings.
- Windows x64/ARM64 release packaging, Inno Setup installer and SHA-256 release checksums.
- CodeQL, Dependabot, NuGet vulnerability audit and expanded automated tests.

### Security

- Narrow UAC elevation only for `.test` hosts-file updates.
- Addon and runtime ZIP traversal protection.
- Runtime/addon checksum enforcement.
- Composer installer SHA-384 verification.
- MySQL credentials removed from process arguments in favor of a short-lived client defaults file.
- Update release URLs restricted to HTTPS GitHub release pages.

### Known limitations

- Built-in automatic MySQL download remains disabled until the package can satisfy the same pinned SHA-256 policy as other runtimes.
- The generated Windows installer is not Authenticode code-signed.
