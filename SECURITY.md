# Security

## Process execution

DevBox does not build command lines through `cmd.exe /c`. Executables are launched directly and each argument is added separately with `ProcessStartInfo.ArgumentList`.

## Process termination

Port conflicts stop startup. DevBox does not automatically terminate the process that owns a conflicting port. Forced termination is limited to a process tree already started and tracked by the current DevBox process.

## Privileged operations

The current implementation does not modify the Windows hosts file or certificate store. Those future features must use a narrowly scoped elevated helper instead of running the whole UI as Administrator.

## Addon downloads

phpMyAdmin installation uses a pinned HTTPS release URL and a pinned SHA-256 checksum. The archive is downloaded to an isolated temporary directory and is not extracted until its checksum matches.

ZIP extraction resolves every entry to an absolute path and rejects entries outside the extraction root, preventing ZIP-slip/path-traversal writes. A candidate installation is staged and validated for its real entry point before replacing the existing addon. If replacement fails, the previous installation is restored.

## Runtime downloads

Generic runtime downloading is not implemented yet. When added, runtime manifests must follow the same HTTPS, pinned-checksum, isolated extraction and path-validation rules used by the addon installer.
