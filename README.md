# DevBox Windows

DevBox Windows is a native Windows local-development environment inspired by Laragon/LaraEnv. The first implementation slice is deliberately small but functional: a WPF dashboard controls native Nginx, PHP FastCGI and MySQL processes through a shared process manager. Docker is not used.

## Current implementation

- .NET 8 + WPF desktop application.
- Central `ProcessManager` with start, stop and restart.
- Per-service PID, port and uptime reporting.
- Port-conflict detection before process startup.
- Direct process invocation through `ProcessStartInfo.ArgumentList`; no `cmd.exe /c` command composition.
- Nginx graceful stop (`nginx -s quit`) with managed-process-tree kill only as a timeout fallback.
- Standard runtime layout under `runtime/*/current`.
- Generated base configuration for Nginx, PHP and MySQL.
- Process logs under `logs/`.
- `ADDONS` section in the desktop UI.
- phpMyAdmin registered as the first addon under `www/phpmyadmin`.
- phpMyAdmin installation detection based on its real `index.php` entry point.
- phpMyAdmin metadata for local URL and required PHP extensions: `mysqli`, `mbstring`, `openssl`, `json`.
- Unit/integration tests for missing runtime handling, occupied ports, duplicate starts, runtime layout and addon detection.
- Windows GitHub Actions build/test workflow.

## Runtime layout

DevBox intentionally does not commit third-party runtime binaries to Git. Put runtime packages in these locations when developing the bootstrapper/downloader:

```text
runtime/
  nginx/current/nginx.exe
  php/current/php-cgi.exe
  mysql/current/bin/mysqld.exe

www/
  phpmyadmin/
```

The application reports a missing runtime or addon explicitly instead of simulating it.

## ADDONS

The `ADDONS` screen is reserved for optional development tools that run inside DevBox. phpMyAdmin is the first registered addon.

Expected phpMyAdmin entry point:

```text
www/phpmyadmin/index.php
```

The UI reports `Installed` only when that file exists. From the addon card the user can open the addon directory or, when installed, open its configured local URL.

The current slice registers and detects phpMyAdmin but does not download third-party phpMyAdmin archives automatically. Runtime/addon downloading will be implemented with checksum verification rather than an unverified download button.

## Run

```powershell
dotnet restore DevBox.sln
dotnet run --project src/DevBox.App/DevBox.App.csproj
```

By default the writable DevBox root is the application directory. Set `DEVBOX_ROOT` to point at a different portable root while developing:

```powershell
$env:DEVBOX_ROOT = 'C:\DevBox'
dotnet run --project src/DevBox.App/DevBox.App.csproj
```

## Architecture

- `DevBox.App` — WPF presentation layer.
- `DevBox.Core` — process management, runtime layout, service definitions and addon catalog.
- `DevBox.Tests` — non-destructive tests that use temporary paths and managed test processes.

The UI does not launch service executables itself; it delegates lifecycle operations to `IProcessManager`.

## Next product slices

The repository specification continues with Sites/.test hosts, PHP version switching and extensions, SSL, database tooling, verified runtime/addon downloads, diagnostics and packaging. These are not represented as fake buttons in the current production UI.
