using DevBox.App.ViewModels;
using DevBox.Core.Models;
using Xunit;

namespace DevBox.Tests;

public sealed class RuntimeRowSafetyTests
{
    [Fact]
    public void ActiveCatalogRuntime_CannotBeRemovedFromUi()
    {
        var package = new RuntimePackageEntry
        {
            Key = "php",
            DisplayName = "PHP",
            Version = "8.5.10",
            Architecture = "x64",
            ExecutableRelativePath = "php-cgi.exe"
        };
        var row = new RuntimeRowViewModel(
            new RuntimeVersionStatus(package, Installed: true, Active: true, Valid: true, RuntimeSupportState.Current),
            Path.GetTempPath());

        Assert.False(row.CanRemove);
        Assert.False(row.CanActivate);
        Assert.Equal("Active", row.Status);
    }

    [Fact]
    public void ActiveDiscoveredRuntime_CannotBeRemovedFromUi()
    {
        var row = new RuntimeRowViewModel(new RuntimeInstallation(
            "php",
            "8.4.0",
            Path.Combine(Path.GetTempPath(), "runtime", "php", "8.4.0"),
            IsActive: true,
            IsValid: true));

        Assert.False(row.CanRemove);
        Assert.False(row.CanActivate);
        Assert.Equal("Active", row.Status);
    }
}
