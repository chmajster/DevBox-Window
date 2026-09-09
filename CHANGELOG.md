# Changelog

All notable changes to DevBox Windows are documented here.

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
