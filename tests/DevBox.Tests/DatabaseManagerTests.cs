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

    [Theory]
    [InlineData("mysql")]
    [InlineData("information_schema")]
    [InlineData("performance_schema")]
    [InlineData("sys")]
    public async Task RestoreAsync_RejectsSystemDatabaseBeforeRunningClient(string databaseName)
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-database-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new DatabaseManager(root);
            var options = new DatabaseConnectionOptions("127.0.0.1", 3306, "root", string.Empty);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.RestoreAsync(databaseName, Path.Combine(root, "missing.sql"), options));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("information_schema")]
    [InlineData("performance_schema")]
    [InlineData("sys")]
    public async Task CreateDatabaseAsync_RejectsSystemDatabaseBeforeRunningClient(string databaseName)
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-database-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new DatabaseManager(root);
            var options = new DatabaseConnectionOptions("127.0.0.1", 3306, "root", string.Empty);

            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateDatabaseAsync(databaseName, options));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ConnectionOptions_RejectInvalidPort()
    {
        var options = new DatabaseConnectionOptions(Port: 70000);
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public async Task ShutdownUsingLastSuccessfulCredentialsAsync_WithoutSuccessfulConnection_ReturnsFalse()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-database-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new DatabaseManager(root);

            var result = await manager.ShutdownUsingLastSuccessfulCredentialsAsync();

            Assert.False(result);
            Assert.Null(manager.GetLastSuccessfulConnectionOptions());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DatabaseConnectionOptions_RecordRetainsPasswordInMemory()
    {
        var options = new DatabaseConnectionOptions("127.0.0.1", 3306, "root", "secret");

        Assert.Equal("secret", options.Password);
    }
}
