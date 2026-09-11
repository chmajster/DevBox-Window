# DevBox Windows

DevBox Windows is a native Windows local-development environment built with .NET 8 and WPF. It manages Nginx, PHP FastCGI, MySQL/MariaDB/PostgreSQL, Node.js and optional local services without Docker. The WPF application and the self-contained `cli\devbox.exe` CLI share the same `DevBox.Core` implementation.

Current application version: `0.2.3` (development; latest published release remains `0.2.2`).

## Main capabilities

### Dashboard and native services

- Start / Stop / Restart for DevBox-owned Nginx, PHP FastCGI and MySQL processes.
- Start All / Stop All / Restart All, PID/port/uptime reporting and port-conflict detection.
- System tray, start with Windows, minimize to tray and configured service startup.
- Direct process invocation with `ProcessStartInfo.ArgumentList`; no arbitrary shell-command composition.
- PID ownership and process-start-time tracking prevents unrelated processes from being killed after PID reuse.
- Manifest-driven optional managed services in `config/services.json`.

### First Run and Runtime Platform

Official DevBox `0.2.2` packages contain Nginx `1.31.5`, PHP FastCGI `8.5.10` NTS and MySQL `8.4.11` LTS. Portable Node.js LTS `24.19.0`, Mailpit `1.31.1` and Microsoft Garnet `2.1.7` are available through verified installers.

Runtime Platform supports side-by-side versioned runtimes under `runtime/<key>/<version>` with an atomic `current` activation directory:

- install, activate/downgrade and remove runtime versions,
- built-in and custom runtime catalogs,
- architecture-aware packages and runtime EOL/support metadata,
- verified HTTPS downloads with pinned SHA-256,
- verified local ZIP import,
- flat ZIPs and archives with a declared root directory,
- archive protections for traversal, NTFS ADS, reparse/symlink entries, entry count, extracted size and suspicious compression ratios.

MySQL remote installation remains disabled when its packaged payload is unavailable and no pinned remote checksum is configured.

### Sites and PHP per-site

- `.test` domains, hosts mapping and generated Nginx vhosts.
- Narrow UAC elevation only when the Windows hosts file requires it.
- Per-site PHP version selection.
- Dedicated FastCGI process and stable local port for each pinned PHP version.
- Unpinned sites use global PHP FastCGI on `127.0.0.1:9084`.
- Site metadata is validated and stored in `config/sites.json`.
- Project files are retained by default when a Site registration is removed.

### Project Manager

Project Manager detects Laravel, Symfony, WordPress, Composer PHP and Node projects and stores compatible project metadata in `devbox.json`.

It supports:

- creating projects from built-in profiles or importing existing source trees,
- Composer `ext-*` requirement discovery,
- Laravel/Symfony/WordPress/plain-PHP profiles and custom profiles,
- project health and repair,
- generated vhost/TLS repair,
- deterministic per-project PHP and Node versions,
- MySQL/MariaDB/PostgreSQL database provisioning when native clients are available,
- optional managed services,
- safe predefined Composer/npm/Artisan/Symfony commands.

### Environment Profiles and `devbox.lock.json`

Environment Profiles provide reusable stack definitions. `devbox.lock.json` is the reproducible desired-state record for:

- PHP/Nginx/Node/runtime versions,
- database engine/version/port/name,
- HTTPS,
- ADDONS,
- managed services,
- ordered Project Actions.

Applying a lock synchronizes compatible `devbox.json` metadata and the registered Site, prepares required runtimes/database runtime/ADDONS, restores the Site PHP pin and HTTPS state, synchronizes known managed-service declarations and replaces Project Actions even when the desired action list is empty.

Drift detection compares the lock against the current runtime installation, database runtime/port, project manifest/Site state, ADDONS, actions and TLS material.

Built-in environment profiles include Laravel, Symfony and WordPress full stacks plus full/minimal plain PHP profiles.

### Project Actions

