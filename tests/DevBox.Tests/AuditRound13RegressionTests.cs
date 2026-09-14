using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using DevBox.App.Services;
using DevBox.App.ViewModels;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class AuditRound13RegressionTests
{
    [Fact]
    public void Upsert_RejectsDuplicateEnabledPortWithoutChangingCatalog()
    {
        using var root = new TemporaryRoot();
        var catalog = new ManagedServiceCatalog(root.Path);
        catalog.Upsert(Service("first", 8123));
        var path = root.File("config/services.json");
        var original = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => catalog.Upsert(Service("second", 8123)));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal("first", Assert.Single(catalog.GetManifests()).Key);
    }

    [Fact]
    public void Upsert_RejectsConflictingReplacementWithoutLosingPreviousSettings()
    {
        using var root = new TemporaryRoot();
        var catalog = new ManagedServiceCatalog(root.Path);
        catalog.Save([Service("first", 8123), Service("second", 8124)]);
        var original = File.ReadAllBytes(root.File("config/services.json"));
        Assert.Throws<InvalidDataException>(() => catalog.Upsert(Service("second", 8123)));
        Assert.Equal(original, File.ReadAllBytes(root.File("config/services.json")));
        Assert.Equal(2, catalog.GetManifests().Count);
    }

    [Fact]
    public void Upsert_AllowsDisabledPortOverlapAndValidReplacement()
    {
        using var root = new TemporaryRoot();
        var catalog = new ManagedServiceCatalog(root.Path);
        catalog.Upsert(Service("first", 8123));
        catalog.Upsert(Service("second", 8123) with { Enabled = false });
        catalog.Upsert(Service("first", 8124));
        Assert.Equal(2, catalog.GetManifests().Count);
        Assert.Equal(8124, catalog.GetManifests().Single(item => item.Key == "first").Port);
    }

    [Fact]
    public void Save_RejectsNullServiceWithControlledError()
    {
        using var root = new TemporaryRoot();
        Assert.Throws<InvalidDataException>(() => new ManagedServiceCatalog(root.Path).Save([null!]));
        Assert.False(File.Exists(root.File("config/services.json")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("\"8125\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void ManagedServices_RejectNonNumericDatabasePort(string value)
    {
        using var root = new TemporaryRoot();
        root.Write("config/database-runtimes.json", "[{\"Port\":" + value + "}]");
        Assert.Throws<InvalidDataException>(() => new ManagedServiceCatalog(root.Path).Upsert(Service("demo", 8123)));
        Assert.False(File.Exists(root.File("config/services.json")));
    }

    [Fact]
    public void PhpExtension_CommentedExampleDoesNotDisableActiveDirective()
    {
        using var root = new TemporaryRoot();
        root.Write("config/php/php.ini", "extension=mysqli\n;extension=mysqli\n");
        Assert.True(Assert.Single(new PhpManager(root.Path).GetExtensions()).Enabled);
    }

    [Theory]
    [InlineData("zend_extension=php_xdebug.dll ; enabled locally")]
    [InlineData("zend_extension=\"C:\\tools;portable\\php_xdebug.dll\" ; comment")]
    [InlineData("zend_extension=xdebug")]
    public void Xdebug_DisablesActiveDirectivesWithCommentsAndQuotedPaths(string directive)
    {
        using var root = new TemporaryRoot();
        root.Write("config/php/php.ini", directive + "\n");
        var service = new XdebugConfigurationService(root.Path);
        Assert.True(service.GetStatus().Enabled);
        Assert.False(service.Configure(new XdebugConfiguration(false)).Enabled);
        Assert.DoesNotContain(File.ReadAllLines(root.File("config/php/php.ini")),
            line => line.TrimStart().StartsWith("zend_extension=", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("zend_extension=not_xdebug.dll")]
    [InlineData("zend_extension=php_not_xdebug.dll")]
    public void Xdebug_DoesNotModifyUnrelatedModules(string directive)
    {
        using var root = new TemporaryRoot();
        root.Write("config/php/php.ini", directive + "\n");
        var service = new XdebugConfigurationService(root.Path);
        Assert.False(service.GetStatus().Enabled);
        service.Configure(new XdebugConfiguration(false));
        Assert.Contains(directive, File.ReadAllLines(root.File("config/php/php.ini")));
    }

    [Fact]
    public void Xdebug_StatusUsesLastActiveScalarDirective()
    {
        using var root = new TemporaryRoot();
        root.Write("config/php/php.ini", "xdebug.client_port=9000\nxdebug.client_port=9004\n;xdebug.client_port=9005\nxdebug.mode=debug\nxdebug.mode=profile\n");
        var status = new XdebugConfigurationService(root.Path).GetStatus();
        Assert.Equal(9004, status.ClientPort);
        Assert.Equal("profile", status.Mode);
    }

    [Theory]
    [InlineData("payload./tool.txt")]
    [InlineData("payload /tool.txt")]
    [InlineData("NUL.txt")]
    [InlineData("CON")]
    [InlineData("aux.log")]
    [InlineData("COM1.txt")]
    [InlineData("LPT9.log")]
    [InlineData("COM¹.txt")]
    [InlineData("payload/../tool.txt")]
    [InlineData("payload/./tool.txt")]
    [InlineData("payload/file?.txt")]
    public void Archive_RejectsUnsafeWindowsNamesBeforeCreatingDestination(string entryName)
    {
        using var root = new TemporaryRoot();
        var archive = CreateArchive(root, entryName, "payload");
        var destination = root.File("out");
        Assert.Throws<InvalidDataException>(() => ArchiveSafety.ExtractZipSafely(archive, destination, 1024 * 1024, 100, "test"));
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Archive_RejectsExistingReparsePathsWithoutOverwritingExternalFiles(bool linkedRoot)
    {
        using var root = new TemporaryRoot();
        root.Write("outside/tool.txt", "keep");
        var destination = root.File("out");
        if (linkedRoot)
            root.LinkDirectory(destination, root.File("outside"));
        else
        {
            Directory.CreateDirectory(destination);
            root.LinkDirectory(System.IO.Path.Combine(destination, "linked"), root.File("outside"));
        }
        var archive = CreateArchive(root, linkedRoot ? "tool.txt" : "linked/tool.txt", "overwrite");
        Assert.Throws<InvalidDataException>(() => ArchiveSafety.ExtractZipSafely(archive, destination, 1024 * 1024, 100, "test"));
        Assert.Equal("keep", File.ReadAllText(root.File("outside/tool.txt")));
    }

    [Fact]
    public void Archive_EnforcesActualDecompressedSizeEvenWhenHeadersLie()
    {
        using var root = new TemporaryRoot();
        var archive = CreateArchive(root, "large.txt", new string('x', 4096));
        var bytes = File.ReadAllBytes(archive);
        for (var index = 0; index <= bytes.Length - 46; index++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4));
            if (signature == 0x04034b50)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 22, 4), 8);
            else if (signature == 0x02014b50)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 24, 4), 8);
        }
        File.WriteAllBytes(archive, bytes);
        var destination = root.File("out");
        Assert.Throws<InvalidDataException>(() => ArchiveSafety.ExtractZipSafely(archive, destination, 64, 100, "test"));
        var output = System.IO.Path.Combine(destination, "large.txt");
        Assert.True(!File.Exists(output) || new FileInfo(output).Length <= 64);
    }

    [Fact]
    public void Archive_StillExtractsOrdinaryFilesAndDirectoryEntries()
    {
        using var root = new TemporaryRoot();
        var archivePath = root.File("normal.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("payload/");
            using var writer = new StreamWriter(archive.CreateEntry("payload/tool.txt").Open());
            writer.Write("normal");
        }
        ArchiveSafety.ExtractZipSafely(archivePath, root.File("out"), 1024, 100, "test");
        Assert.Equal("normal", File.ReadAllText(root.File("out/payload/tool.txt")));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("clear")]
    [InlineData("list")]
    public void Logs_RejectLinkedLogsDirectory(string operation)
    {
        using var root = new TemporaryRoot();
        root.Write("outside/app.log", "keep");
        root.LinkDirectory(root.File("logs"), root.File("outside"));
        var logs = new LogReader(root.Path);
        Assert.Throws<InvalidOperationException>(() =>
        {
            switch (operation)
            {
                case "read": logs.ReadTail("app.log"); break;
                case "clear": logs.Clear("app.log"); break;
                default: logs.GetAvailableLogs(); break;
            }
        });
        Assert.Equal("keep", File.ReadAllText(root.File("outside/app.log")));
    }

    [Fact]
    public void Logs_RejectLinkedLogFileAndOmitItFromListing()
    {
        using var root = new TemporaryRoot();
        root.Write("outside/app.log", "keep");
        Directory.CreateDirectory(root.File("logs"));
        root.LinkFile(root.File("logs/app.log"), root.File("outside/app.log"));
        var logs = new LogReader(root.Path);
        Assert.Throws<InvalidOperationException>(() => logs.Clear("app.log"));
        Assert.Throws<InvalidOperationException>(() => logs.ReadTail("app.log"));
        Assert.Empty(logs.GetAvailableLogs());
        Assert.Equal("keep", File.ReadAllText(root.File("outside/app.log")));
    }

    [Fact]
    public void Logs_RejectDanglingFileLinkBeforeCreatingExternalTarget()
    {
        using var root = new TemporaryRoot();
        Directory.CreateDirectory(root.File("logs"));
        Directory.CreateDirectory(root.File("outside"));
        root.LinkFile(root.File("logs/app.log"), root.File("outside/missing.log"));
        Assert.Throws<InvalidOperationException>(() => new LogReader(root.Path).Clear("app.log"));
        Assert.False(File.Exists(root.File("outside/missing.log")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Logs_ClearRejectsMissingName(string? name)
    {
        using var root = new TemporaryRoot();
        Assert.ThrowsAny<ArgumentException>(() => new LogReader(root.Path).Clear(name!));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"release\"")]
    [InlineData("{\"tag_name\":42,\"html_url\":\"https://github.com/chmajster/DevBox-Window/releases/tag/v1.1.0\"}")]
    [InlineData("{\"tag_name\":\"v1.1.0\",\"html_url\":true}")]
    public async Task Updaters_RejectMalformedReleaseShapesWithControlledErrors(string payload)
    {
        using var root = new TemporaryRoot();
        using var http = new HttpClient(new StaticHandler(payload));
        using var checker = new ApplicationUpdateService(new Version(1, 0, 0), http);
        using var installer = new ApplicationSelfUpdateService(root.Path, http);
        await Assert.ThrowsAsync<InvalidDataException>(() => checker.CheckAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.DownloadLatestInstallerAsync(new Version(1, 0, 0)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{\"name\":42,\"browser_download_url\":\"https://github.com/asset\"}")]
    [InlineData("{\"name\":\"other.txt\",\"browser_download_url\":true}")]
    public async Task SelfUpdater_RejectsMalformedAssetEntries(string asset)
    {
        using var root = new TemporaryRoot();
        var payload = "{\"tag_name\":\"v1.1.0\",\"html_url\":\"https://github.com/chmajster/DevBox-Window/releases/tag/v1.1.0\",\"assets\":[" + asset + "]}";
        using var http = new HttpClient(new StaticHandler(payload));
        using var service = new ApplicationSelfUpdateService(root.Path, http);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadLatestInstallerAsync(new Version(1, 0, 0)));
        Assert.False(Directory.Exists(root.File("tmp/updates")));
    }

    [Fact]
    public async Task UpdateUi_HandlesNetworkTimeoutInsteadOfRemainingInCheckingState()
    {
        using var http = new HttpClient(new FailingHandler(new TaskCanceledException("network timeout")));
        using var service = new ApplicationUpdateService(new Version(1, 0, 0), http);
        var dialogs = new RecordingDialogs();
        var model = new UpdateWindowViewModel(service, new NoopShell(), dialogs);
        await model.CheckAsync();
        Assert.Equal("Update check failed", model.Status);
        Assert.Equal(1, dialogs.Errors);
        Assert.False(model.UpdateAvailable);
    }

    [Fact]
    public async Task UpdateUi_ClearsStaleUpdateActionsAfterFailedRecheck()
    {
        using var http = new HttpClient(new OnceSuccessfulHandler());
        using var service = new ApplicationUpdateService(new Version(1, 0, 0), http);
        var model = new UpdateWindowViewModel(service, new NoopShell(), new RecordingDialogs());
        await model.CheckAsync();
        Assert.True(model.UpdateAvailable);
        await model.CheckAsync();
        Assert.Equal("Update check failed", model.Status);
        Assert.False(model.UpdateAvailable);
        Assert.Null(model.ReleaseUrl);
        Assert.False(model.InstallUpdateCommand.CanExecute(null));
    }

    private static ManagedServiceManifest Service(string key, int port) => new(
        ManagedServiceManifest.CurrentSchemaVersion, key, key, "runtime/test/tool.exe", [], ".", port, "1.0");

    private static string CreateArchive(TemporaryRoot root, string name, string content)
    {
        var path = root.File("fixture.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open());
        writer.Write(content);
        return path;
    }

    private sealed class TemporaryRoot : IDisposable
    {
        private readonly List<(string Path, bool Directory)> _links = [];
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devbox-round13", Guid.NewGuid().ToString("N"));
        public TemporaryRoot() => Directory.CreateDirectory(Path);
        public string File(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public void Write(string relative, string content)
        {
            var path = File(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
        }
        public void LinkDirectory(string path, string target)
        {
            Directory.CreateSymbolicLink(path, target);
            _links.Add((path, true));
        }
        public void LinkFile(string path, string target)
        {
            System.IO.File.CreateSymbolicLink(path, target);
            _links.Add((path, false));
        }
        public void Dispose()
        {
            // Remove links explicitly before recursive cleanup; never follow a target.
            foreach (var link in _links.AsEnumerable().Reverse())
            {
                if (link.Directory) Directory.Delete(link.Path);
                else System.IO.File.Delete(link.Path);
            }
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class StaticHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
    }
    private sealed class FailingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(error);
    }
    private sealed class OnceSuccessfulHandler : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++_calls > 1) return Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tag_name\":\"v1.1.0\",\"html_url\":\"https://github.com/chmajster/DevBox-Window/releases/tag/v1.1.0\"}")
            });
        }
    }
    private sealed class NoopShell : IShellService { public void Open(string target) { } }
    private sealed class RecordingDialogs : IDialogService
    {
        public int Errors { get; private set; }
        public void Error(string title, string message) => Errors++;
        public void Info(string title, string message) { }
        public void Warning(string title, string message) { }
        public bool Confirm(string title, string message) => false;
    }
}
