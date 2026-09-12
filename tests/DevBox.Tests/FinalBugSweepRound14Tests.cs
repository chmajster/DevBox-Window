using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound14Tests
{
    [Fact]
    public void DiagnosticsReportsUnsafeConfigRootInsteadOfThrowing()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-diagnostics-config-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var config = Path.Combine(root, "config");
        try
        {
            if (!TryCreateDirectoryLink(config, external))
                return;

            var report = new AdvancedDiagnosticsService(root).Run();

            Assert.Contains(report.Findings, finding =>
                finding.Key.StartsWith("directory-config", StringComparison.OrdinalIgnoreCase) &&
                finding.Severity == DiagnosticSeverity.Error);
        }
        finally
        {
            TryDeleteLink(config);
            Delete(root);
            Delete(external);
        }
    }

    [Fact]
    public void DiagnosticsDoesNotTraverseReparseProjectRoot()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-diagnostics-www-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, ProjectWorkspaceService.ManifestFileName), "{}");
        var www = Path.Combine(root, "www");
        try
        {
            if (!TryCreateDirectoryLink(www, external))
                return;

            var report = new AdvancedDiagnosticsService(root).Run();

            Assert.Contains(report.Findings, finding => finding.Key == "projects-root-unsafe");
            Assert.DoesNotContain(report.Findings, finding => finding.Key.StartsWith("project-lock-invalid-", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteLink(www);
            Delete(root);
            Delete(external);
        }
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteLink(string path)
    {
        try
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(path);
        }
        catch { }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round14", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
