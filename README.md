# DevBox Windows

DevBox Windows is a native Windows local-development environment built with .NET 8 and WPF. It manages native Nginx, PHP FastCGI and MySQL processes without Docker, supports optional local services and keeps the development environment under one portable DevBox root. A standalone `devbox.exe` CLI exposes the same core runtime and project operations for automation.

Current application version: `0.2.2`.

## Implemented modules

### Dashboard and services

- Native Start / Stop / Restart for Nginx, PHP FastCGI and MySQL.
- Start All / Stop All / Restart All.
- PID, TCP port and uptime reporting.
- Port-conflict detection before startup.
- Direct process invocation with `ProcessStartInfo.ArgumentList`; no arbitrary shell-command composition.
- Managed-process termination only; DevBox does not kill unrelated processes occupying a port.
- Manifest-driven optional managed services in `config/services.json`.
- Service logs under `logs/`.
- System-tray controls for Open, Start All, Restart All, Stop All and Exit.
- Optional start with Windows, minimize-to-tray and automatic service startup.

### First Run and runtimes

- First Run Wizard reports missing environment components.
- Official DevBox `0.2.2` packaged releases include Nginx `1.31.5`, PHP FastCGI `8.5.10` NTS and MySQL `8.4.11` LTS runtime payloads.
- Bundled runtimes are stored under versioned `runtime/<runtime>/<version>` directories and First Run can activate them without network access.
- Atomic activation through `runtime/<runtime>/current`.
- PHP and Nginx retain an HTTPS download fallback with pinned SHA-256 verification, safe ZIP extraction, staging and rollback when a bundled payload is absent.
- MySQL remote installation remains disabled when its bundled payload is missing because DevBox does not accept an unpinned remote package.
- Runtime Install / Activate / Remove lifecycle.
- Release packaging records upstream URLs and calculated SHA-256 values in `runtime/bundled-runtimes.json`; PHP and Nginx archives are additionally verified against pinned source checksums before packaging.
- Optional Mailpit and Garnet runtimes use architecture-specific Windows packages with pinned SHA-256 values and the same verified runtime lifecycle.
- Portable Node.js LTS `24.19.0` is available as a versioned x64/ARM64 runtime with pinned official SHA-256 packages.

### Sites

- Create local projects with `.test` domains.
- Automatic document-root and Nginx-vhost generation.
- Safe site metadata in `config/sites.json`.
- Narrow UAC elevation only when a `.test` entry must be added to or removed from the Windows hosts file.
- Project files are retained by default when a site registration is deleted.

### Project Manager

- WPF Project Manager available from Developer Tools.
- Stack detection for Laravel, Symfony, WordPress, Composer PHP and Node projects.
- Composer `ext-*` requirement discovery.
- Create projects from built-in stack profiles or import existing source trees.
- Built-in Laravel, Symfony, WordPress and plain-PHP profiles plus persistent custom profiles in `config/project-profiles.json`.
- Versioned per-project `devbox.json` manifest for domain, project kind, PHP/Node versions, database, HTTPS, addons and services.
- Laravel and Symfony profiles pin Node.js `24.19.0`; npm presets use that exact version from `runtime/node/<version>` and do not silently fall back to another Node installation.
- Project Health checks for document roots, generated vhosts, PHP runtime/extensions, manifest and TLS files.
- Repair workflow for generated vhosts, TLS state, missing project manifest and available Composer-required PHP extensions.
- Safe predefined command presets for Composer, npm, Laravel Artisan and Symfony Console workflows; arbitrary command text is not accepted by the project command runner.
- Project provisioning combines Site registration, project manifest, MySQL/MariaDB/PostgreSQL database creation when the matching native client runtime is available, and optional managed-service registration.

### PHP and Xdebug

