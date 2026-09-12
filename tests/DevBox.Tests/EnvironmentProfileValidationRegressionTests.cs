using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.Tests;

public sealed class EnvironmentProfileValidationRegressionTests
{
    [Fact]
    public void SaveCustomProfile_RejectsUnsupportedActionExecutable()
    {
        var root = TemporaryRoot();
        try
        {
            var service = new EnvironmentProfileService(root);
            var profile = Profile(new ProjectActionDefinition(
                "bad-tool",
                "Bad tool",
                "cmd.exe",
                ["/c", "echo bad"],
                null,
                30));

            var error = Assert.Throws<InvalidDataException>(() => service.SaveCustomProfile(profile));
            Assert.Contains("unsupported tool", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "config", "environment-profiles.json")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void SaveCustomProfile_RejectsAbsoluteActionWorkingDirectory()
    {
        var root = TemporaryRoot();
        try
        {
            var service = new EnvironmentProfileService(root);
            var profile = Profile(new ProjectActionDefinition(
                "bad-working-dir",
                "Bad working directory",
                "php",
                ["-v"],
                Path.GetFullPath(Path.GetTempPath()),
                30));

            var error = Assert.Throws<InvalidDataException>(() => service.SaveCustomProfile(profile));
            Assert.Contains("relative", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void SaveCustomProfile_RejectsTooManyActionArguments()
    {
        var root = TemporaryRoot();
        try
        {
            var service = new EnvironmentProfileService(root);
            var profile = Profile(new ProjectActionDefinition(
                "too-many-args",
                "Too many args",
                "php",
                Enumerable.Range(0, 65).Select(value => value.ToString()).ToArray(),
                null,
                30));

            Assert.Throws<InvalidDataException>(() => service.SaveCustomProfile(profile));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static EnvironmentProfile Profile(ProjectActionDefinition action) => new()
    {
        Key = "validation-regression",
        DisplayName = "Validation regression",
        Kind = ProjectKind.EmptyPhp,
        Database = new EnvironmentDatabasePin("none", null, null),
        Actions = [action]
    };

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-profile-validation-tests", Guid.NewGuid().ToString("N"));
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
}
