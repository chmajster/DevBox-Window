# Architecture

DevBox uses a layered native-desktop architecture. `DevBox.App` owns WPF presentation and narrow Windows desktop integration, `DevBox.Cli` exposes automation-friendly commands, and `DevBox.Core` owns environment lifecycle and business rules. Both user interfaces call the same Core services; runtime binaries, generated configuration, secrets and project state remain outside compiled assemblies.

## Layers

### DevBox.App

Responsibilities:

- WPF windows and ViewModels,
- dependency-injection composition,
- dialogs and shell navigation,
- current-user startup settings and system tray,
- narrowly scoped hosts-file elevation,
- `EnvironmentCenterWindow` as the orchestration surface for the environment platform.

`EnvironmentCenterViewModel` is deliberately separate from `MainWindowViewModel`. The dashboard therefore remains focused on global service status while Environment Center coordinates profiles, locks, runtimes, DB runtimes, snapshots/transfers, Task Center, diagnostics, configuration, Local CA, secrets, ADDONS Marketplace, Git bootstrap and WordPress Toolkit.

Views do not implement runtime, database, TLS or archive lifecycle rules directly.

### DevBox.Cli

`devbox.exe` is a self-contained CLI backed by `DevBox.Core`. It provides service operations plus Runtime Platform, database-runtime lifecycle, environment profile/lock workflows, snapshots/transfers, project actions, diagnostics, secrets and WordPress operations. `--json` provides machine-readable output for supported commands.

Sensitive values are not accepted as ordinary command-line arguments where they would be exposed through process listings. Secret values and WordPress passwords are supplied through stdin/interactive input and delegated to the Core secret/process boundaries.

### DevBox.Core

Important services include:

- `IProcessManager` / `ProcessManager` — managed process lifecycle and PID ownership,
- `IRuntimeManager` / `RuntimeManager` — transactional versioned runtime install/activate/remove,
- `RuntimePlatformService` — multi-runtime catalog, side-by-side versions, EOL metadata and verified local imports,
- `DatabaseRuntimeService` — MySQL/MariaDB/PostgreSQL server registration, initialization, start/stop/restart and backup/restore,
- `EnvironmentProfileService` — built-in and user environment profiles,
- `EnvironmentLockService` — `devbox.lock.json`, desired-state application and drift detection,
- `ProjectActionService` — ordered, allow-listed declarative project actions,
- `ProjectSnapshotService` — project snapshots plus optional database payloads,
- `ProjectTransferService` — safe project export/import between DevBox installations,
- `RemoteEnvironmentService` — portable environment definitions without credentials,
- `PlatformTaskCenter` — bounded background operation queue, progress, cancellation and persisted terminal history,
- `AdvancedDiagnosticsService` — filesystem/runtime/service/site/lock/config diagnostics,
- `ConfigurationFileService` — validated Nginx/PHP/MySQL configuration editing with backups,
- `SecureSecretStore` — current-user Windows DPAPI secret storage,
- `LocalCertificateAuthorityService` — one DevBox local CA and per-site certificates,
- `AddonMarketplaceService` — signed remote ADDONS catalogs separated from persistent local catalog entries,
- `GitProjectBootstrapService` — clone/detect/provision/apply-profile bootstrap,
- `WordPressToolkitService` — verified WP-CLI integration and WordPress provisioning,
- `SiteManager`, `PhpManager`, `PhpRuntimePoolManager`, `DatabaseManager`, `AddonCatalog`, `AddonInstaller`, `DiagnosticsService`, `LogReader` and update services.

### DevBox.Tests

Tests use temporary DevBox roots and mocked HTTP responses. They must not modify the real hosts file, certificate store, production databases or installed runtimes. Environment-platform regressions cover archive roots, action ordering, terminal Task Center state, portable environment metadata, snapshot identity/database payload restore, lock synchronization and marketplace revocation.

## Root and filesystem model

All environment paths resolve from one DevBox root. The root is either `DEVBOX_ROOT`, when explicitly set, or the application directory.

Important subtrees:

```text
config/     persistent configuration, catalogs, Site metadata and encrypted secret payloads
runtime/    versioned native runtimes
data/       database server data directories
www/        project roots and project manifests
backups/    database/config/project snapshots, exports and restored snapshot DB payloads
logs/       service logs and Task Center history
tmp/        transactional staging and short-lived authentication material
tools/      tools such as Composer and WP-CLI
```

Path-sensitive services normalize candidate paths and reject traversal, reparse points or escapes outside the expected root where applicable.

## Environment source of truth

