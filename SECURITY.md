# Security

## Process execution

DevBox launches service and tool executables directly. Arguments are added with `ProcessStartInfo.ArgumentList`; application code does not compose service commands through `cmd.exe /c`.

Before starting a managed service, DevBox validates the executable and required TCP port. It tracks only processes it started. A conflicting unrelated process is reported, not terminated.

## Privileged operations

The WPF application normally runs without Administrator rights.

Windows hosts-file updates are the narrow privileged exception. When a `.test` mapping cannot be written as the current user, DevBox re-runs its own executable with one constrained helper command:

- `--hosts-ensure <domain> <address>`
- `--hosts-remove <domain>`

The elevated path rejects domains outside `.test`. The whole UI is not elevated.

Local certificate trust uses the current-user Windows Root certificate store rather than the machine-wide store.

Startup registration uses the current-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` key and does not require elevation.

## Runtime downloads

Runtime installation requires:

- HTTPS package URLs,
- a pinned SHA-256 value,
- isolated temporary download and extraction,
- ZIP path validation,
- staging before activation,
- rollback when replacement fails.

The built-in catalog contains only packages that can satisfy that policy. MySQL automatic download is intentionally not enabled rather than accepting a weaker checksum policy.

## ADDONS

Addon definitions are loaded from `config/addons.json` and validated before use. DevBox validates addon keys, paths beneath the DevBox web root, `.test` local URLs, HTTPS download URLs, archive roots and SHA-256 values.

Archive extraction resolves each ZIP entry to an absolute destination and rejects entries escaping the extraction root. Candidate installations are staged and validated before replacing an existing installation. Addon vhosts are created and removed with the addon lifecycle so stale configuration is not left behind intentionally.

phpMyAdmin receives a cryptographically random `blowfish_secret` when its configuration is generated.

## PHP per-site processes

Pinned PHP versions accept only `MAJOR.MINOR.PATCH` identifiers. Runtime paths are derived from the DevBox runtime root rather than user-provided arbitrary paths. Each version receives a stable local FastCGI port in the dedicated range `20000-49999`; startup is refused when that port is already occupied.

Only installed `php-cgi.exe` runtimes can be started through the per-site manager.

## Database credentials

MySQL credentials are not passed in command-line arguments. Database operations create a short-lived hidden `--defaults-extra-file` under the DevBox temporary root, use it for the native MySQL client operation and remove it afterward.

Backup and restore invoke `mysqldump.exe` / `mysql.exe` directly.

## Developer tools

Composer installation downloads the official Composer installer and the published installer signature separately. DevBox verifies the installer using SHA-384 before executing it through the active PHP runtime.

Node.js LTS installation uses the exact Windows Package Manager package identifier `OpenJS.NodeJS.LTS`. pnpm installation is delegated to npm after Node.js/npm are available.

## Application updates

The in-app updater performs a check only. It accepts stable release tags in `vMAJOR.MINOR.PATCH` format and validates the returned release URL as HTTPS on `github.com` before opening it.

Release builds publish SHA-256 checksums for produced artifacts. The current installer is not Authenticode code-signed, so checksums provide integrity verification but not publisher identity.

## Certificates

Local site certificates use RSA-3072, SHA-256 and a Subject Alternative Name for the target `.test` domain. Generated private keys are stored under the DevBox configuration root. These certificates are for local development only and must not be reused for public production services.

## Reporting

Do not include passwords, private keys, database dumps or other secrets in a public issue. Security reports should contain the minimum reproduction information necessary to demonstrate the problem.