Projects can define ordered allow-listed actions in `devbox.json`. DevBox executes supported tools with structured argument arrays rather than arbitrary shell text. Declaration order is preserved so dependent setup steps execute deterministically.

Portable environment shares intentionally omit Project Actions because action arguments may contain authentication material.

### Environment Center

The WPF **Environment Center** is implemented as a separate feature window and ViewModel rather than expanding `MainWindowViewModel`. It exposes:

- Profiles, Lock & Drift,
- Runtime Platform,
- MySQL/MariaDB/PostgreSQL runtime instances,
- project snapshots and transfer archives,
- Git bootstrap,
- Task Center,
- Advanced Diagnostics,
- Nginx/PHP/MySQL configuration validation and editing,
- DPAPI Secrets,
- DevBox Local CA,
- signed ADDONS Marketplace,
- WordPress Toolkit.

### Database platform

DevBox supports both project-level database administration and side-by-side native database server runtimes.

`DatabaseRuntimeService` supports MySQL, MariaDB and PostgreSQL:

- runtime registration,
- automatic data-directory initialization,
- stable per-version ports,
- start / stop / restart,
- backup / restore.

Re-registering or restarting an existing database runtime without a replacement port preserves its registered endpoint. MySQL/MariaDB dumps can be restored into an explicitly selected destination database; PostgreSQL uses custom-format `pg_dump` / `pg_restore`.

MySQL/MariaDB credentials use short-lived client configuration files; PostgreSQL credentials use environment/file mechanisms. Passwords are not placed on ordinary process command lines.

### Snapshots, clone and project transfer

- Project snapshots with optional DB backup payloads.
- Configurable `.git`, `vendor` and `node_modules` inclusion.
- Safe extraction with archive limits.
- `devbox.json` and `devbox.lock.json` identity rewrite when restoring under a new project name.
- Restored DB payloads stored under `backups/snapshot-restores/<project>/...`.
- Site domain/document-root/PHP/HTTPS synchronization after restore.
- Portable project export/import.
- Transactional overwrite import with project-directory, Site/vhost/certificate and moved-DB-backup rollback on failure.
- Project clone workflows through the shared Core/CLI platform.

### Git bootstrap

Git bootstrap can clone an HTTPS repository, detect its stack, prepare the DevBox project, apply an optional Environment Profile and execute configured safe bootstrap actions.

### WordPress Toolkit

WordPress Toolkit uses a locally imported, SHA-256-verified `wp-cli.phar`. It supports WordPress project creation and status checks. Database and administrator passwords are supplied to WP-CLI over stdin instead of ordinary command-line arguments.

### PHP and Xdebug

- Active PHP version reporting and `php.ini` access.
- PHP extension discovery and safe enable/disable operations.
- Per-site PHP runtime selection and dedicated FastCGI pools.
- Xdebug mode/client-port/start-with-request configuration.
- Local Xdebug DLL installation with Windows PE validation, optional SHA-256 verification, atomic replacement and recorded provenance checksum.

### SSL and Local CA

DevBox supports individual `.test` certificates and a shared local development CA.

The Local CA:

- uses a 4096-bit RSA key,
- stores its PFX password in the current-user DPAPI secret store,
- is trusted only in the current-user Windows Root store,
- issues bounded per-site `.test` certificates,
- can be rotated and untrusted.

Nginx HTTPS vhosts use TLS 1.2/1.3 and HTTP-to-HTTPS redirects.

### DPAPI Secrets

`SecureSecretStore` protects local values using current-user Windows DPAPI. Only protected payloads are serialized to `config/secrets.dpapi.json`.

CLI secret values are read from stdin or hidden interactive input and are not accepted as ordinary command-line values.

### ADDONS and signed Marketplace

`AddonCatalog` / `AddonInstaller` provide manifest-driven addon lifecycle with validated paths, pinned SHA-256 downloads, staging, rollback, health checks and addon-owned Nginx vhosts. phpMyAdmin `5.2.3` remains the default addon definition.