- Active PHP version reporting.
- `php.ini` access.
- PHP extension discovery from the active runtime.
- Enable / disable extensions with safe `php.ini` updates.
- Per-site PHP runtime selection.
- Dedicated FastCGI process and stable local port for each pinned PHP version.
- Nginx automatically routes each site to its selected PHP version; sites without a pin use the global PHP FastCGI service on port `9084`.
- Configured per-site PHP pools are restored when DevBox starts.
- Xdebug status and configuration for `mode`, client port and `start_with_request`.
- Local Xdebug DLL installation validates the Windows PE structure, optionally verifies a supplied SHA-256, copies atomically and records the installed binary checksum. DevBox intentionally does not auto-download an Xdebug DLL without a trusted pinned checksum.

### SSL

- Local certificates for valid `.test` domains.
- RSA-3072 keys and SHA-256 certificates.
- Subject Alternative Name for the target domain.
- Trust / untrust in the current-user Windows Root store; the whole application does not run as Administrator.
- Nginx HTTPS vhost generation with TLS 1.2 / 1.3.
- HTTP to HTTPS redirect for SSL-enabled sites.

### Databases

- MySQL database listing and creation.
- Database size, charset and collation metadata.
- Drop, clone and rename operations with system-database protection.
- Backup through `mysqldump`.
- Restore through the native MySQL client.
- Project provisioning providers for MySQL, MariaDB and PostgreSQL. MariaDB uses `runtime/mariadb/current/bin`; PostgreSQL uses `runtime/postgresql/current/bin`.
- MySQL/MariaDB credentials use short-lived client configuration files; PostgreSQL uses a short-lived `PGPASSFILE`. Passwords are not placed on process command lines.

### ADDONS

- Manifest-driven addon catalog stored in `config/addons.json`.
- Validation of addon keys, paths, `.test` URLs, HTTPS downloads and SHA-256 values.
- Verified installation with ZIP-slip/path-traversal protection.
- Staging, replacement and rollback.
- Addon-owned Nginx vhost lifecycle: install/repair creates it; uninstall removes it.
- Health checks for hosts mapping, config and PHP requirements.
- phpMyAdmin `5.2.3` is included as the default addon definition.

Default phpMyAdmin package:

```text
https://files.phpmyadmin.net/phpMyAdmin/5.2.3/phpMyAdmin-5.2.3-all-languages.zip
SHA-256: 2d2e13c735366d318425c78e4ee2cc8fc648d77faba3ddea2cd516e43885733f
```

### Developer Tools

- Detection of Composer, Node.js, npm and pnpm.
- Composer installer downloaded from the official Composer endpoint and checked against the published SHA-384 installer signature before execution.
- Node.js LTS installation through the exact winget package ID `OpenJS.NodeJS.LTS` for the global developer toolchain.
- Portable Node.js LTS `24.19.0` installation for deterministic per-project npm commands.
- pnpm installation through npm after Node.js is available.
- Mailpit `1.31.1` installation for Windows x64/ARM64 through pinned SHA-256 release packages; local web UI uses port `8025` and SMTP uses `1025`.
- Microsoft Garnet `2.1.7` provides the native Redis-compatible Windows service on `127.0.0.1:6379`, using pinned SHA-256 Windows ReadyToRun packages.
- Local Xdebug DLL selection and verified/recorded installation into the active PHP extension directory.

### CLI

The packaged application includes `devbox.exe`, a self-contained CLI backed by `DevBox.Core` rather than a separate implementation.

```text
devbox status [all|service]
devbox start [all|service]
devbox stop [all|service]
devbox restart [all|service]
devbox site create <name> [domain]
devbox php use <version>
devbox db create <name> [mysql|mariadb|postgresql]
devbox addon install <key>
```

`DEVBOX_ROOT` can explicitly target another portable DevBox root.

### Diagnostics, logs and updates

- Diagnostics for filesystem, configuration, runtimes and managed services.
- GUI log viewer with tail and clear operations constrained to the DevBox log root.
- Stable-release check through the repository's GitHub Releases API.
- Release URL validation is restricted to HTTPS `github.com` links and stable `vMAJOR.MINOR.PATCH` tags.
- `Install update` downloads the exact stable x64 installer and `SHA256SUMS.txt`, verifies the installer SHA-256 before execution, exits through the normal managed-service shutdown path and relaunches DevBox through the installer after the update.

