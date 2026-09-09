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
- Unit/integration tests for missing runtime handling, occupied ports, duplicate starts and runtime layout.
- Windows GitHub Actions build/test workflow.

## Runtime layout

DevBox intentionally does not commit third-party runtime binaries to Git. Put runtime packages in these locations when developing the bootstrapper/downloader:

```text
runtime/
  nginx/current/nginx.exe
  php/current/php-cgi.exe
  mysql/current/bin/mysqld.exe
```

The application reports a missing runtime in the dashboard instead of simulating a running service.

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
- `DevBox.Core` — process management, runtime layout and service definitions.
- `DevBox.Tests` — non-destructive tests that use temporary paths and managed test processes.

The UI does not launch executables itself; it delegates all lifecycle operations to `IProcessManager`.

## Next product slices

The repository specification continues with Sites/.test hosts, PHP version switching and extensions, SSL, database tooling, runtime downloads, diagnostics and packaging. These are not represented as fake buttons in the current production UI.
