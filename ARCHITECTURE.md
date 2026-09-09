# Architecture

DevBox uses a layered native-desktop architecture. `DevBox.App` owns WPF presentation and narrow Windows desktop integration. `DevBox.Core` owns environment lifecycle and business rules. Runtime binaries, generated configuration and user project state remain outside compiled assemblies.

## Layers

### DevBox.App

Responsibilities:

- WPF windows and ViewModels,
- dependency-injection composition,
- dialogs and shell navigation,
- current-user startup settings,
- system-tray integration,
- requesting the narrowly scoped hosts-file elevation helper.

Views do not directly implement Nginx/PHP/MySQL lifecycle rules.

### DevBox.Core

Core services include:

- `IProcessManager` / `ProcessManager` — managed service lifecycle,
- `IRuntimeManager` / `RuntimeManager` — versioned runtime install/activate/remove,
- `RuntimeCatalog` — verified built-in runtime definitions,
- `SiteManager` — site metadata and Nginx vhosts,
- `PhpManager` — `php.ini` and PHP extensions,
- `PhpRuntimePoolManager` — per-version FastCGI processes for PHP-per-site,
- `DatabaseManager` — native MySQL administration, backup and restore,
- `LocalCertificateManager` — local `.test` certificates,
- `AddonCatalog` / `AddonInstaller` — manifest-driven optional web tools,
- `DiagnosticsService` and `LogReader`,
- `EnvironmentReadinessService`,
- `DeveloperToolsService`,
- `ApplicationUpdateService`.

### DevBox.Tests

Tests use temporary filesystem roots and mocked HTTP responses where external downloads are involved. They must not modify the real hosts file, certificate store, production databases or installed runtimes.

## Root and filesystem model

All environment paths resolve from one DevBox root. The root is either:

1. `DEVBOX_ROOT`, when explicitly set, or
2. the application directory.

Important subtrees are `runtime/`, `config/`, `data/`, `logs/`, `tmp/`, `tools/` and `www/`. Path-sensitive services normalize paths and reject operations that would escape the expected root.

## Managed service lifecycle

`IProcessManager` is the boundary for the global Nginx, PHP FastCGI and MySQL services.

A start validates the executable and configured port before launch. Arguments use `ProcessStartInfo.ArgumentList`. The process manager tracks only processes it starts.

A stop prefers a configured graceful command. Forced process-tree termination is a timeout fallback limited to the tracked process.

## Runtime lifecycle

Versioned packages are installed under:

```text
runtime/<key>/<version>/
```

The active version is materialized through:

```text
runtime/<key>/current/
```

Runtime installation is transactional at the application level:

```text
HTTPS download
  -> SHA-256 verification
  -> safe extraction
  -> validation
  -> staging
  -> replacement/activation
  -> cleanup
```

The built-in catalog deliberately omits a package when it cannot satisfy this verification policy.

## Sites and PHP per-site

`SiteManager` stores project definitions in `config/sites.json` and renders Nginx vhosts from that metadata.

Sites without a pinned PHP version use the global FastCGI endpoint:

```text
127.0.0.1:9084
```

A site pinned to a version such as `8.5.10` is routed to a stable dedicated port calculated for that validated version. `PhpRuntimePoolManager` starts the matching `runtime/php/<version>/php-cgi.exe` process and tracks it for the DevBox process lifetime.

This design allows several installed PHP versions to serve different local sites concurrently while keeping the global PHP service available for unpinned sites.

## SSL

`LocalCertificateManager` generates local certificates and optionally trusts them in the current-user Root store. `SiteManager` owns the corresponding Nginx TLS configuration. Enabling HTTPS therefore updates site metadata and regenerates the vhost rather than patching arbitrary Nginx text from the UI.

## Hosts file boundary

Normal application code uses `IHostMappingService`. Direct modification of the Windows hosts file is attempted as the current user first. If elevation is necessary, the application restarts only a constrained helper command and validates that the target domain ends in `.test`.

The main WPF process does not run elevated.

## Database boundary

`DatabaseManager` invokes native MySQL tools without shell command composition. Passwords are supplied through a short-lived client defaults file, not process arguments.

## ADDONS

Addon metadata is manifest-driven. `AddonCatalog` resolves and validates definitions; `AddonInstaller` owns download, verification, extraction, replacement, addon-specific configuration and vhost lifecycle.

`RuntimeLayout` creates only baseline environment state. It does not pretend optional addons are installed.

## Desktop lifecycle

`AppSettingsService` persists startup/tray preferences under the DevBox config root and uses only the current-user Windows startup registry key.

`TrayService` owns the notification icon and provides service lifecycle shortcuts. Closing the main window can hide it instead of terminating the application; explicit tray Exit requests application shutdown.

## Updates and releases

`ApplicationUpdateService` is intentionally a release checker, not a self-replacing executable updater. It validates stable GitHub release metadata and opens the release page for an explicit user-controlled update.

The release workflow produces self-contained x64/ARM64 artifacts, portable ZIP files, an Inno Setup installer and SHA-256 checksums.