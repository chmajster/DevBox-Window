# DevBox Windows

DevBox Windows is a native Windows local-development environment inspired by Laragon/LaraEnv. The current implementation provides a WPF dashboard for native Nginx, PHP FastCGI and MySQL processes plus an ADDONS module. Docker is not used.

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
- phpMyAdmin 5.2.3 as the first addon under `www/phpmyadmin`.
- Automatic Install / Reinstall from the official phpMyAdmin release archive.
- Pinned SHA-256 verification before extraction.
- ZIP-slip/path-traversal protection during extraction.
- Atomic directory replacement with rollback to the previous installation on failure.
- Nginx virtual host configuration for `phpmyadmin.test` using PHP FastCGI on port 9084.
- phpMyAdmin installation detection based on its real `index.php` entry point.
- Required PHP extension metadata: `mysqli`, `mbstring`, `openssl`, `json`.
- Unit/integration tests for process management, runtime layout, addon detection, verified installation and unsafe ZIP rejection.
- Windows GitHub Actions build/test workflow.

## Runtime layout

DevBox intentionally does not commit third-party runtime binaries to Git.

```text
runtime/
  nginx/current/nginx.exe
  php/current/php-cgi.exe
  mysql/current/bin/mysqld.exe

www/
  phpmyadmin/

config/
  nginx/
    sites-enabled/
      phpmyadmin.test.conf
```

The application reports missing runtimes and addons explicitly instead of simulating them.

## ADDONS / phpMyAdmin

Open `ADDONS` and use `Install` next to phpMyAdmin. DevBox downloads the pinned official release:

```text
phpMyAdmin 5.2.3
https://files.phpmyadmin.net/phpMyAdmin/5.2.3/phpMyAdmin-5.2.3-all-languages.zip
SHA-256: 2d2e13c735366d318425c78e4ee2cc8fc648d77faba3ddea2cd516e43885733f
```

Installation flow:

1. Download the archive over HTTPS into `tmp/addons/...`.
2. Verify SHA-256.
3. Extract into an isolated temporary directory while rejecting entries that escape the extraction root.
4. Validate that `index.php` exists.
5. Replace `www/phpmyadmin` atomically, preserving the previous installation until the new one is ready.
6. Mark the addon `Installed` only after the real entry point exists.

After installation the action changes to `Reinstall`. `Open` launches `http://phpmyadmin.test`; `Folder` opens the installed files.

The generated Nginx site points `phpmyadmin.test` to `www/phpmyadmin` and forwards PHP requests to `127.0.0.1:9084`.

Note: resolving the custom `.test` hostname still requires the planned HostsManager/elevated-helper module. Until that module is implemented, the Nginx virtual host is generated but Windows hosts-file registration is not performed automatically.

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
- `DevBox.Core` — process management, runtime layout, service definitions, addon catalog and verified addon installation.
- `DevBox.Tests` — non-destructive tests using temporary paths and mocked HTTP responses.

The UI does not launch service executables itself; it delegates lifecycle operations to `IProcessManager`.

## Next product slices

The repository specification continues with Sites/.test HostsManager, PHP version switching and extensions, SSL, database tooling, runtime downloads, diagnostics and packaging. These are not represented as fake buttons in the production UI.