`AddonMarketplaceService` accepts a remote HTTPS catalog only after detached RSA-SHA256 signature verification. Persistent local/user entries are stored separately from synchronized marketplace state, so an item withdrawn upstream disappears after the next successful sync.

Default phpMyAdmin package:

```text
https://files.phpmyadmin.net/phpMyAdmin/5.2.3/phpMyAdmin-5.2.3-all-languages.zip
SHA-256: 2d2e13c735366d318425c78e4ee2cc8fc648d77faba3ddea2cd516e43885733f
```

### Developer Tools

- Composer detection/install with the official installer SHA-384 check.
- Global Node.js LTS through the exact winget package `OpenJS.NodeJS.LTS`.
- Portable Node.js LTS `24.19.0` for deterministic project commands.
- npm and pnpm support.
- Mailpit `1.31.1` on web port `8025` and SMTP port `1025`.
- Microsoft Garnet `2.1.7` as the native Redis-compatible service on `127.0.0.1:6379`.

### Task Center, diagnostics and configuration

Task Center provides bounded concurrency, progress, cancellation and persisted history. `Completed`, `Failed` and `Cancelled` are terminal states; delayed progress callbacks cannot reopen a finished task.

Advanced Diagnostics inspects filesystem layout, free disk space, runtime validity/EOL state, services/ports, Sites/TLS, lock drift, configuration files and stale transaction artifacts.

Configuration management can read, validate and atomically save known Nginx, PHP and MySQL configuration files. Native validators are used when available and accepted replacements retain timestamped backups under `backups/configuration`.

## CLI 2.0

The packaged application includes `cli\devbox.exe`. Windows treats `DevBox.exe` and `devbox.exe` as the same filename, so the CLI is deliberately kept under `cli\` while `DevBox.exe` remains the WPF application.

Add `--json` to supported commands for machine-readable output.

Representative commands:

```text
cli\devbox status [all|service]
cli\devbox start [all|service]
cli\devbox stop [all|service]
cli\devbox restart [all|service]

cli\devbox site create <name> [domain]
cli\devbox php use <version>

cli\devbox runtime list [key]
cli\devbox runtime install <key> <version>
cli\devbox runtime use <key> <version>
cli\devbox runtime remove <key> <version>

cli\devbox db create <name> [mysql|mariadb|postgresql]
cli\devbox db runtime list [engine]
cli\devbox db runtime register <engine> <version> [port]
cli\devbox db runtime start <engine> <version>
cli\devbox db runtime stop <engine> <version>
cli\devbox db runtime restart <engine> <version>
cli\devbox db backup <engine> <version> <database> [destination]
cli\devbox db restore <engine> <version> <database> <backup>

cli\devbox env profiles
cli\devbox env lock <project> [profile]
cli\devbox env apply <project>
cli\devbox env drift <project>
cli\devbox env export ...
cli\devbox env import ...

cli\devbox project snapshot ...
cli\devbox project restore ...
cli\devbox project export ...
cli\devbox project import ...
cli\devbox project clone ...
cli\devbox project action ...

cli\devbox diagnostics
cli\devbox secret list
cli\devbox secret set <key>
cli\devbox secret delete <key>
cli\devbox wordpress ...
```

Run `cli\devbox --help` for exact syntax. `DEVBOX_ROOT` can explicitly target another portable DevBox root.

Example:

```powershell
.\cli\devbox.exe runtime list --json
.\cli\devbox.exe diagnostics --json
```

## Runtime layout

Third-party runtime binaries are not committed to Git. Release builds fetch selected upstream archives, validate layout/checksums and package them into the portable root.

```text
DevBox/
  DevBox.exe
  cli/
    devbox.exe
  config/
    addons.json
    addons.local.json
    addon-marketplace-source.json
    appsettings.json
    database-runtimes.json
    environment-profiles.json
    project-profiles.json
    runtime-catalog.json
    services.json
    sites.json
    secrets.dpapi.json
    nginx/
    php/
    mysql/
    ssl/
      ca/
      sites/
  data/
    mysql/<version>/
    mariadb/<version>/
    postgresql/<version>/
  backups/
    configuration/
    databases/
    environment-shares/
    exports/
    projects/
    snapshot-restores/
  logs/
  tmp/
  tools/
    composer/
    wp-cli/
  runtime/
    bundled-runtimes.json
    nginx/<version>/
    php/<version>/
    mysql/<version>/
    mariadb/<version>/
    postgresql/<version>/
    node/<version>/
    mailpit/<version>/
    redis/<version>/
  www/
    <project>/
      devbox.json
      devbox.lock.json
