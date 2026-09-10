using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class DatabaseManagerTests
{
    [Theory]
    [InlineData("app")]
    [InlineData("app_test_2026")]
    [InlineData("A1")]
    public void ValidateDatabaseName_AcceptsSafeNames(string value)
    {
        Assert.Equal(value, DatabaseManager.ValidateDatabaseName(value));
    }

    [Theory]
    [InlineData("app-test")]
    [InlineData("app;DROP DATABASE mysql")]
    [InlineData("../mysql")]
    [InlineData("")]
    public void ValidateDatabaseName_RejectsUnsafeNames(string value)
    {
        Assert.Throws<ArgumentException>(() => DatabaseManager.ValidateDatabaseName(value));
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("information_schema")]
    [InlineData("performance_schema")]
    [InlineData("sys")]
    public void ValidateMutableDatabaseName_ProtectsSystemDatabases(string value)
    {
        Assert.Throws<InvalidOperationException>(() => DatabaseManager.ValidateMutableDatabaseName(value));
    }

    [Fact]
    public void ValidateMutableDatabaseName_AllowsApplicationDatabase()
    {
        Assert.Equal("devbox_app", DatabaseManager.ValidateMutableDatabaseName("devbox_app"));
    }

    [Fact]
    public void ConnectionOptions_RejectInvalidPort()
    {
        var options = new DatabaseConnectionOptions(Port: 70000);
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}
