using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class ProjectDatabaseProvisionerTests
{
    [Fact]
    public void IsAvailable_DetectsMariaDbAndPostgreSqlClientLayouts()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            Directory.CreateDirectory(Path.Combine(root, "runtime", "mariadb", "current", "bin"));
            File.WriteAllText(Path.Combine(root, "runtime", "mariadb", "current", "bin", "mariadb.exe"), string.Empty);
            Directory.CreateDirectory(Path.Combine(root, "runtime", "postgresql", "current", "bin"));
            File.WriteAllText(Path.Combine(root, "runtime", "postgresql", "current", "bin", "psql.exe"), string.Empty);
            File.WriteAllText(Path.Combine(root, "runtime", "postgresql", "current", "bin", "createdb.exe"), string.Empty);

            var service = new ProjectDatabaseProvisioner(root, new DatabaseManager(root));

            Assert.True(service.IsAvailable("mariadb"));
            Assert.True(service.IsAvailable("postgresql"));
            Assert.False(service.IsAvailable("mysql"));
            Assert.True(service.IsAvailable("none"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a:b", "a\\:b")]
    [InlineData("a\\b", "a\\\\b")]
    public void EscapePgPass_EscapesReservedCharacters(string value, string expected)
    {
        Assert.Equal(expected, ProjectDatabaseProvisioner.EscapePgPass(value));
    }

    [Fact]
    public void EscapePgPass_RejectsLineBreaks()
    {
        Assert.Throws<ArgumentException>(() => ProjectDatabaseProvisioner.EscapePgPass("bad\nvalue"));
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
            Directory.Delete(root, recursive: true);
    }
}
