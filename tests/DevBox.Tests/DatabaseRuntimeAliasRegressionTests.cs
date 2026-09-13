using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class DatabaseRuntimeAliasRegressionTests
{
    [Fact]
    public void DuplicatePostgresAliases_AreRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-db-alias-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "database-runtimes.json"), """
[
  { "engine": "postgres", "version": "16", "port": 5432 },
  { "engine": "postgresql", "version": "16", "port": 5433 }
]
""");

            using var service = new DatabaseRuntimeService(root);
            Assert.Throws<InvalidDataException>(() => service.GetInstances());
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
