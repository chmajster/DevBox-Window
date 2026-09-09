using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class DeveloperToolsServiceTests
{
    [Fact]
    public void ResolveCommand_ReturnsExistingAbsoluteCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-tools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "tool.exe");
            File.WriteAllBytes(executable, []);

            var resolved = DeveloperToolsService.ResolveCommand([executable]);

            Assert.Equal(Path.GetFullPath(executable), resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveCommand_ReturnsNullWhenCandidateDoesNotExist()
    {
        var missing = Path.Combine(Path.GetTempPath(), "devbox-tools-tests", Guid.NewGuid().ToString("N"), "missing.exe");
        Assert.Null(DeveloperToolsService.ResolveCommand([missing]));
    }
}
