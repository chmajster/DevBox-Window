# DevBox Windows

DevBox Windows is a native Windows local-development environment built with .NET 8 and WPF. It manages Nginx, PHP FastCGI, MySQL/MariaDB/PostgreSQL, Node.js and optional local services without Docker. The GUI and the self-contained `devbox.exe` CLI share the same `DevBox.Core` implementation.

Current application version: `0.2.2`.

## Main capabilities

### Dashboard and native services

- Start / Stop / Restart for DevBox-owned Nginx, PHP FastCGI and MySQL processes.
- Start All / Stop All / Restart All, PID/port/uptime reporting and port-conflict detection.
- System tray, start with Windows, minimize to tray and configured service startup.
- Process arguments use `ProcessStartInfo.ArgumentList`; DevBox does not compose arbitrary shell commands.
- PID ownership/start-time tracking prevents unrelated processes from being killed after PID reuse.

### Runtime Platform

DevBox supports side-by-side versioned runtimes under `runtime/<key>/<version>` with an atomic `current` activation directory.

- install, activate/downgrade and remove runtime versions,
- built-in and custom runtime catalog,
- Windows architecture awareness,
- runtime EOL/support metadata,
- verified HTTPS downloads with pinned SHA-256,
- verified local ZIP import,
- both flat ZIPs and archives with a declared root directory,
- extraction limits for path traversal, ADS, symbolic/reparse entries, entry count, extracted bytes and suspicious compression ratios.

Official `0.2.2` packages contain Nginx `1.31.5`, PHP `8.5.10` NTS and MySQL `8.4.11` LTS. Portable Node.js LTS `24.19.0`, Mailpit `1.31.1` and Microsoft Garnet `2.1.7` are available through verified runtime installers.

### Projects, profiles and environment locks

Project Manager detects Laravel, Symfony, WordPress, Composer PHP and Node projects and stores compatible project metadata in `devbox.json`.

Environment Profiles provide reusable stack definitions. `devbox.lock.json` is the reproducible desired-state record for:

- PHP/Nginx/Node/runtime versions,
- database engine/version/port/name,
- HTTPS,
- ADDONS,
- managed services,
- ordered Project Actions.

Applying a lock synchronizes the project manifest and Site registration, prepares required runtimes/database runtime/ADDONS, restores the Site PHP pin and HTTPS state, synchronizes known managed-service declarations and replaces Project Actions. Drift detection reports differences between the lock and the current machine/project state.

Built-in profiles include full Laravel, Symfony and WordPress environments plus full/minimal plain PHP profiles.

### Project Actions

Projects can define ordered allow-listed actions in `devbox.json`, for example Composer install, npm install or framework commands. DevBox executes only supported tools and argument arrays; actions are not arbitrary shell snippets. Declaration order is preserved.

### Environment Center

The WPF **Environment Center** is a separate feature window/ViewModel and exposes the platform without inflating the main dashboard ViewModel. It provides one UI for:

- Profiles, Lock & Drift,
- Runtime Platform,
- MySQL/MariaDB/PostgreSQL runtime instances,
- project snapshots and transfer archives,
- Git bootstrap,
- Task Center,
- Advanced Diagnostics,
- Nginx/PHP/MySQL configuration validation/editing,
- DPAPI Secrets,
- DevBox Local CA,
- signed ADDONS Marketplace,
- WordPress Toolkit.

### Sites and PHP per-site

- `.test` domains and generated Nginx vhosts,
- hosts-file integration with narrow UAC elevation only when required,
- per-site PHP version selection,
- dedicated FastCGI pool/port per pinned PHP version,
- unpinned sites use global PHP FastCGI on `127.0.0.1:9084`,
- project files are retained by default when a Site registration is removed.

### Database platform

DevBox supports two database layers:

1. Project/database administration through native clients.
2. `DatabaseRuntimeService` for side-by-side MySQL, MariaDB and PostgreSQL server instances.

Database runtimes support registration, automatic data-directory initialization, per-version ports, start/stop/restart and backup/restore. MySQL/MariaDB backups can be restored into an explicitly selected destination database; PostgreSQL uses custom-format `pg_dump`/`pg_restore`.

Passwords are supplied through short-lived client files or environment mechanisms rather than ordinary process arguments.

### Snapshots, clone and transfer

- project snapshots with configurable `.git`, `vendor` and `node_modules` inclusion,
- optional database backup payloads,
- safe restore with archive limits,
- identity rewrite of `devbox.json` and `devbox.lock.json` when restored under another project name,
- restored DB payloads stored under `backups/snapshot-restores/<project>/...`,
- portable project export/import,
- overwrite import updates full Site state rather than retaining stale domain/document-root/PHP/HTTPS metadata,
- project clone workflows are exposed through the CLI/platform services.

### Git bootstrap

