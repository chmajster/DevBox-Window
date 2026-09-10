using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class NodeRuntimeCatalogTests
{
    [Fact]
    public void RecommendedNodeRuntimeIsPinnedAndVersioned()
    {
        var definition = new NodeRuntimeCatalog().GetRecommended();

        Assert.Equal("node", definition.Key);
        Assert.Equal(NodeRuntimeCatalog.RecommendedVersion, definition.Version);
        Assert.True(definition.HasRemotePackage);
        Assert.StartsWith("https://nodejs.org/dist/v24.19.0/", definition.DownloadUrl!, StringComparison.Ordinal);
        Assert.Equal(64, definition.Sha256!.Length);
        Assert.Equal("node.exe", definition.ExecutableRelativePath);
        Assert.Contains("node-v24.19.0-win-", definition.ArchiveRootDirectory!);
    }
}
