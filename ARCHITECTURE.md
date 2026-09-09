# Architecture

DevBox follows a layered desktop architecture. `DevBox.App` owns WPF presentation concerns only. `DevBox.Core` owns process lifecycle, filesystem layout and service definitions. Runtime binaries and generated user state live outside compiled assemblies.

## Process lifecycle

`IProcessManager` is the boundary between UI and Windows processes. Arguments are passed with `ProcessStartInfo.ArgumentList`, not concatenated into a shell command. A service start validates the executable and required TCP port before launch. The manager tracks only processes it started and never kills an unrelated process merely because a port is occupied.

A stop first executes a configured graceful command when one exists. If the managed process does not exit inside the service timeout, only that tracked process tree is killed.

## Runtime root

All paths are resolved from a root selected by `DEVBOX_ROOT` or the application directory. The runtime/config/www/log layout is therefore relocatable as a unit.

## Planned boundaries

Future site, hosts, SSL, runtime-download and database components should be introduced behind interfaces in `DevBox.Core`; WPF must not directly edit hosts files, certificates or service configuration.
