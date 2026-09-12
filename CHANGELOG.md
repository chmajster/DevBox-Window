# Changelog

All notable changes to DevBox Windows are documented here.

## Unreleased

- Manual Release can keep PHP/phpMyAdmin online-only; PHP is omitted from release packaging and both can be downloaded on demand from verified catalogs.
- Runtimes now exposes a verified Download action for PHP, while phpMyAdmin uses Download/Reinstall in ADDONS and remains installable even before PHP is present.

### Added

- GitHub Actions now separates automatic pull-request validation from publishing: PRs run the reusable Windows test/package validation workflow, while releases are started manually with a `patch`, `minor`, or `major` version increment that updates version metadata, creates the tag and publishes the GitHub Release.
- Project Manager WPF workflow for creating, importing, diagnosing, repairing and operating local projects.
- Automatic stack detection for Laravel, Symfony, WordPress, Composer PHP and Node projects, including Composer `ext-*` requirement discovery.
- Versioned per-project `devbox.json` manifests and persistent built-in/custom stack profiles.
- Project Health and Repair checks for document roots, Nginx vhosts, PHP runtimes/extensions, manifests and local TLS files.
- Safe predefined project command presets for Composer, npm, Laravel Artisan and Symfony Console workflows.
- Project provisioning that combines Site registration, project metadata, database creation and optional managed-service declarations.
- Multi-engine project database provisioning for MySQL, MariaDB and PostgreSQL using native clients without placing passwords on process command lines.
- MySQL database size/charset/collation metadata plus protected drop, clone and rename operations.
- Manifest-driven optional services in `config/services.json` and integration with the existing DevBox process lifecycle.
- Verified Mailpit `1.31.1` installer for Windows x64/ARM64 using pinned GitHub release SHA-256 values; web UI listens on `8025` and SMTP on `1025`.
- Verified Microsoft Garnet `2.1.7` installer as the native Redis-compatible Windows service on `127.0.0.1:6379`, using pinned x64/ARM64 ReadyToRun package SHA-256 values.
- Xdebug configuration UI and safe local DLL installation with PE validation, optional SHA-256 verification, atomic replacement and recorded provenance checksum.
- Portable Node.js LTS `24.19.0` runtime catalog for Windows x64/ARM64 with pinned official SHA-256 values and versioned installation under `runtime/node/<version>`.
- Per-project Node.js pinning: npm presets use the exact `NodeVersion` recorded in `devbox.json` instead of silently falling back to another Node installation from `PATH`.
- Standalone `devbox.exe` CLI backed by `DevBox.Core`, with service status/start/stop/restart, Site creation, PHP runtime activation, database creation and ADDON installation commands.
- Verified application self-update flow that downloads the stable release installer and `SHA256SUMS.txt`, checks the installer hash before execution, closes DevBox through the normal shutdown path and relaunches it after installation.
- Release builds now package `devbox.exe` with the GUI application for both x64 and ARM64 portable layouts.
- Release workflow support for Authenticode signing of DevBox-owned binaries and the installer when `WINDOWS_SIGNING_CERTIFICATE_BASE64` and `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` secrets are configured.
- CI now publishes the CLI as a self-contained single-file executable and validates that it is included in the installer input layout.
- Official DevBox 0.2.2 release packages now bundle Nginx `1.31.5`, PHP FastCGI `8.5.10` NTS and MySQL `8.4.11` LTS under versioned `runtime/` directories.
- First Run can activate a bundled runtime locally without making an HTTP request; PHP and Nginx retain their verified remote fallback when the bundle is absent.
- Release packaging records upstream source URLs and calculated SHA-256 values in `runtime/bundled-runtimes.json`; PHP and Nginx archives are additionally verified against pinned SHA-256 values before extraction.
- MySQL becomes an automatic First Run action when the packaged MySQL payload is available, while remote MySQL download remains disabled without a pinned SHA-256 source.
- New DevBox application icon with editable SVG source and Windows ICO asset.
- DevBox branding is now embedded in the executable, applied to WPF windows and used by the Inno Setup installer.
- Installer now always exposes the installation directory so the target path can be changed.
- Installer options now control desktop and Start menu shortcuts and whether DevBox starts after installation.
- Existing DevBox installations are detected and presented with upgrade/update, reinstall, or uninstall maintenance actions.
- Reinstall removes the existing package before continuing, while upgrade/update keeps the installation and replaces application files.
- CI now compiles the Inno Setup script on pull requests to catch installer regressions before merge.

### Fixed

