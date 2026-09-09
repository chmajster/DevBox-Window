# DevBox Windows

DevBox Windows is a native Windows local-development environment built with .NET 8 and WPF. It manages native Nginx, PHP FastCGI and MySQL processes without Docker and keeps the development environment under one portable DevBox root.

Current application version: `0.2.2`.

## Implemented modules

### Dashboard and services

- Native Start / Stop / Restart for Nginx, PHP FastCGI and MySQL.
- Start All / Stop All / Restart All.
- PID, TCP port and uptime reporting.
- Port-conflict detection before startup.
- Direct process invocation with `ProcessStartInfo.ArgumentList`; no `cmd.exe /c` composition.
- Managed-process termination only; DevBox does not kill unrelated processes occupying a port.
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

### Sites

- Create local projects with `.test` domains.
- Automatic document-root and Nginx-vhost generation.
- Safe site metadata in `config/sites.json`.
- Narrow UAC elevation only when a `.test` entry must be added to or removed from the Windows hosts file.
- Project files are retained by default when a site registration is deleted.

### PHP

- Active PHP version reporting.
- `php.ini` access.
- PHP extension discovery from the active runtime.
- Enable / disable extensions with safe `php.ini` updates.
- Per-site PHP runtime selection.
- Dedicated FastCGI process and stable local port for each pinned PHP version.
- Nginx automatically routes each site to its selected PHP version; sites without a pin use the global PHP FastCGI service on port `9084`.
- Configured per-site PHP pools are restored when DevBox starts.

### SSL

- Local certificates for valid `.test` domains.
- RSA-3072 keys and SHA-256 certificates.
- Subject Alternative Name for the target domain.
- Trust / untrust in the current-user Windows Root store; the whole application does not run as Administrator.
- Nginx HTTPS vhost generation with TLS 1.2 / 1.3.
- HTTP to HTTPS redirect for SSL-enabled sites.

### Databases

- MySQL database listing and creation.
- Backup through `mysqldump`.
- Restore through the native MySQL client.
- Connection passwords are not passed on the process command line. DevBox uses a short-lived client defaults file and removes it after the operation.

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
- Node.js LTS installation through the exact winget package ID `OpenJS.NodeJS.LTS`.
- pnpm installation through npm after Node.js is available.

### Diagnostics, logs and updates

- Diagnostics for filesystem, configuration, runtimes and managed services.
- GUI log viewer with tail and clear operations constrained to the DevBox log root.
- Stable-release check through the repository's GitHub Releases API.
- Release URL validation is restricted to HTTPS `github.com` links and stable `vMAJOR.MINOR.PATCH` tags.

## Runtime layout

Third-party runtime binaries are not committed to Git. Release builds download selected upstream archives into the CI workspace, validate the expected executable layout, verify pinned checksums where available and copy the extracted runtimes into each packaged application.

```text
DevBox/
  config/
    addons.json
    appsettings.json
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
  www/
```

`DEVBOX_ROOT` can point to a different root while developing or running a portable layout.

## Build and run

```powershell
dotnet restore DevBox.sln
dotnet build DevBox.sln --configuration Release
dotnet test DevBox.sln --configuration Release
dotnet run --project src/DevBox.App/DevBox.App.csproj
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
- self-contained `win-x64` publish artifact.

CodeQL scans C# separately. Dependabot monitors NuGet and GitHub Actions dependencies.

## Releases

The release workflow supports three guarded paths:

1. A tag matching `v*.*.*` builds and publishes that tagged version.
2. A commit on `main` whose first line is exactly `release: vX.Y.Z` builds the current SHA, creates tag `vX.Y.Z` on that SHA and publishes the GitHub Release only after restore, vulnerability audit, build and tests succeed.
3. `workflow_dispatch` performs a packaging dry-run for the supplied version and uploads the release artifact without creating a tag or GitHub Release.

Normal pushes to `main` do not execute the release job.

A publishing run builds:

- self-contained `win-x64`,
- self-contained `win-arm64`,
- bundled Nginx, PHP FastCGI and MySQL runtime payloads,
- `runtime/bundled-runtimes.json` with source and checksum metadata,
- portable ZIP archives,
- an Inno Setup per-user installer,
- `SHA256SUMS.txt`,
- a GitHub Release containing the packaged artifacts.

The installer is not currently code-signed. Release SHA-256 checksums provide integrity verification but are not a substitute for Authenticode publisher signing.

## Architecture

- `DevBox.App` — WPF views, ViewModels, desktop dialogs, system tray and current-user desktop integration.
- `DevBox.Core` — runtime/process/site/PHP/database/SSL/addon/update business logic.
- `DevBox.Tests` — non-destructive tests using temporary directories and mocked HTTP where applicable.

See `ARCHITECTURE.md` and `SECURITY.md` for the detailed boundaries and threat controls.
