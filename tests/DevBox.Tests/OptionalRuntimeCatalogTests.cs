using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class OptionalRuntimeCatalogTests
{
    [Fact]
    public void MailpitDefinitionUsesPinnedHttpsPackage()
    {
        var definition = new OptionalRuntimeCatalog().GetMailpit();

        Assert.Equal("mailpit", definition.Key);
        Assert.Equal("1.31.1", definition.Version);
        Assert.True(definition.HasRemotePackage);
        Assert.StartsWith("https://github.com/axllent/mailpit/releases/download/", definition.DownloadUrl, StringComparison.Ordinal);
        Assert.Equal(64, definition.Sha256!.Length);
        Assert.Equal("mailpit.exe", definition.ExecutableRelativePath);
    }

    [Fact]
    public void RedisDefinitionUsesPinnedGarnetWindowsPackage()
    {
        var definition = new OptionalRuntimeCatalog().GetRedisCompatibleServer();

        Assert.Equal("redis", definition.Key);
        Assert.Equal("2.1.7", definition.Version);
        Assert.True(definition.HasRemotePackage);
        Assert.StartsWith("https://github.com/microsoft/garnet/releases/download/", definition.DownloadUrl, StringComparison.Ordinal);
        Assert.Equal(64, definition.Sha256!.Length);
        Assert.Equal("GarnetServer.exe", definition.ExecutableRelativePath);
    }

    [Fact]
    public void ManagedServiceTemplatesMatchOptionalRuntimeLayout()
    {
        var mailpit = ManagedServiceCatalog.MailpitTemplate("1.31.1");
        var redis = ManagedServiceCatalog.RedisTemplate("2.1.7");

        Assert.Equal("runtime/mailpit/current/mailpit.exe", mailpit.ExecutableRelativePath);
        Assert.Equal(8025, mailpit.Port);
        Assert.Contains("1025", string.Join(' ', mailpit.Arguments));

        Assert.Equal("runtime/redis/current/GarnetServer.exe", redis.ExecutableRelativePath);
        Assert.Equal(6379, redis.Port);
        Assert.Contains("127.0.0.1", redis.Arguments);
    }
}
