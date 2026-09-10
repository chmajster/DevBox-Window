using DevBox.App.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public void IsStartupCommandForExecutable_AcceptsExactGeneratedCommand()
    {
        var executable = Path.Combine(Path.GetTempPath(), "DevBox", "DevBox.exe");
        var command = AppSettingsService.BuildStartupCommand(executable);

        Assert.True(AppSettingsService.IsStartupCommandForExecutable(command, executable));
    }

    [Theory]
    [InlineData("\"C:\\Apps\\DevBox.exe\" --startup --unexpected")]
    [InlineData("cmd.exe /c \"C:\\Apps\\DevBox.exe\" --startup")]
    [InlineData("\"C:\\Apps\\DevBox.exe.bak\" --startup")]
    public void IsStartupCommandForExecutable_RejectsLookalikeCommands(string command)
    {
        Assert.False(AppSettingsService.IsStartupCommandForExecutable(command, @"C:\Apps\DevBox.exe"));
    }
}
