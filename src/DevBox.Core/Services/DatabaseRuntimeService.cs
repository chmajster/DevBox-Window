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
        var registrations = LoadRegistrations();
        var result = new List<DatabaseRuntimeInstance>();
        foreach (var registration in registrations)
        {
            if (!string.IsNullOrWhiteSpace(engine) && !registration.Engine.Equals(engine, StringComparison.OrdinalIgnoreCase))
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
        return result
            .OrderBy(item => item.Engine)
            .ThenByDescending(item => ParseVersion(item.Version))
            .ToArray();
    }

    public DatabaseRuntimeInstance Register(string engine, string version, int? port = null)
    {
        ThrowIfDisposed();
        var kind = ParseEngine(engine);
        ValidateVersion(version);
        var runtime = RuntimePath(NormalizeEngine(kind), version);
        var executable = ServerExecutable(kind, runtime);
        if (!File.Exists(executable))
            throw new FileNotFoundException($"{DisplayEngine(kind)} server executable was not found for version {version}.", executable);

        var registrations = LoadRegistrations().ToList();
        var normalizedEngine = NormalizeEngine(kind);
        var selectedPort = port ?? ChooseAvailablePort(kind, registrations);
        if (selectedPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Database port must be between 1 and 65535.");
        if (registrations.Any(item => item.Port == selectedPort && !(item.Engine.Equals(normalizedEngine, StringComparison.OrdinalIgnoreCase) && item.Version.Equals(version, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException($"Port {selectedPort} is already assigned to another DevBox database runtime.");

        var index = registrations.FindIndex(item => item.Engine.Equals(normalizedEngine, StringComparison.OrdinalIgnoreCase) && item.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
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
            var kind = instance.Engine;
            if (IsInitialized(kind, version))
                return ToCurrentInstance(instance);

            var dataPath = instance.DataPath;
            if (Directory.Exists(dataPath) && Directory.EnumerateFileSystemEntries(dataPath).Any())
                throw new InvalidOperationException($"Database data directory is non-empty but not recognized as initialized: {dataPath}");
            Directory.CreateDirectory(dataPath);

            var runtime = instance.RuntimePath;
            ProcessResult result;
            if (kind == DatabaseEngineKind.PostgreSql)
            {
                var initDb = Path.Combine(runtime, "bin", "initdb.exe");
                if (!File.Exists(initDb))
                    throw new FileNotFoundException("PostgreSQL initdb.exe was not found.", initDb);
                result = await RunProcessAsync(initDb, ["-D", dataPath, "-U", "postgres", "--auth=trust", "--encoding=UTF8"], runtime, null, null, cancellationToken).ConfigureAwait(false);
            }
            else if (kind == DatabaseEngineKind.MariaDb)
            {
                var installer = FirstExisting(
                    Path.Combine(runtime, "bin", "mariadb-install-db.exe"),
                    Path.Combine(runtime, "bin", "mysql_install_db.exe"));
                if (installer is not null)
                {
                    result = await RunProcessAsync(installer, [$"--basedir={runtime}", $"--datadir={dataPath}"], runtime, null, null, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var server = ServerExecutable(kind, runtime);
                    result = await RunProcessAsync(server, ["--no-defaults", "--initialize-insecure", $"--basedir={runtime}", $"--datadir={dataPath}"], runtime, null, null, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var server = ServerExecutable(kind, runtime);
                result = await RunProcessAsync(server, ["--no-defaults", "--initialize-insecure", $"--basedir={runtime}", $"--datadir={dataPath}"], runtime, null, null, cancellationToken).ConfigureAwait(false);
            }

            if (result.ExitCode != 0)
                throw new InvalidOperationException($"{DisplayEngine(kind)} initialization failed: {result.StandardError.Trim()}");
            if (!IsInitialized(kind, version))
                throw new InvalidDataException($"{DisplayEngine(kind)} initialization finished without producing the expected data directory structure.");
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
        var definition = BuildServiceDefinition(instance.Engine, instance.Version, instance.Port);
        return await _processes.StartAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServiceSnapshot> StopAsync(string engine, string version, DatabaseConnectionOptions? credentials = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var registration = GetRegistration(engine, version);
        var kind = ParseEngine(registration.Engine);
        var definition = BuildServiceDefinition(kind, registration.Version, registration.Port);

        if (credentials is not null && kind is DatabaseEngineKind.MySql or DatabaseEngineKind.MariaDb)
            await TryCredentialAwareMySqlShutdownAsync(kind, registration, credentials, cancellationToken).ConfigureAwait(false);

        return await _processes.StopAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServiceSnapshot> RestartAsync(string engine, string version, DatabaseConnectionOptions? credentials = null, CancellationToken cancellationToken = default)
    {
        await StopAsync(engine, version, credentials, cancellationToken).ConfigureAwait(false);
        return await StartAsync(engine, version, null, cancellationToken).ConfigureAwait(false);
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
        options ??= DefaultOptions(kind, registration.Port);
        var backupRoot = Path.Combine(_rootPath, "backups", "databases", registration.Engine);
        Directory.CreateDirectory(backupRoot);
        var extension = kind == DatabaseEngineKind.PostgreSql ? ".dump" : ".sql";
        var destination = string.IsNullOrWhiteSpace(destinationPath)
            ? Path.Combine(backupRoot, $"{databaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}{extension}")
            : Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (kind == DatabaseEngineKind.PostgreSql)
        {
            var executable = Path.Combine(RuntimePath(registration.Engine, version), "bin", "pg_dump.exe");
            EnsureExecutable(executable);
            var env = PasswordEnvironment(options, kind);
            var args = new List<string> { "-h", options.Host, "-p", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", options.User, "-Fc", "-f", destination, databaseName };
            var result = await RunProcessAsync(executable, args, _rootPath, env, null, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"PostgreSQL backup failed: {result.StandardError.Trim()}");
        }
        else
        {
            var runtime = RuntimePath(registration.Engine, version);
            var executable = FirstExisting(Path.Combine(runtime, "bin", "mysqldump.exe"), Path.Combine(runtime, "bin", "mariadb-dump.exe"))
                ?? throw new FileNotFoundException($"{DisplayEngine(kind)} dump client was not found.");
            var defaults = CreateMySqlDefaultsFile(options);
            try
            {
                var args = new[] { $"--defaults-extra-file={defaults}", "--single-transaction", "--routines", "--events", "--databases", databaseName };
                var result = await RunProcessAsync(executable, args, runtime, null, destination, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"{DisplayEngine(kind)} backup failed: {result.StandardError.Trim()}");
            }
            finally
            {
                TryDeleteFile(defaults);
            }
        }

        var info = new FileInfo(destination);
        return new DatabaseBackupResult(registration.Engine, databaseName, destination, info.Exists ? info.Length : 0, DateTimeOffset.UtcNow);
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
        options ??= DefaultOptions(kind, registration.Port);

        if (kind == DatabaseEngineKind.PostgreSql)
        {
            var executable = Path.Combine(RuntimePath(registration.Engine, version), "bin", "pg_restore.exe");
            EnsureExecutable(executable);
            var env = PasswordEnvironment(options, kind);
            var args = new[] { "-h", options.Host, "-p", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "-U", options.User, "--clean", "--if-exists", "--no-owner", "-d", databaseName, source };
            var result = await RunProcessAsync(executable, args, _rootPath, env, null, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"PostgreSQL restore failed: {result.StandardError.Trim()}");
            return;
        }

        var runtime = RuntimePath(registration.Engine, version);
        var executableMySql = FirstExisting(Path.Combine(runtime, "bin", "mysql.exe"), Path.Combine(runtime, "bin", "mariadb.exe"))
            ?? throw new FileNotFoundException($"{DisplayEngine(kind)} command client was not found.");
        var defaultsFile = CreateMySqlDefaultsFile(options);
        try
        {
            var result = await RunProcessAsync(executableMySql, [$"--defaults-extra-file={defaultsFile}"], runtime, null, null, cancellationToken, source).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"{DisplayEngine(kind)} restore failed: {result.StandardError.Trim()}");
        }
        finally
        {
            TryDeleteFile(defaultsFile);
        }
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
        var admin = FirstExisting(Path.Combine(runtime, "bin", "mysqladmin.exe"), Path.Combine(runtime, "bin", "mariadb-admin.exe"));
        return new ServiceDefinition(
            $"db-{engine}-{SafeServiceSegment(version)}",
            $"{DisplayEngine(kind)} {version}",
            server,
            ["--no-defaults", $"--basedir={runtime}", $"--datadir={data}", $"--port={port}", "--bind-address=127.0.0.1", "--console"],
            _rootPath,
            port,
            version,
            admin,
            admin is null ? null : ["--protocol=tcp", "--host=127.0.0.1", $"--port={port}", "--user=root", "shutdown"],
            TimeSpan.FromSeconds(12),
            log);
    }

    private async Task TryCredentialAwareMySqlShutdownAsync(DatabaseEngineKind kind, DatabaseRuntimeRegistration registration, DatabaseConnectionOptions options, CancellationToken cancellationToken)
    {
        var runtime = RuntimePath(registration.Engine, registration.Version);
        var admin = FirstExisting(Path.Combine(runtime, "bin", "mysqladmin.exe"), Path.Combine(runtime, "bin", "mariadb-admin.exe"));
        if (admin is null)
            return;
        var effective = options with { Port = registration.Port };
        var defaults = CreateMySqlDefaultsFile(effective);
        try
        {
            _ = await RunProcessAsync(admin, [$"--defaults-extra-file={defaults}", "shutdown"], runtime, null, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(defaults);
        }
    }

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
            var values = JsonSerializer.Deserialize<List<DatabaseRuntimeRegistration>>(File.ReadAllText(_registrationsPath), JsonOptions)
                ?? new List<DatabaseRuntimeRegistration>();
            foreach (var item in values)
            {
                _ = ParseEngine(item.Engine);
                ValidateVersion(item.Version);
                if (item.Port is < 1 or > 65535)
                    throw new InvalidDataException("Database runtime registration contains an invalid port.");
            }
            return values;
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
        if (registrations.Count > 0)
            SaveRegistrations(registrations);
        return registrations;
    }

    private void SaveRegistrations(IReadOnlyList<DatabaseRuntimeRegistration> registrations)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_registrationsPath)!);
        AtomicWrite(_registrationsPath, JsonSerializer.Serialize(registrations, JsonOptions));
    }

    private int ChooseAvailablePort(DatabaseEngineKind kind, IReadOnlyList<DatabaseRuntimeRegistration> registrations)
    {
        var start = kind switch
        {
            DatabaseEngineKind.MySql => 3306,
            DatabaseEngineKind.MariaDb => 3316,
            DatabaseEngineKind.PostgreSql => 5432,
            _ => 5500
        };
        var assigned = registrations.Select(item => item.Port).ToHashSet();
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet();
        for (var port = start; port <= 65535; port++)
        {
            if (!assigned.Contains(port) && !listeners.Contains(port))
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
        var definition = BuildServiceDefinition(instance.Engine, instance.Version, instance.Port);
        var snapshot = _processes.GetStatus(definition);
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

    private static Dictionary<string, string?>? PasswordEnvironment(DatabaseConnectionOptions options, DatabaseEngineKind kind)
    {
        if (kind != DatabaseEngineKind.PostgreSql || string.IsNullOrEmpty(options.Password))
            return null;
        return new Dictionary<string, string?> { ["PGPASSWORD"] = options.Password };
    }

    private string CreateMySqlDefaultsFile(DatabaseConnectionOptions options)
    {
        var directory = Path.Combine(_rootPath, "tmp", "db-auth");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"client-{Guid.NewGuid():N}.cnf");
        var builder = new StringBuilder();
        builder.AppendLine("[client]");
        builder.AppendLine($"host={EscapeIniValue(options.Host)}");
        builder.AppendLine($"port={options.Port}");
        builder.AppendLine($"user={EscapeIniValue(options.User)}");
        if (!string.IsNullOrEmpty(options.Password))
            builder.AppendLine($"password={EscapeIniValue(options.Password)}");
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string EscapeIniValue(string value)
    {
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Database credential contains unsupported control characters.");
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

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

    private static void EnsureExecutable(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Required database executable was not found.", path);
    }

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static void ValidateVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (version.Length > 64 || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || version.Contains(Path.DirectorySeparatorChar) || version.Contains(Path.AltDirectorySeparatorChar))
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
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record DatabaseRuntimeRegistration(string Engine, string Version, int Port);
    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
