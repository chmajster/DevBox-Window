using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class DatabaseRuntimeService : IDisposable
{
    private readonly string _rootPath;
    private readonly string _registrationsPath;
    private readonly ProcessManager _processes = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public DatabaseRuntimeService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _registrationsPath = Path.Combine(_rootPath, "config", "database-runtimes.json");
    }

    public IReadOnlyList<DatabaseRuntimeInstance> GetInstances(string? engine = null)
    {
        ThrowIfDisposed();
        var result = new List<DatabaseRuntimeInstance>();
        foreach (var registration in LoadRegistrations())
        {
            if (!string.IsNullOrWhiteSpace(engine) && !registration.Engine.Equals(NormalizeEngine(ParseEngine(engine)), StringComparison.OrdinalIgnoreCase))
                continue;
            var kind = ParseEngine(registration.Engine);
            var definition = BuildServiceDefinition(kind, registration.Version, registration.Port);
            var snapshot = _processes.GetStatus(definition);
            result.Add(new DatabaseRuntimeInstance(
                kind,
                registration.Version,
                registration.Port,
                RuntimePath(registration.Engine, registration.Version),
                DataPath(registration.Engine, registration.Version),
                definition.LogPath ?? string.Empty,
                IsInitialized(kind, registration.Version),
                snapshot.State,
                snapshot.ProcessId));
        }
        return result.OrderBy(item => item.Engine).ThenByDescending(item => ParseVersion(item.Version)).ToArray();
    }

    public DatabaseRuntimeInstance Register(string engine, string version, int? port = null)
    {
        ThrowIfDisposed();
        var kind = ParseEngine(engine);
        ValidateVersion(version);
        var normalizedEngine = NormalizeEngine(kind);
        var runtime = RuntimePath(normalizedEngine, version);
        var executable = ServerExecutable(kind, runtime);
        if (!File.Exists(executable))
            throw new FileNotFoundException($"{DisplayEngine(kind)} server executable was not found for version {version}.", executable);

        using var registrationLock = AcquireRegistrationLock();
        var registrations = LoadRegistrations().ToList();
        var managedServicePorts = GetEnabledManagedServicePorts();
        var index = registrations.FindIndex(item => item.Engine.Equals(normalizedEngine, StringComparison.OrdinalIgnoreCase) && item.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
        var existing = index >= 0 ? registrations[index] : null;
        var selectedPort = port ?? existing?.Port ?? ChooseAvailablePort(kind, registrations, managedServicePorts);
        if (selectedPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Database port must be between 1 and 65535.");
        if (registrations.Any(item => item.Port == selectedPort && !(item.Engine.Equals(normalizedEngine, StringComparison.OrdinalIgnoreCase) && item.Version.Equals(version, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException($"Port {selectedPort} is already assigned to another DevBox database runtime.");
        if ((existing is null || existing.Port != selectedPort) && managedServicePorts.Contains(selectedPort))
            throw new InvalidOperationException($"Port {selectedPort} is already assigned to an enabled managed service.");
        if (port.HasValue && (existing is null || existing.Port != selectedPort) && IsTcpPortInUse(selectedPort))
            throw new InvalidOperationException($"Port {selectedPort} is already in use by another process.");

        var registration = new DatabaseRuntimeRegistration(normalizedEngine, version, selectedPort);
        if (index >= 0)
            registrations[index] = registration;
        else
            registrations.Add(registration);
        SaveRegistrations(registrations);

        var definition = BuildServiceDefinition(kind, version, selectedPort);
        var snapshot = _processes.GetStatus(definition);
        return new DatabaseRuntimeInstance(kind, version, selectedPort, runtime, DataPath(normalizedEngine, version), definition.LogPath ?? string.Empty, IsInitialized(kind, version), snapshot.State, snapshot.ProcessId);
    }

    public async Task<DatabaseRuntimeInstance> EnsureInitializedAsync(string engine, string version, int? port = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var instance = Register(engine, version, port);
        if (instance.Initialized)
            return instance;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var initializationLock = await CrossProcessFileLock.AcquireAsync(
                InitializationLockPath(instance.Engine, version),
                cancellationToken,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (IsInitialized(instance.Engine, version))
                return ToCurrentInstance(instance);

            var dataPath = instance.DataPath;
            if (Directory.Exists(dataPath) && Directory.EnumerateFileSystemEntries(dataPath).Any())
                throw new InvalidOperationException($"Database data directory is non-empty but not recognized as initialized: {dataPath}");
            Directory.CreateDirectory(dataPath);
            var runtime = instance.RuntimePath;
            ProcessResult result;

            if (instance.Engine == DatabaseEngineKind.PostgreSql)
            {
                var initDb = Path.Combine(runtime, "bin", "initdb.exe");
                EnsureExecutable(initDb);
                result = await RunProcessAsync(initDb, ["-D", dataPath, "-U", "postgres", "--auth=trust", "--encoding=UTF8"], runtime, null, null, cancellationToken).ConfigureAwait(false);
            }
            else if (instance.Engine == DatabaseEngineKind.MariaDb)
            {
                var installer = FirstExisting(Path.Combine(runtime, "bin", "mariadb-install-db.exe"), Path.Combine(runtime, "bin", "mysql_install_db.exe"));
                result = installer is not null
                    ? await RunProcessAsync(installer, [$"--basedir={runtime}", $"--datadir={dataPath}"], runtime, null, null, cancellationToken).ConfigureAwait(false)
                    : await RunProcessAsync(ServerExecutable(instance.Engine, runtime), ["--no-defaults", "--initialize-insecure", $"--basedir={runtime}", $"--datadir={dataPath}"], runtime, null, null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await RunProcessAsync(ServerExecutable(instance.Engine, runtime), ["--no-defaults", "--initialize-insecure", $"--basedir={runtime}", $"--datadir={dataPath}"], runtime, null, null, cancellationToken).ConfigureAwait(false);
            }

            if (result.ExitCode != 0)
                throw new InvalidOperationException($"{DisplayEngine(instance.Engine)} initialization failed: {SanitizeOutput(result.StandardError)}");
            if (!IsInitialized(instance.Engine, version))
                throw new InvalidDataException($"{DisplayEngine(instance.Engine)} initialization finished without producing the expected data directory structure.");
            return ToCurrentInstance(instance with { Initialized = true });
        }
        catch
        {
            if (Directory.Exists(instance.DataPath) && !IsInitialized(instance.Engine, instance.Version))
                TryDeleteDirectory(instance.DataPath);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServiceSnapshot> StartAsync(string engine, string version, int? port = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var instance = await EnsureInitializedAsync(engine, version, port, cancellationToken).ConfigureAwait(false);
        return await _processes.StartAsync(BuildServiceDefinition(instance.Engine, instance.Version, instance.Port), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServiceSnapshot> StopAsync(string engine, string version, DatabaseConnectionOptions? credentials = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var registration = GetRegistration(engine, version);
        var kind = ParseEngine(registration.Engine);
        var definition = BuildServiceDefinition(kind, registration.Version, registration.Port);
        if (credentials is not null && kind is DatabaseEngineKind.MySql or DatabaseEngineKind.MariaDb)
            await TryCredentialAwareMySqlShutdownAsync(registration, credentials, cancellationToken).ConfigureAwait(false);
        return await _processes.StopAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServiceSnapshot> RestartAsync(string engine, string version, DatabaseConnectionOptions? credentials = null, CancellationToken cancellationToken = default)
    {
        var registration = GetRegistration(engine, version);
        await StopAsync(engine, version, credentials, cancellationToken).ConfigureAwait(false);
        return await StartAsync(engine, version, registration.Port, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseBackupResult> BackupAsync(
        string engine,
        string version,
        string databaseName,
        DatabaseConnectionOptions? options = null,
        string? destinationPath = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateDatabaseName(databaseName);
        var registration = GetRegistration(engine, version);
        var kind = ParseEngine(registration.Engine);
        options = NormalizeOptions(options ?? DefaultOptions(kind, registration.Port), registration.Port);
        var backupRoot = Path.Combine(_rootPath, "backups", "databases", registration.Engine);
        Directory.CreateDirectory(backupRoot);
        var extension = kind == DatabaseEngineKind.PostgreSql ? ".dump" : ".sql";
        var destination = string.IsNullOrWhiteSpace(destinationPath)
            ? Path.Combine(backupRoot, $"{databaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}")
            : Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporaryDestination = destination + $".{Guid.NewGuid():N}.tmp";

        try
        {
            if (kind == DatabaseEngineKind.PostgreSql)
            {
                var executable = Path.Combine(RuntimePath(registration.Engine, version), "bin", "pg_dump.exe");
                EnsureExecutable(executable);
                var result = await RunProcessAsync(
                    executable,
                    ["-h", options.Host, "-p", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", options.User, "-Fc", "-f", temporaryDestination, databaseName],
                    _rootPath,
                    PasswordEnvironment(options, kind),
                    null,
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result, "PostgreSQL backup");
            }
            else
            {
                var runtime = RuntimePath(registration.Engine, version);
                var executable = FirstExisting(Path.Combine(runtime, "bin", "mysqldump.exe"), Path.Combine(runtime, "bin", "mariadb-dump.exe"))
                    ?? throw new FileNotFoundException($"{DisplayEngine(kind)} dump client was not found.");
                var arguments = MySqlClientArguments(options, "--single-transaction", "--routines", "--events", "--triggers", databaseName);
                var result = await RunProcessAsync(
                    executable,
                    arguments,
                    runtime,
                    MySqlPasswordEnvironment(options),
                    temporaryDestination,
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(result, $"{DisplayEngine(kind)} backup");
            }

            var temporaryInfo = new FileInfo(temporaryDestination);
            if (!temporaryInfo.Exists)
                throw new InvalidDataException("Database backup command completed without creating the backup file.");
            File.Move(temporaryDestination, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryDestination);
        }

        var info = new FileInfo(destination);
        return new DatabaseBackupResult(registration.Engine, databaseName, destination, info.Length, DateTimeOffset.UtcNow);
    }

    public async Task RestoreAsync(
        string engine,
        string version,
        string databaseName,
        string backupPath,
        DatabaseConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateDatabaseName(databaseName);
        var source = Path.GetFullPath(backupPath);
        if (!File.Exists(source))
            throw new FileNotFoundException("Database backup was not found.", source);
        var registration = GetRegistration(engine, version);
        var kind = ParseEngine(registration.Engine);
        options = NormalizeOptions(options ?? DefaultOptions(kind, registration.Port), registration.Port);

        if (kind == DatabaseEngineKind.PostgreSql)
        {
            var runtime = RuntimePath(registration.Engine, version);
            await EnsurePostgreSqlDatabaseAsync(runtime, databaseName, options, cancellationToken).ConfigureAwait(false);
            var executable = Path.Combine(runtime, "bin", "pg_restore.exe");
            EnsureExecutable(executable);
            var result = await RunProcessAsync(
                executable,
                ["-h", options.Host, "-p", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", options.User, "--clean", "--if-exists", "--no-owner", "-d", databaseName, source],
                runtime,
                PasswordEnvironment(options, kind),
                null,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, "PostgreSQL restore");
            return;
        }

        var mysqlRuntime = RuntimePath(registration.Engine, version);
        var client = FirstExisting(Path.Combine(mysqlRuntime, "bin", "mysql.exe"), Path.Combine(mysqlRuntime, "bin", "mariadb.exe"))
            ?? throw new FileNotFoundException($"{DisplayEngine(kind)} command client was not found.");
        var createSql = $"CREATE DATABASE IF NOT EXISTS `{databaseName}`";
        var environment = MySqlPasswordEnvironment(options);
        var createResult = await RunProcessAsync(client, MySqlClientArguments(options, $"--execute={createSql}"), mysqlRuntime, environment, null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(createResult, $"{DisplayEngine(kind)} target database creation");

        var restoreResult = await RunProcessAsync(client, MySqlClientArguments(options, databaseName), mysqlRuntime, environment, null, cancellationToken, source).ConfigureAwait(false);
        EnsureSuccess(restoreResult, $"{DisplayEngine(kind)} restore");
    }

    private static async Task EnsurePostgreSqlDatabaseAsync(
        string runtime,
        string databaseName,
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var psql = Path.Combine(runtime, "bin", "psql.exe");
        var createdb = Path.Combine(runtime, "bin", "createdb.exe");
        EnsureExecutable(psql);
        EnsureExecutable(createdb);
        var port = options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var environment = PasswordEnvironment(options, DatabaseEngineKind.PostgreSql);
        var exists = await RunProcessAsync(
            psql,
            ["-h", options.Host, "-p", port, "-U", options.User, "-d", "postgres", "-tA", "-c", $"SELECT 1 FROM pg_database WHERE datname = '{databaseName}';"],
            runtime,
            environment,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(exists, "PostgreSQL database existence check");
        if (exists.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("1", StringComparer.Ordinal))
            return;

        var create = await RunProcessAsync(
            createdb,
            ["-h", options.Host, "-p", port, "-U", options.User, databaseName],
            runtime,
            environment,
            null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(create, "PostgreSQL target database creation");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _processes.Dispose();
        _gate.Dispose();
    }

    private ServiceDefinition BuildServiceDefinition(DatabaseEngineKind kind, string version, int port)
    {
        var engine = NormalizeEngine(kind);
        var runtime = RuntimePath(engine, version);
        var data = DataPath(engine, version);
        var log = Path.Combine(_rootPath, "logs", $"{engine}-{version}.log");
        if (kind == DatabaseEngineKind.PostgreSql)
        {
            return new ServiceDefinition(
                $"db-{engine}-{SafeServiceSegment(version)}",
                $"PostgreSQL {version}",
                Path.Combine(runtime, "bin", "postgres.exe"),
                ["-D", data, "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-h", "127.0.0.1"],
                _rootPath,
                port,
                version,
                Path.Combine(runtime, "bin", "pg_ctl.exe"),
                ["-D", data, "stop", "-m", "fast", "-w"],
                TimeSpan.FromSeconds(15),
                log);
        }

        var server = ServerExecutable(kind, runtime);
        return new ServiceDefinition(
            $"db-{engine}-{SafeServiceSegment(version)}",
            $"{DisplayEngine(kind)} {version}",
            server,
            ["--no-defaults", $"--basedir={runtime}", $"--datadir={data}", $"--port={port}", "--bind-address=127.0.0.1", "--console"],
            _rootPath,
            port,
            version,
            StopExecutablePath: null,
            StopArguments: null,
            ShutdownTimeout: TimeSpan.FromSeconds(12),
            LogPath: log);
    }

    private async Task TryCredentialAwareMySqlShutdownAsync(DatabaseRuntimeRegistration registration, DatabaseConnectionOptions options, CancellationToken cancellationToken)
    {
        var runtime = RuntimePath(registration.Engine, registration.Version);
        var admin = FirstExisting(Path.Combine(runtime, "bin", "mysqladmin.exe"), Path.Combine(runtime, "bin", "mariadb-admin.exe"));
        if (admin is null)
            return;
        var effective = NormalizeOptions(options, registration.Port);
        _ = await RunProcessAsync(
            admin,
            MySqlClientArguments(effective, "shutdown"),
            runtime,
            MySqlPasswordEnvironment(effective),
            null,
            cancellationToken).ConfigureAwait(false);
    }

    private FileStream AcquireRegistrationLock() =>
        CrossProcessFileLock.Acquire(_registrationsPath + ".lock", TimeSpan.FromSeconds(10));

    private string InitializationLockPath(DatabaseEngineKind engine, string version) =>
        Path.Combine(_rootPath, "tmp", "locks", $"database-init-{SafeServiceSegment(NormalizeEngine(engine))}-{SafeServiceSegment(version)}.lock");

    private DatabaseRuntimeRegistration GetRegistration(string engine, string version)
    {
        var normalized = NormalizeEngine(ParseEngine(engine));
        ValidateVersion(version);
        return LoadRegistrations().FirstOrDefault(item => item.Engine.Equals(normalized, StringComparison.OrdinalIgnoreCase) && item.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Database runtime {normalized} {version} is not registered. Run database runtime register first.");
    }

    private IReadOnlyList<DatabaseRuntimeRegistration> LoadRegistrations()
    {
        if (!File.Exists(_registrationsPath))
            return DiscoverRegistrations();
        try
        {
            var values = JsonSerializer.Deserialize<List<DatabaseRuntimeRegistration?>>(File.ReadAllText(_registrationsPath), JsonOptions) ?? [];
            if (values.Any(item => item is null))
                throw new InvalidDataException("Database runtime registrations contain a null entry.");
            var materialized = values.Select(item => item!).ToArray();
            foreach (var item in materialized)
            {
                try
                {
                    _ = ParseEngine(item.Engine);
                    ValidateVersion(item.Version);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidDataException("Database runtime registration contains an invalid engine or version.", ex);
                }
                if (item.Port is < 1 or > 65535)
                    throw new InvalidDataException("Database runtime registration contains an invalid port.");
            }
            var duplicateIdentity = materialized
                .GroupBy(item => $"{item.Engine}|{item.Version}", StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateIdentity is not null)
                throw new InvalidDataException($"Database runtime registrations contain duplicate runtime '{duplicateIdentity.Key}'.");
            var duplicatePort = materialized.GroupBy(item => item.Port).FirstOrDefault(group => group.Count() > 1);
            if (duplicatePort is not null)
                throw new InvalidDataException($"Database runtime registrations contain duplicate port {duplicatePort.Key}.");
            return materialized;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("config/database-runtimes.json contains invalid JSON.", ex);
        }
    }

    private IReadOnlyList<DatabaseRuntimeRegistration> DiscoverRegistrations()
    {
        var registrations = new List<DatabaseRuntimeRegistration>();
        foreach (var engine in new[] { "mysql", "mariadb", "postgresql" })
        {
            var runtimeRoot = Path.Combine(_rootPath, "runtime", engine);
            if (!Directory.Exists(runtimeRoot))
                continue;
            foreach (var directory in Directory.GetDirectories(runtimeRoot))
            {
                var version = Path.GetFileName(directory);
                if (version.Equals("current", StringComparison.OrdinalIgnoreCase) || version.StartsWith(".", StringComparison.Ordinal))
                    continue;
                var kind = ParseEngine(engine);
                if (!File.Exists(ServerExecutable(kind, directory)))
                    continue;
                registrations.Add(new DatabaseRuntimeRegistration(engine, version, ChooseAvailablePort(kind, registrations)));
            }
        }
        return registrations;
    }

    private void SaveRegistrations(IReadOnlyList<DatabaseRuntimeRegistration> registrations)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_registrationsPath)!);
        AtomicWrite(_registrationsPath, JsonSerializer.Serialize(registrations, JsonOptions));
    }

    private static bool IsTcpPortInUse(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);

    private HashSet<int> GetEnabledManagedServicePorts() =>
        new ManagedServiceCatalog(_rootPath).GetManifests()
            .Where(item => item.Enabled)
            .Select(item => item.Port)
            .ToHashSet();

    private int ChooseAvailablePort(DatabaseEngineKind kind, IReadOnlyList<DatabaseRuntimeRegistration> registrations, IReadOnlySet<int>? additionalReservedPorts = null)
    {
        var start = kind switch
        {
            DatabaseEngineKind.MySql => 3306,
            DatabaseEngineKind.MariaDb => 3316,
            DatabaseEngineKind.PostgreSql => 5432,
            _ => 5500
        };
        var assigned = registrations.Select(item => item.Port).ToHashSet();
        var reserved = additionalReservedPorts ?? GetEnabledManagedServicePorts();
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet();
        for (var port = start; port <= 65535; port++)
        {
            if (!assigned.Contains(port) && !reserved.Contains(port) && !listeners.Contains(port))
                return port;
        }
        throw new InvalidOperationException("No available TCP port could be assigned to the database runtime.");
    }

    private bool IsInitialized(DatabaseEngineKind kind, string version)
    {
        var data = DataPath(NormalizeEngine(kind), version);
        return kind == DatabaseEngineKind.PostgreSql
            ? File.Exists(Path.Combine(data, "PG_VERSION"))
            : Directory.Exists(Path.Combine(data, "mysql")) || File.Exists(Path.Combine(data, "ibdata1"));
    }

    private DatabaseRuntimeInstance ToCurrentInstance(DatabaseRuntimeInstance instance)
    {
        var snapshot = _processes.GetStatus(BuildServiceDefinition(instance.Engine, instance.Version, instance.Port));
        return instance with { State = snapshot.State, ProcessId = snapshot.ProcessId, Initialized = IsInitialized(instance.Engine, instance.Version) };
    }

    private string RuntimePath(string engine, string version) => Path.Combine(_rootPath, "runtime", engine, version);
    private string DataPath(string engine, string version) => Path.Combine(_rootPath, "data", engine, version);

    private static string ServerExecutable(DatabaseEngineKind kind, string runtime) => kind switch
    {
        DatabaseEngineKind.PostgreSql => Path.Combine(runtime, "bin", "postgres.exe"),
        _ => Path.Combine(runtime, "bin", "mysqld.exe")
    };

    private static DatabaseEngineKind ParseEngine(string value) => value.Trim().ToLowerInvariant() switch
    {
        "mysql" => DatabaseEngineKind.MySql,
        "mariadb" => DatabaseEngineKind.MariaDb,
        "postgresql" or "postgres" => DatabaseEngineKind.PostgreSql,
        _ => throw new ArgumentException("Database engine must be mysql, mariadb or postgresql.", nameof(value))
    };

    private static string NormalizeEngine(DatabaseEngineKind kind) => kind switch
    {
        DatabaseEngineKind.MySql => "mysql",
        DatabaseEngineKind.MariaDb => "mariadb",
        DatabaseEngineKind.PostgreSql => "postgresql",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string DisplayEngine(DatabaseEngineKind kind) => kind switch
    {
        DatabaseEngineKind.MySql => "MySQL",
        DatabaseEngineKind.MariaDb => "MariaDB",
        DatabaseEngineKind.PostgreSql => "PostgreSQL",
        _ => kind.ToString()
    };

    private static DatabaseConnectionOptions DefaultOptions(DatabaseEngineKind kind, int port) => kind == DatabaseEngineKind.PostgreSql
        ? new DatabaseConnectionOptions(Host: "127.0.0.1", Port: port, User: "postgres", Password: string.Empty)
        : new DatabaseConnectionOptions(Host: "127.0.0.1", Port: port, User: "root", Password: string.Empty);

    private static DatabaseConnectionOptions NormalizeOptions(DatabaseConnectionOptions options, int registeredPort)
    {
        options.Validate();
        return options with { Port = registeredPort };
    }

    private static Dictionary<string, string?>? PasswordEnvironment(DatabaseConnectionOptions options, DatabaseEngineKind kind)
    {
        if (kind != DatabaseEngineKind.PostgreSql || string.IsNullOrEmpty(options.Password))
            return null;
        return new Dictionary<string, string?> { ["PGPASSWORD"] = options.Password };
    }

    private static IReadOnlyList<string> MySqlClientArguments(DatabaseConnectionOptions options, params string[] commandArguments)
    {
        var result = new List<string>
        {
            $"--host={options.Host}",
            $"--port={options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"--user={options.User}"
        };
        result.AddRange(commandArguments);
        return result;
    }

    private static IReadOnlyDictionary<string, string?>? MySqlPasswordEnvironment(DatabaseConnectionOptions options) =>
        string.IsNullOrEmpty(options.Password) ? null : new Dictionary<string, string?> { ["MYSQL_PWD"] = options.Password };


    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        string? stdoutFile,
        CancellationToken cancellationToken,
        string? stdinFile = null)
    {
        EnsureExecutable(executable);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinFile is not null
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment)
                startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Unable to start {Path.GetFileName(executable)}.");

        Task<string> stdoutTask;
        FileStream? outputStream = null;
        Task copyTask = Task.CompletedTask;
        if (stdoutFile is null)
        {
            stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stdoutFile)!);
            outputStream = new FileStream(stdoutFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            copyTask = process.StandardOutput.BaseStream.CopyToAsync(outputStream, cancellationToken);
            stdoutTask = Task.FromResult(string.Empty);
        }
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task inputTask = Task.CompletedTask;
        if (stdinFile is not null)
        {
            inputTask = Task.Run(async () =>
            {
                await using var input = new FileStream(stdinFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }, cancellationToken);
        }

        try
        {
            await Task.WhenAll(copyTask, inputTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            throw;
        }
        finally
        {
            if (outputStream is not null)
                await outputStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{operation} failed: {SanitizeOutput(result.StandardError)}");
    }

    private static string SanitizeOutput(string value)
    {
        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 2000 ? text : text[..2000];
    }

    private static void EnsureExecutable(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Required database executable was not found.", path);
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static void ValidateVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (version.Length > 64 || version is "." or ".." || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.Contains(Path.DirectorySeparatorChar) || version.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Database runtime version contains unsupported characters.", nameof(version));
    }

    private static void ValidateDatabaseName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 64 || !value.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-'))
            throw new ArgumentException("Database name contains unsupported characters.", nameof(value));
    }

    private static string SafeServiceSegment(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
    private static Version ParseVersion(string value) => Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0);

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, content);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record DatabaseRuntimeRegistration(string Engine, string Version, int Port);
    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
