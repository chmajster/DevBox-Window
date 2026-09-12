using System.Net;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalAuditRegressionTests
{
    [Theory]
    [InlineData("mariadb", "mysql")]
    [InlineData("mariadb", "information_schema")]
    [InlineData("postgresql", "postgres")]
    [InlineData("postgresql", "template0")]
    [InlineData("postgresql", "template1")]
    public async Task ProjectDatabaseProvisioner_RejectsSystemDatabaseMutationsBeforeClientLookup(string engine, string database)
    {
        var root = TemporaryRoot();
        try
        {
            var provisioner = new ProjectDatabaseProvisioner(root, new DatabaseManager(root));

            await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.EnsureDatabaseAsync(engine, database));
            await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.DropDatabaseAsync(engine, database));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void EnvironmentProfile_RejectsExecutableOutsideActionAllowList()
    {
        var root = TemporaryRoot();
        try
        {
            var profile = BaseProfile() with
            {
                Actions = [new ProjectActionDefinition("unsafe", "Unsafe", "powershell.exe", ["-Command", "whoami"], null, 30)]
            };

            Assert.Throws<InvalidDataException>(() => new EnvironmentProfileService(root).SaveCustomProfile(profile));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void EnvironmentProfile_RejectsAbsoluteActionWorkingDirectory()
    {
        var root = TemporaryRoot();
        try
        {
            var profile = BaseProfile() with
            {
                Actions = [new ProjectActionDefinition("composer-install", "Composer", "composer", ["install"], Path.GetPathRoot(root), 30)]
            };

            Assert.Throws<InvalidDataException>(() => new EnvironmentProfileService(root).SaveCustomProfile(profile));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ComposerInstaller_RejectsOversizedSignatureBeforeInstallerDownload()
    {
        var root = TemporaryRoot();
        try
        {
            var php = Path.Combine(root, "runtime", "php", "current", "php.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(php)!);
            File.WriteAllBytes(php, []);

            var handler = new OversizedSignatureHandler();
            using var http = new HttpClient(handler);
            using var tools = new DeveloperToolsService(root, http);

            await Assert.ThrowsAsync<InvalidDataException>(() => tools.InstallComposerAsync());
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static EnvironmentProfile BaseProfile() => new()
    {
        Key = "final-audit",
        DisplayName = "Final audit",
        Kind = ProjectKind.EmptyPhp,
        Database = new EnvironmentDatabasePin("none", null, null)
    };

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-final-audit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class OversizedSignatureHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var content = new ByteArrayContent(new byte[5000]);
            content.Headers.ContentLength = 5000;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }
}