- ZIP extraction rejects duplicate and case-insensitive alias output paths so later archive entries cannot overwrite previously validated runtime/ADDON files.
- Protected-path validation rejects a `www`/backup/service root that is itself a junction or symbolic link; managed-service executable, working-directory, stop-executable and log paths now use the same reparse-aware boundary checks.
- Environment profiles reuse the canonical Project Action policy, preventing profiles with unsupported executables or incompatible action definitions from being saved and then failing only during apply.
- Git bootstrap, project transfer and project manifests use the same strict `.test` domain validation as Sites/TLS, rejecting empty labels, oversized labels and leading/trailing hyphens before mutation begins.
- Git bootstrap and WordPress setup preserve the original operation exception when TLS/database/site cleanup also fails, instead of reporting only the rollback failure.
- Developer tools, PHP extension checks, configuration validators, Git bootstrap, WordPress CLI, project database clients, MySQL text commands and service stop helpers retain bounded stdout/stderr while continuing to drain child-process pipes.
- WP-CLI cancellation now covers stdin writes as well as process waiting, terminating the child process instead of leaving an interactive command running after cancellation.
- Configuration read/restore enforces the 2 MiB safety limit before loading a configuration or backup into memory.
- Composer installer temporary-directory cleanup is best-effort so an antivirus/lock cleanup error cannot replace the primary installation result.

- ADDONS install/entry-point paths now reject junctions, symbolic links and other reparse-point traversal, including direct installer calls that bypass the catalog.
- ADDONS local URLs are restricted to plain `http://*.test` on port 80, matching the Nginx vhost DevBox actually generates; unsupported HTTPS/custom-port URLs are rejected instead of producing unreachable addons.
- ADDON keys are capped at 64 characters so hand-edited or marketplace catalogs cannot generate invalid Windows lock/temp paths.
- ADDON installation now observes cancellation while copying staged files and immediately before the irreversible directory swap, and rejects reparse points in staged payloads.
- Configuration backup restore now rejects reparse-point traversal out of `backups/configuration`.
- Project actions, command presets, snapshots, transfers and environment-lock operations now consistently reject project roots that traverse a junction/symbolic link outside the DevBox `www` tree; project-action working directories receive the same protection.
- Project action and preset command output is drained with a bounded in-memory capture, preventing noisy child processes from growing DevBox memory without limit.
- Command event subscribers are isolated so failing `CanExecuteChanged`/`ExecutionFailed` handlers cannot corrupt asynchronous command state.
- Persisted project-action, environment-profile, runtime-catalog, secret-store and Task Center duplicate identities are rejected deterministically instead of being silently overwritten or surfacing collection exceptions.
- ADDONS catalogs reject conflicting install directories/domains, initialize safely across competing processes and report malformed marketplace keys as controlled data errors.
- Managed-service database-port reservations now parse registration property names case-insensitively and reject malformed registration records.
- Service shutdown re-checks cancellation immediately before forced process-tree termination, closing the graceful-timeout/kill race.

- Local TLS and Local CA mutations now avoid re-entrant lock deadlocks, serialize CA lifecycle operations, and roll back failed CA creation/rotation transactionally.
- ADDON install/repair/uninstall operations and mutable environment/runtime/service catalogs are serialized across GUI and CLI processes.
- phpMyAdmin Repair now refreshes a stale MySQL/MariaDB port instead of accepting an otherwise complete obsolete configuration.
- Project provisioning, WordPress setup and Git bootstrap restore pre-existing TLS material and trust state when a later setup stage fails.
- Explicit database ports are rejected when another process already owns the listener, and database autodiscovery no longer persists registrations outside the registration lock.
- Managed services reject HTTPS, standard database and currently registered database ports reserved by DevBox core services.
- Cancelled project database client operations terminate their native child process instead of leaving it running in the background.
- Self-update verifies that a trusted installer is signed by the same publisher identity as the currently running signed DevBox executable and enables certificate revocation checks.
- Project/Site path validation rejects junctions and other reparse points that could escape the DevBox `www` tree.
- Runtime activation/import copy loops now observe cancellation between files and directories.