```

## Build and run

```powershell
dotnet restore DevBox.sln
dotnet build DevBox.sln --configuration Release
dotnet test DevBox.sln --configuration Release
dotnet run --project src/DevBox.App/DevBox.App.csproj
dotnet run --project src/DevBox.Cli/DevBox.Cli.csproj -- diagnostics --json
```

Example custom root:

```powershell
$env:DEVBOX_ROOT = 'C:\DevBox'
dotnet run --project src/DevBox.App/DevBox.App.csproj
```

## CI and security scanning

Pull requests targeting `main` automatically run the reusable Windows `PR Tests` workflow. It validates:

- restore,
- NuGet vulnerability audit,
- Release build,
- tests and coverage collection,
- self-contained `win-x64` GUI publish,
- self-contained single-file `cli\devbox.exe` publish,
- protection against replacing the WPF `DevBox.exe` with the case-insensitively identical CLI filename,
- Inno Setup installer compilation.

Normal pushes to `main` do not run the PR test workflow. CodeQL runs for pull requests and on the weekly security schedule. Dependabot monitors NuGet and GitHub Actions dependencies.

Security boundaries include pinned checksums/signatures, safe archive extraction, no arbitrary project shell execution, current-user DPAPI, constrained hosts-file elevation, database/WordPress/secret values outside ordinary process arguments and stable-release installer SHA-256 verification.

## Releases

Publishing is intentionally manual. Open **Actions → Manual Release → Run workflow**, select `main` and choose the semantic version increment:

- `patch`: `0.2.2` → `0.2.3`,
- `minor`: `0.2.2` → `0.3.0`,
- `major`: `0.2.2` → `1.0.0`.

The manual workflow first invokes the same validation used by pull requests. After validation, it reads the current version from `src/DevBox.App/DevBox.App.csproj`, calculates the next version and updates application/assembly/file versions, the Inno Setup fallback version and README version marker in the release workspace.

The release job builds:

- self-contained GUI `win-x64` and `win-arm64`,
- self-contained single-file CLI `cli\devbox.exe` for x64 and ARM64,
- bundled Nginx, PHP FastCGI and MySQL runtime payloads,
- `runtime/bundled-runtimes.json`,
- portable ZIP archives,
- an Inno Setup per-user x64 installer,
- `SHA256SUMS.txt`.

Only after packaging succeeds does the workflow commit the version bump, create an annotated `vMAJOR.MINOR.PATCH` tag and atomically push both to `main`. If `main` changes while the release is building, publication stops and must be restarted from the latest `main`. Pushes and tags do not publish automatically; concurrent manual releases are serialized.

Authenticode signing is applied only when `WINDOWS_SIGNING_CERTIFICATE_BASE64` and `WINDOWS_SIGNING_CERTIFICATE_PASSWORD` are configured. Without them, artifacts remain unsigned and SHA-256 release checksums still provide integrity verification.

## Architecture

- `DevBox.App` — WPF UI, Environment Center, dialogs, tray and desktop integration.
- `DevBox.Cli` — automation surface backed by shared Core services.
- `DevBox.Core` — runtime/database/environment/project/security business logic.
- `DevBox.Tests` — non-destructive unit/regression tests using temporary roots and mocked HTTP.

See `ARCHITECTURE.md` and `SECURITY.md` for detailed boundaries and design decisions.
