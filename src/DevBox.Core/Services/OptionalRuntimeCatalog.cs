using System.Runtime.InteropServices;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class OptionalRuntimeCatalog
{
    public RuntimeDefinition GetMailpit() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => new RuntimeDefinition(
            "mailpit",
            "Mailpit",
            "1.31.1",
            "https://github.com/axllent/mailpit/releases/download/v1.31.1/mailpit-windows-amd64.zip",
            "73ff05204741bd89cc96e6074573c4b22e1edfadf1b3427536233ff20c751604",
            "mailpit.exe"),
        Architecture.Arm64 => new RuntimeDefinition(
            "mailpit",
            "Mailpit",
            "1.31.1",
            "https://github.com/axllent/mailpit/releases/download/v1.31.1/mailpit-windows-arm64.zip",
            "a6475d8e63ac92084d0a2a3cf22ff8a723b508ccc65283b2d4d54f71ef897ca0",
            "mailpit.exe"),
        _ => throw new PlatformNotSupportedException("Mailpit automatic installation is supported on Windows x64 and ARM64.")
    };

    public RuntimeDefinition GetRedisCompatibleServer() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => new RuntimeDefinition(
            "redis",
            "Garnet Redis-compatible server",
            "2.1.7",
            "https://github.com/microsoft/garnet/releases/download/v2.1.7/win-x64-based-readytorun.zip",
            "3409c9aba39565caa4c6166f2d7c66ac112c2a773d70679b1bb34458dea16473",
            "GarnetServer.exe"),
        Architecture.Arm64 => new RuntimeDefinition(
            "redis",
            "Garnet Redis-compatible server",
            "2.1.7",
            "https://github.com/microsoft/garnet/releases/download/v2.1.7/win-arm64-based-readytorun.zip",
            "5058984a69fa724a6d3474113e4b08498f6105d9588a0d16b607ec377646ebbc",
            "GarnetServer.exe"),
        _ => throw new PlatformNotSupportedException("Automatic Redis-compatible server installation is supported on Windows x64 and ARM64.")
    };

    public RuntimeDefinition Get(string key) => key.ToLowerInvariant() switch
    {
        "mailpit" => GetMailpit(),
        "redis" or "garnet" => GetRedisCompatibleServer(),
        _ => throw new KeyNotFoundException($"Optional runtime '{key}' is not registered.")
    };
}