- Environment profile application no longer writes `devbox.lock.json` when prerequisites fail or the apply operation reports warnings.
- Site, TLS and runtime mutations are serialized across DevBox processes to prevent lost updates and concurrent replacement races.
- Project and WordPress provisioning now roll back databases created by the failing operation without deleting pre-existing user databases.
- phpMyAdmin and WordPress now use the registered MySQL/MariaDB runtime port instead of assuming `3306`; MariaDB defaults to the managed `3316` port.
- Database client execution no longer relies on plaintext temporary credential files for MariaDB/PostgreSQL runtime operations.
- Configuration restore validates the matching configuration kind before atomic replacement, and Task Center shutdown waits for active task synchronization before disposing primitives.
- Snapshot and project-transfer file copies now honor cancellation during large file operations.
- Git bootstrap fails explicitly when environment profile application is incomplete instead of continuing with a partial environment.
- Windows ARM64 can use verified x64 runtime packages as an emulation fallback when no native package exists.
- Release and CI packaging no longer copy the CLI over the case-insensitively identical `DevBox.exe` GUI path on Windows. The GUI executable now keeps its embedded application icon and starts normally after installation, while the CLI is packaged separately as `cli\devbox.exe`.
- Sites pinned to a specific PHP version now ensure their dedicated FastCGI pool is running before opening over either HTTP or HTTPS instead of always starting the global PHP service.
- Asynchronous WPF commands now contain and trace unexpected exceptions at the command boundary instead of leaking them through `async void` execution.
- Dashboard refreshes are non-reentrant, run every two seconds, and throttle expensive ADDONS health checks to a 15-second cadence.
- Site metadata validation now rejects duplicate names/domains/document roots and dedicated PHP versions that map to the same FastCGI port before accepting `sites.json`.
- Service PID markers are written atomically and include process start time, reducing PID-reuse misidentification; failed post-start registration now terminates the partially started process.
- MySQL shutdown can reuse the last successful connection credentials kept only in process memory before falling back to the standard managed-process stop path.
- Windows autostart detection now requires an exact normalized `"DevBox.exe" --startup` command instead of accepting substring matches.
- The Inno Setup fallback application version is aligned with DevBox `0.2.2`.
- Managed-service discovery in Project Manager is side-effect free and no longer rewrites `services.json` while displaying disabled services.
- Redis project profiles now map to the Windows-native Garnet executable instead of an unavailable `redis-server.exe` assumption.
- Xdebug tests comply with the xUnit single-item analyzer and no longer stop Release builds before tests execute.
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
- Corrupt or non-executable versioned `php-cgi.exe` runtimes now fail with a controlled FastCGI startup error and release the failed process object.
- ADDONS installation keeps the previous version until configuration succeeds and rolls back on configuration failure.
- ADDONS checksum verification consistently accepts valid SHA-256 values with surrounding whitespace.
- ADDONS manifests validate required fields before duplicate-key processing and reject installation directly into the shared `www` root.
- ADDONS PHP extension requirements are normalized and validated before they can be written to `php.ini`, preventing malformed directive injection and false prerequisite failures.
- phpMyAdmin Repair now regenerates an incomplete or damaged `config.inc.php`.
- phpMyAdmin local cookie authentication permits the intentionally passwordless local MySQL root account.
- Conflicting duplicate hosts-file entries for a DevBox domain are normalized to one loopback mapping.
- Expired/replaced local TLS certificates are removed from the current-user trusted root store during rotation.
- The Windows autostart setting is synchronized with the actual `HKCU\...\Run` registration.
- Site document roots are constrained to the DevBox `www` directory.
- Invalid or unsafe `config/sites.json` metadata is quarantined to a backup instead of crashing DevBox during startup or site refresh.
- PHP extension configuration now matches only the exact `extension=` directive, so `extension_dir=` is never mistaken for a loaded module.
- PHP extension parsing recognizes inline `php.ini` comments and extension toggling collapses duplicate entries to a single canonical directive.
- PHP extension checks report the runtime as unavailable when PHP CLI cannot be started, including corrupt or non-executable runtime files.
- Default MySQL shutdown explicitly uses the local root account and a bounded connection timeout before process termination fallback.

### Security

- Self-update now requires both the published SHA-256 checksum and a valid trusted Authenticode signature before the downloaded installer can execute.
- Manual releases now require the Windows code-signing certificate; unsigned release artifacts are rejected instead of being published for an updater that will not trust them.
- Runtime and ADDONS downloads now enforce explicit byte limits, and ZIP extraction enforces entry-count/extracted-size limits while rejecting traversal, NTFS alternate data streams, symbolic-link entries and suspicious compression ratios.
- Bundled MySQL packaging verifies the vendor-published digest before extraction and records SHA-256 provenance for the packaged archive.

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

- Built-in automatic MySQL download remains disabled until the package can satisfy the same pinned SHA-256 policy as other remote runtimes. Packaged DevBox 0.2.2 releases carry MySQL directly.
- Authenticode releases require `WINDOWS_SIGNING_CERTIFICATE_BASE64` and `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` repository secrets; manual release fails closed when signing material is unavailable.