## Runtime layout

Third-party runtime binaries are not committed to Git. Release builds download selected upstream archives into the CI workspace, validate the expected executable layout, verify pinned checksums where available and copy the extracted runtimes into each packaged application. Optional runtimes are downloaded only through definitions carrying a pinned SHA-256.

```text
DevBox/
  DevBox.exe
  devbox.exe
  config/
    addons.json
    appsettings.json
    project-profiles.json
    services.json
    sites.json
    nginx/
      nginx.conf
      fastcgi_params
      sites-enabled/
    php/
      php.ini
    mysql/
      my.ini
    ssl/
      sites/
  data/
    mysql/
  logs/
  tmp/
  tools/
    composer/
  runtime/
    bundled-runtimes.json
    nginx/
      current/
      1.31.5/
    php/
      current/
      8.5.10/
    mysql/
      current/
      8.4.11/
    node/
      24.19.0/
    mailpit/
      current/
      <version>/
    redis/
      current/
      <version>/
    mariadb/
      current/
      bin/
    postgresql/
      current/
      bin/
  www/
    <project>/
      devbox.json
```

`DEVBOX_ROOT` can point to a different root while developing or running a portable layout.

## Build and run

```powershell
dotnet restore DevBox.sln
dotnet build DevBox.sln --configuration Release
dotnet test DevBox.sln --configuration Release
dotnet run --project src/DevBox.App/DevBox.App.csproj
dotnet run --project src/DevBox.Cli/DevBox.Cli.csproj -- status
```

Example custom root:

```powershell
$env:DEVBOX_ROOT = 'C:\DevBox'
dotnet run --project src/DevBox.App/DevBox.App.csproj
```

## CI and security scanning

Pull requests run Windows CI with:

- restore,
- NuGet vulnerability audit,
- Release build,
- tests and coverage collection,
- self-contained `win-x64` GUI publish,
- self-contained single-file `devbox.exe` publish,
- installer compilation against the combined GUI + CLI layout.

CodeQL scans C# separately. Dependabot monitors NuGet and GitHub Actions dependencies.

## Releases

The release workflow supports three guarded paths:

1. A tag matching `v*.*.*` builds and publishes that tagged version.
2. A commit on `main` whose first line is exactly `release: vX.Y.Z` builds the current SHA, creates tag `vX.Y.Z` on that SHA and publishes the GitHub Release only after restore, vulnerability audit, build and tests succeed.
3. `workflow_dispatch` performs a packaging dry-run for the supplied version and uploads the release artifact without creating a tag or GitHub Release.

Normal pushes to `main` do not execute the release job.

A publishing run builds:

- self-contained GUI `win-x64` and `win-arm64`,
- self-contained single-file CLI `devbox.exe` for x64 and ARM64,
- bundled Nginx, PHP FastCGI and MySQL runtime payloads,
- `runtime/bundled-runtimes.json` with source and checksum metadata,
- portable ZIP archives,
- an Inno Setup per-user x64 installer,
- `SHA256SUMS.txt`,
- a GitHub Release containing the packaged artifacts.

The release workflow supports Authenticode signing of DevBox-owned binaries and the installer. Signing is enabled only when `WINDOWS_SIGNING_CERTIFICATE_BASE64` and `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` repository secrets are configured. Without those secrets, artifacts remain unsigned and SHA-256 release checksums continue to provide integrity verification.

## Architecture

- `DevBox.App` — WPF views, ViewModels, desktop dialogs, system tray and current-user desktop integration.
- `DevBox.Cli` — command-line surface backed by the shared Core service layer.
- `DevBox.Core` — runtime/process/site/project/PHP/database/SSL/addon/managed-service/update business logic.
- `DevBox.Tests` — non-destructive tests using temporary directories and mocked HTTP where applicable.

See `ARCHITECTURE.md` and `SECURITY.md` for the detailed boundaries and threat controls.
