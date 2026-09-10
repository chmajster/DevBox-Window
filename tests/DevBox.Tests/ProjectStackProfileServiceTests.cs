using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class ProjectStackProfileServiceTests
{
    [Fact]
    public void BuiltInLaravelProfileProducesCreateRequest()
    {
        var root = TemporaryRoot();
        try
        {
            var service = new ProjectStackProfileService(root);

            var request = service.CreateRequest("laravel", "demo");

            Assert.Equal(ProjectKind.Laravel, request.Kind);
            Assert.Equal("mysql", request.DatabaseEngine);
            Assert.True(request.Https);
            Assert.Contains("redis", request.Services!);
            Assert.Contains("mailpit", request.Services!);
            Assert.Empty(request.Addons!);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void CustomProfileCanOverrideBuiltInProfile()
    {
        var root = TemporaryRoot();
        try
        {
            var service = new ProjectStackProfileService(root);
            service.SaveCustomProfile(new ProjectStackProfile(
                "laravel",
                "Laravel Custom",
                ProjectKind.Laravel,
                "8.4.0",
                "24",
                "postgresql",
                false,
                Array.Empty<string>(),
                ["redis"],
                "Team profile"));

            var profile = service.GetProfile("laravel");

            Assert.Equal("Laravel Custom", profile.DisplayName);
            Assert.Equal("8.4.0", profile.PhpVersion);
            Assert.Equal("postgresql", profile.DatabaseEngine);
            Assert.False(profile.Https);
            Assert.Equal(new[] { "redis" }, profile.Services);
            Assert.Empty(profile.Addons);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void CustomProfilePersistsAndCanBeRemoved()
    {
        var root = TemporaryRoot();
        try
        {
            var service = new ProjectStackProfileService(root);
            service.SaveCustomProfile(new ProjectStackProfile(
                "team-php",
                "Team PHP",
                ProjectKind.Php,
                null,
                null,
                "mysql",
                true,
                Array.Empty<string>(),
                Array.Empty<string>(),
                "Custom team stack"));

            var reloaded = new ProjectStackProfileService(root);
            Assert.Equal("Team PHP", reloaded.GetProfile("team-php").DisplayName);
            Assert.True(reloaded.RemoveCustomProfile("team-php"));
            Assert.Throws<KeyNotFoundException>(() => reloaded.GetProfile("team-php"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
