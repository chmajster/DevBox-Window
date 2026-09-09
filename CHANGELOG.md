# Changelog

All notable changes to DevBox Windows are documented here.

## 0.2.1 - 2026-09-09

### Added

- Official release packages now carry Nginx `1.31.5`, PHP FastCGI `8.5.10` NTS and MySQL `8.4.11` LTS runtime payloads under versioned `runtime/` directories.
- First Run can activate a bundled runtime locally without making an HTTP request.
- Release packaging records runtime source URLs and calculated SHA-256 values in `runtime/bundled-runtimes.json`; PHP and Nginx archives are also verified against pinned SHA-256 values before packaging.
- MySQL becomes an automatic First Run action when its bundled payload is present in the packaged application.

### Fixed

- Closing the First Run dialog no longer shuts down DevBox before the main dashboard is shown.
- `Continue to DevBox` now closes only the setup dialog and continues into the main window.
- First Run runtime `Install` buttons now use an explicit ancestor binding to the setup ViewModel command.
- `Install` is disabled for checks that are already ready or require manual action, including a source/development build where a bundled-only MySQL payload is absent.

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

- Built-in automatic MySQL download remains disabled until the package can satisfy the same pinned SHA-256 policy as other remote runtimes. Packaged releases from 0.2.1 onward carry the MySQL runtime directly.
- The generated Windows installer is not Authenticode code-signed.
