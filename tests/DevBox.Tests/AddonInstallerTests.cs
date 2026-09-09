using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class AddonInstallerTests
{
    [Fact]
    public async Task InstallAsync_VerifiedArchive_InstallsAndConfiguresPhpMyAdmin()
    {
        var root = TempRoot();
        try
        {
            var archive = CreateArchive(("package/index.php", "<?php echo 'ok';"), ("package/README.txt", "test"));
            var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var addon = Definition(root, hash);
            using var http = new HttpClient(new StaticResponseHandler(archive));
            using var installer = new AddonInstaller(root, http);

            await installer.InstallAsync(addon);

            Assert.True(File.Exists(addon.EntryPointPath));
            Assert.Equal("<?php echo 'ok';", File.ReadAllText(addon.EntryPointPath));
            var config = Path.Combine(addon.InstallPath, "config.inc.php");
            Assert.True(File.Exists(config));
            Assert.Contains("blowfish_secret", File.ReadAllText(config), StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(addon.InstallPath, "tmp")));

            var vhost = Path.Combine(root, "config", "nginx", "sites-enabled", "phpmyadmin.test.conf");
            Assert.True(File.Exists(vhost));
            Assert.Contains("server_name phpmyadmin.test", File.ReadAllText(vhost), StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InstallAsync_WrongHash_DoesNotInstallAddon()
    {
        var root = TempRoot();
        try
        {
            var archive = CreateArchive(("package/index.php", "<?php echo 'ok';"));
            var addon = Definition(root, new string('0', 64));
            using var http = new HttpClient(new StaticResponseHandler(archive));
            using var installer = new AddonInstaller(root, http);

            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(addon));
            Assert.False(File.Exists(addon.EntryPointPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InstallAsync_ZipSlipEntry_IsRejected()
    {
        var root = TempRoot();
        try
        {
            var archive = CreateArchive(("package/index.php", "ok"), ("../escape.txt", "blocked"));
            var hash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var addon = Definition(root, hash);
            using var http = new HttpClient(new StaticResponseHandler(archive));
            using var installer = new AddonInstaller(root, http);

            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(addon));
            Assert.False(File.Exists(Path.Combine(root, "tmp", "addons", "escape.txt")));
            Assert.False(File.Exists(addon.EntryPointPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UninstallAsync_RemovesAddonDirectoryAndOwnedVhostOnly()
    {
        var root = TempRoot();
        try
        {
            var addon = Definition(root, new string('0', 64));
            Directory.CreateDirectory(addon.InstallPath);
            File.WriteAllText(addon.EntryPointPath, "ok");
            var sibling = Path.Combine(root, "www", "keep.txt");
            File.WriteAllText(sibling, "keep");
            using var installer = new AddonInstaller(root, new HttpClient(new StaticResponseHandler(Array.Empty<byte>())));

            await installer.RepairAsync(addon);
            var vhost = Path.Combine(root, "config", "nginx", "sites-enabled", "phpmyadmin.test.conf");
            Assert.True(File.Exists(vhost));

            await installer.UninstallAsync(addon);

            Assert.False(Directory.Exists(addon.InstallPath));
            Assert.False(File.Exists(vhost));
            Assert.True(File.Exists(sibling));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UninstallAsync_PathOutsideWww_IsRejected()
    {
        var root = TempRoot();
        try
        {
            var outside = Path.Combine(root, "outside");
            var addon = new AddonDefinition("bad", "Bad", "test", outside, Path.Combine(outside, "index.php"), "http://bad.test", [], "1", "https://example.test/a.zip", new string('0', 64), "package");
            using var installer = new AddonInstaller(root, new HttpClient(new StaticResponseHandler(Array.Empty<byte>())));

            await Assert.ThrowsAsync<InvalidOperationException>(() => installer.UninstallAsync(addon));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static AddonDefinition Definition(string root, string sha256)
    {
        var install = Path.Combine(root, "www", "phpmyadmin");
        return new AddonDefinition(
            "phpmyadmin",
            "phpMyAdmin",
            "test",
            install,
            Path.Combine(install, "index.php"),
            "http://phpmyadmin.test",
            ["mysqli"],
            "test",
            "https://example.test/phpmyadmin.zip",
            sha256,
            "package");
    }

    private static byte[] CreateArchive(params (string Path, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(file.Content);
            }
        }

        return stream.ToArray();
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "devbox-installer-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticResponseHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
    }
}