`devbox.json` remains the user-facing project manifest and keeps schema-v1 compatibility. `devbox.lock.json` captures the reproducible desired state: runtime versions, database engine/version/port/name, HTTPS, ADDONS, managed services and ordered project actions.

`EnvironmentLockService.ApplyLockAsync` synchronizes compatible `devbox.json` metadata and the registered Site, ensures required runtimes/database runtime/ADDONS, synchronizes known service declarations, updates the PHP Site pin, manages HTTPS certificate/vhost state and replaces Project Actions even when the locked list is empty.

Drift detection compares the current installation and project registration with the lock rather than merely checking that a lock file exists.

## Runtime lifecycle

Versioned packages are installed under `runtime/<key>/<version>/` and the active global version is materialized through `runtime/<key>/current/`.

Runtime installation is transactional:

```text
HTTPS download or verified local archive
  -> SHA-256 verification
  -> archive safety validation
  -> extraction/staging
  -> executable validation
  -> atomic placement/activation
  -> rollback/cleanup
```

Flat archives and archives with a declared root directory are supported. Archive extraction enforces path, entry-count, extracted-size and compression-ratio controls.

## Managed processes and Task Center

`IProcessManager` tracks only DevBox-owned processes. Startup validates the executable and port; arguments are supplied through `ProcessStartInfo.ArgumentList`. Graceful stop is preferred and managed process-tree termination is a bounded fallback.

`PlatformTaskCenter` limits parallel operations, supports cancellation and records task history. `Completed`, `Failed` and `Cancelled` are terminal states: delayed progress callbacks cannot reopen a finished operation.

## Sites and PHP per-site

`SiteManager` stores definitions in `config/sites.json` and renders Nginx vhosts from normalized metadata. Sites without a PHP pin use the global FastCGI endpoint at `127.0.0.1:9084`; pinned sites use a stable dedicated port for the selected version and `PhpRuntimePoolManager` owns that FastCGI process.

## Database runtime boundary

`DatabaseRuntimeService` manages side-by-side MySQL, MariaDB and PostgreSQL instances with per-version data directories and ports. Database backup/restore uses native vendor clients. MySQL/MariaDB dumps are database-content dumps that can be restored into an explicitly selected destination database; PostgreSQL uses custom-format dumps and `pg_restore`.

Credentials are passed through short-lived client configuration/environment mechanisms, never embedded in ordinary process arguments.

## Snapshots and transfers

`ProjectSnapshotService` archives a project with optional database backup files. Restore enforces archive limits, rewrites `devbox.json` and `devbox.lock.json` identity when restoring under another name, and places database payloads under a deterministic `backups/snapshot-restores/<project>/...` subtree.

`ProjectTransferService` exports portable project archives and safely imports them into `www/`. Overwrite import updates the complete Site registration rather than retaining stale domain, document-root, PHP or HTTPS state.

`RemoteEnvironmentService` exports profiles/locks separately from project contents. These definitions explicitly exclude credentials and may be shared between machines.

## SSL and secrets

`SecureSecretStore` protects local secret values with current-user Windows DPAPI. The serialized store contains only protected payloads.

`LocalCertificateAuthorityService` creates a long-lived DevBox development CA, stores its private-key password in DPAPI, trusts only the CA in the current-user Root store and issues bounded per-site `.test` certificates. Nginx TLS state is regenerated through Site metadata rather than arbitrary UI text mutation.

## ADDONS Marketplace

`AddonCatalog` / `AddonInstaller` own the effective local addon definitions and verified installation lifecycle. `AddonMarketplaceService` verifies detached RSA-SHA256 signatures before accepting a remote catalog.

Persistent user/local entries are stored separately from the synchronized marketplace result. A marketplace item removed upstream therefore disappears on the next successful synchronization instead of becoming a permanent local baseline.

## Configuration management

`ConfigurationFileService` exposes known Nginx, PHP and MySQL configurations, performs native-validator checks when the relevant runtime exists, falls back to structural validation where appropriate, backs up the previous configuration and writes replacements atomically.

## Hosts file boundary

Normal application code uses `IHostMappingService`. If the Windows hosts file requires elevation, DevBox restarts only a constrained helper command after validating the `.test` domain. The main WPF process does not run elevated.

## Updates and releases

Stable updates are resolved through GitHub Releases. Installer URLs and release metadata are validated, the downloaded installer is checked against `SHA256SUMS.txt`, and installation is started only after integrity verification and normal managed-service shutdown.

Release CI produces self-contained x64/ARM64 GUI and CLI artifacts, portable ZIPs, the Inno Setup installer and SHA-256 checksums. Optional Authenticode signing is applied only when signing secrets are configured.
