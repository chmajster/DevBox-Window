using System.Runtime.InteropServices;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class NodeRuntimeCatalog
{
    public const string RecommendedVersion = "24.19.0";

    public RuntimeDefinition GetRecommended() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => new RuntimeDefinition(
            "node",
            "Node.js LTS",
            RecommendedVersion,
            $"https://nodejs.org/dist/v{RecommendedVersion}/node-v{RecommendedVersion}-win-x64.zip",
            "57f71ab3652e797d84acddc79c81cc9ff1c6ddb2a1974cdb83f00fee9bff4c73",
            "node.exe",
            $"node-v{RecommendedVersion}-win-x64"),
        Architecture.Arm64 => new RuntimeDefinition(
            "node",
            "Node.js LTS",
            RecommendedVersion,
            $"https://nodejs.org/dist/v{RecommendedVersion}/node-v{RecommendedVersion}-win-arm64.zip",
            "8502f4a50b458d4cc38ed8f2001556c2cd239d464920f74017926ccb1e1c157f",
            "node.exe",
            $"node-v{RecommendedVersion}-win-arm64"),
        _ => throw new PlatformNotSupportedException("Portable Node.js automatic installation is supported on Windows x64 and ARM64.")
    };
}
