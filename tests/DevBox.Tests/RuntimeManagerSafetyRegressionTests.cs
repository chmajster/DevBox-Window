using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class RuntimeManagerSafetyRegressionTests
{
    [Fact]
    public async Task RemoveAsync_RejectsPhpVersionAssignedToSite()
    {
        var root = TemporaryRoot();
        try
        {
            var runtimePath = Path.Combine(root, "runtime", "php", "8.3.0");
            Directory.CreateDirectory(runtimePath);
            File.WriteAllText(Path.Combine(runtimePath, "php-cgi.exe"), "runtime");
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            var sites = new SiteManager(root);
            _ = sites.Create("demo", "demo.test", project);
            _ = sites.Update(new SiteDefinition("demo", "demo.test", project, "php", "8.3.0", false));

            using var manager = new RuntimeManager(root, new HttpClient(new StaticHandler(Array.Empty<byte>())));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RemoveAsync("php", "8.3.0"));

            Assert.Contains("demo.test", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(runtimePath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InstallAsync_RemotePackage_ReportsMonotonicProgressThrough100()
    {
        var root = TemporaryRoot();
        try
        {
            var package = CreateArchive(("package/php.exe", "runtime"));
            var checksum = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
            using var manager = new RuntimeManager(root, new HttpClient(new StaticHandler(package)));
            var definition = new RuntimeDefinition(
                "php", "PHP", "8.4.0", "https://example.test/php.zip", checksum, "php.exe", "package");
            var values = new List<int>();
            var progress = new InlineProgress<int>(values.Add);

            await manager.InstallAsync(definition, progress);

            Assert.NotEmpty(values);
            Assert.Equal(0, values[0]);
            Assert.Equal(100, values[^1]);
            Assert.All(values, value => Assert.InRange(value, 0, 100));
            for (var index = 1; index < values.Count; index++)
                Assert.True(values[index] >= values[index - 1], $"Progress regressed from {values[index - 1]} to {values[index]}.");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static byte[] CreateArchive(params (string Path, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(file.Content);
            }
        }
        return memory.ToArray();
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-runtime-safety-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class StaticHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
