# Changelog

All notable changes to DevBox Windows are documented here.

## Unreleased

### Added

- WPF **Environment Center** as a separate feature window/ViewModel for Profiles/Lock & Drift, Runtime Platform, database runtimes, Task Center, Advanced Diagnostics, configuration management, snapshots/transfers, Git bootstrap, WordPress Toolkit, Local CA, DPAPI Secrets and ADDONS Marketplace.
- **CLI 2.0** with `--json` support and commands for runtime lifecycle, database runtime lifecycle/backup/restore, environment profiles/lock/apply/drift/export/import, project snapshot/restore/export/import/clone/actions, diagnostics, secrets and WordPress workflows.
- Runtime Platform with side-by-side versions, install/activate/downgrade/remove, architecture-aware catalog entries, support/EOL metadata and verified local runtime ZIP imports.
- Full native MySQL, MariaDB and PostgreSQL runtime registration, initialization, start/stop/restart, per-version ports/data directories and backup/restore workflows.
- Environment Profiles plus reproducible `devbox.lock.json` desired-state files covering runtimes, database configuration, HTTPS, ADDONS, managed services and ordered Project Actions.
- Drift detection across runtime availability, database runtime/port, project manifest/Site state, ADDONS, Project Actions and HTTPS certificate material.
- Declarative Project Actions restricted to approved tools and argument arrays; declaration order is preserved.
- Project snapshots with optional DB payloads, safe restore, identity rewrite and deterministic restored-backup storage.
- Safe project transfer/export/import with full Site state synchronization on overwrite.
- Portable environment profile/lock export/import that explicitly excludes credentials.
- Task Center with bounded concurrency, cancellation, progress and persisted history.
- Advanced Diagnostics for filesystem, disk space, runtimes/EOL, services/ports, Sites/TLS, lock drift, configuration and stale transaction artifacts.
- Validated Nginx/PHP/MySQL configuration editor with atomic save and backup/restore support.
- Current-user Windows DPAPI secret store.
- Shared DevBox Local Development CA with DPAPI-protected PFX password, current-user trust, per-site certificate issuance and CA rotation.
- Signed ADDONS Marketplace using HTTPS plus detached RSA-SHA256 signatures and a separate persistent local catalog.
- Git project bootstrap workflow for clone, stack detection, DevBox import/provisioning, optional environment profile application and safe bootstrap actions.
- WordPress Toolkit using SHA-256-verified WP-CLI; sensitive database/admin values are supplied via stdin instead of ordinary process arguments.
- Regression coverage for flat runtime archives, Project Action ordering, Task Center terminal states, portable environment metadata, snapshot rename/DB restore, environment-lock synchronization and marketplace revocation.
- Project Manager WPF workflow for creating, importing, diagnosing, repairing and operating local projects.
- Automatic stack detection for Laravel, Symfony, WordPress, Composer PHP and Node projects, including Composer `ext-*` requirement discovery.
- Versioned per-project `devbox.json` manifests and persistent built-in/custom stack profiles.
- Project Health and Repair checks for document roots, Nginx vhosts, PHP runtimes/extensions, manifests and local TLS files.
- Project provisioning combining Site registration, project metadata, database creation and optional managed-service declarations.
- MySQL database size/charset/collation metadata plus protected drop, clone and rename operations.
- Manifest-driven optional services in `config/services.json` integrated with the DevBox process lifecycle.
- Verified Mailpit `1.31.1`, Microsoft Garnet `2.1.7`, portable Node.js LTS `24.19.0` and local Xdebug binary workflows.
- Verified application self-update flow with stable release resolution and installer SHA-256 verification.
- Self-contained x64/ARM64 CLI packaging alongside the GUI and installer package-layout validation.
- Optional Authenticode signing of DevBox-owned binaries and installer when signing secrets are configured.
- Official DevBox 0.2.2 packages bundle Nginx `1.31.5`, PHP FastCGI `8.5.10` NTS and MySQL `8.4.11` LTS runtime payloads.
- New DevBox application icon/branding and installer maintenance options for install path, shortcuts, launch-after-install, upgrade/update, reinstall and uninstall.

### Fixed

- Flat runtime ZIP archives without `ArchiveRootDirectory` now use the extraction root while preserving traversal protection.
- `EnvironmentLockService.ApplyLockAsync` now restores the complete locked state rather than only runtimes/database/actions; empty lists also clear stale actions/addons/services metadata.
- Marketplace entries withdrawn upstream disappear after the next successful sync instead of becoming permanent local baseline entries.
- Snapshot restore now restores `database/*` payloads and rewrites `devbox.json` / `devbox.lock.json` identity when restoring under another project name.
- MySQL/MariaDB backup/restore semantics now allow a dump to be restored into the explicitly selected destination database.
- Project Actions are no longer alphabetically reordered.
- Portable environment exports no longer reject their own `containsSecrets=false` metadata while secret-like fields remain blocked.
- Late Task Center progress callbacks can no longer change `Completed`, `Failed` or `Cancelled` tasks back to `Running`.
- Project-transfer overwrite now updates complete Site domain/document-root/PHP/HTTPS state and regenerates the corresponding vhost/TLS state.
- Duplicate `DiagnosticSeverity`, configuration tuple access, nullable `PhpPort` assumptions and WPF `Button` ambiguity compilation failures were corrected.
- Sites pinned to a PHP version ensure their dedicated FastCGI pool is running before opening.
- Asynchronous WPF commands contain and trace unexpected exceptions at the command boundary.
- Dashboard refresh is non-reentrant and expensive ADDONS checks are throttled.
- Site metadata rejects duplicate names/domains/document roots and PHP-port collisions; invalid metadata is quarantined.
- Service PID markers are atomic and include process start time; failed post-start registration terminates the partial process.
- Runtime activation restores a previously running service; assigned PHP runtimes cannot be removed; transactional backup directories are ignored by discovery.
- MySQL shutdown can reuse last successful in-memory credentials before managed-process fallback.
- Managed-service discovery is side-effect free and Redis project profiles target Garnet on Windows.
- MySQL data directories initialize safely on first start and partial initialization fails closed.
- Composer/npm/pnpm Windows launcher handling, semantic runtime sorting, bulk service failure aggregation and graceful-stop fallbacks were hardened.
- ADDONS install/rollback, checksum handling, manifest/path validation, PHP-extension validation and phpMyAdmin repair/passwordless-local-root behavior were corrected.
- Hosts-file duplicate mappings, TLS certificate rotation, current-user autostart state, Site document-root boundaries and PHP extension parsing/toggling were hardened.

### Security

- Runtime and ADDONS downloads enforce byte limits; archive extraction enforces entry-count/extracted-size limits and rejects traversal, NTFS alternate data streams, symlink/reparse entries and suspicious compression ratios.
- Environment/project archive restore validates paths and limits before writing data.
- Runtime, addon and marketplace payloads use pinned checksum/signature verification where applicable.
- Project Actions do not accept arbitrary shell text.
- Database, WordPress and secret workflows keep passwords/tokens/private keys out of ordinary process command arguments, exports and routine logs.
- Shared Local CA private material is protected with current-user DPAPI and trust is scoped to the current-user certificate store.

## 0.2.1 - 2026-09-09

### Fixed

- Closing the First Run dialog no longer shuts down DevBox before the main dashboard is shown.
- `Continue to DevBox` now closes only the setup dialog and continues into the main window.
- First Run runtime `Install` buttons use an explicit ancestor binding to the setup ViewModel command.
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
- Authenticode support is implemented in the release workflow, but artifacts remain unsigned until a code-signing PFX and password are configured as repository secrets.
