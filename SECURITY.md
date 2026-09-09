# Security

## Process execution

DevBox does not build command lines through `cmd.exe /c`. Executables are launched directly and each argument is added separately with `ProcessStartInfo.ArgumentList`.

## Process termination

Port conflicts stop startup. DevBox does not automatically terminate the process that owns a conflicting port. Forced termination is limited to a process tree already started and tracked by the current DevBox process.

## Privileged operations

The current implementation does not modify the Windows hosts file or certificate store. Those future features must use a narrowly scoped elevated helper instead of running the whole UI as Administrator.

## Downloads

Runtime downloading is not implemented in this slice. When added, manifests must use HTTPS and pinned SHA-256 checksums before extraction, and extraction must reject path traversal/ZIP-slip entries.