Git bootstrap can clone a repository, detect the project stack, prepare the DevBox project, apply an optional Environment Profile and run configured safe bootstrap actions.

### WordPress Toolkit

WordPress Toolkit uses a locally imported, SHA-256-verified `wp-cli.phar` and can create/status WordPress projects. Database and administrator passwords are supplied over stdin instead of process command-line arguments.

### SSL and Local CA

DevBox supports individual `.test` certificates and a shared local development CA. The CA:

- uses a 4096-bit RSA key,
- stores its PFX password in the current-user DPAPI secret store,
- is trusted only in the current-user Windows Root store,
- issues bounded per-site `.test` certificates,
- can be rotated and untrusted.

Nginx HTTPS vhosts use TLS 1.2/1.3 and HTTP-to-HTTPS redirects.

### Secrets

`SecureSecretStore` protects local secrets using current-user Windows DPAPI. Only encrypted payloads are serialized to `config/secrets.dpapi.json`. CLI secret values are read from stdin/hidden interactive input and are not accepted as ordinary command-line values.

### ADDONS and signed Marketplace

`AddonCatalog` / `AddonInstaller` provide manifest-driven, checksum-verified addon installation with staging, rollback, health checks and addon-owned Nginx vhosts. phpMyAdmin `5.2.3` remains the default local addon.

`AddonMarketplaceService` can synchronize an HTTPS catalog only after detached RSA-SHA256 signature verification. Persistent local/user entries live separately from synchronized marketplace state, so an item withdrawn upstream disappears on the next successful sync.

### Task Center and diagnostics

Task Center provides a bounded operation queue, progress, cancellation and persisted history. Terminal states (`Completed`, `Failed`, `Cancelled`) are monotonic; delayed progress callbacks cannot reopen a task.

Advanced Diagnostics inspects filesystem layout, free disk space, runtime validity/EOL state, service ports/processes, Sites/TLS, environment locks/drift, configuration files and stale transaction artifacts.

### Configuration management

Environment Center can read, validate and atomically save known Nginx, PHP and MySQL configuration files. Native runtime validators are used when available; validated replacements keep timestamped backups under `backups/configuration`.

## CLI 2.0

`devbox.exe` is backed by `DevBox.Core`. Add `--json` to supported commands for machine-readable output.

Representative commands:

```text
devbox status [all|service]
devbox start [all|service]
devbox stop [all|service]
devbox restart [all|service]

devbox site create <name> [domain]
devbox php use <version>

devbox runtime list [key]
devbox runtime install <key> <version>
devbox runtime use <key> <version>
devbox runtime remove <key> <version>

devbox db create <name> [mysql|mariadb|postgresql]
devbox db runtime list [engine]
devbox db runtime register <engine> <version> [port]
devbox db runtime start <engine> <version>
devbox db runtime stop <engine> <version>
devbox db runtime restart <engine> <version>
devbox db backup <engine> <version> <database> [destination]
devbox db restore <engine> <version> <database> <backup>

devbox env profiles
devbox env lock <project> [profile]
devbox env apply <project>
devbox env drift <project>
devbox env export ...
devbox env import ...

devbox project snapshot ...
devbox project restore ...
devbox project export ...
devbox project import ...
devbox project clone ...
devbox project action ...

devbox diagnostics
devbox secret list
devbox secret set <key>
devbox secret delete <key>
devbox wordpress ...
```

Run `devbox --help` for the exact syntax available in the installed build. Set `DEVBOX_ROOT` to operate on another portable DevBox root.

Example JSON automation:

```powershell
devbox runtime list --json
devbox diagnostics --json
```

## Runtime layout

```text
DevBox/
  DevBox.exe
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

## Build and test

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

Pull requests run on Windows and perform restore, NuGet vulnerability audit, Release build, tests/coverage, self-contained GUI/CLI publish, package-layout validation and Inno Setup compilation. CodeQL scans C# separately. Dependabot monitors NuGet and GitHub Actions dependencies.

Security boundaries include pinned checksums, safe archive extraction, no arbitrary project shell execution, current-user DPAPI, constrained hosts-file elevation, database credentials outside ordinary process arguments and stable-release installer SHA-256 verification.

## Releases

Release CI builds self-contained `win-x64` and `win-arm64` GUI/CLI packages, portable ZIPs, an Inno Setup installer and `SHA256SUMS.txt`. Authenticode signing is enabled only when the configured signing certificate/password repository secrets are available.

## Architecture

- `DevBox.App` — WPF UI, Environment Center, dialogs, tray and desktop integration.
- `DevBox.Cli` — automation surface backed by shared Core services.
- `DevBox.Core` — runtime/database/environment/project/security business logic.
- `DevBox.Tests` — non-destructive unit/regression tests using temporary roots and mocked HTTP.

See `ARCHITECTURE.md` and `SECURITY.md` for detailed boundaries and design decisions.
