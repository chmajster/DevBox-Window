using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class PhpRuntimePoolManagerTests
{
    [Fact]
    public void GetPort_IsStableAndInDedicatedRange()
    {
        var first = PhpRuntimePoolManager.GetPort("8.5.10");
        var second = PhpRuntimePoolManager.GetPort("8.5.10");

        Assert.Equal(first, second);
        Assert.InRange(first, 20000, 49999);
    }

    [Theory]
    [InlineData("")]
    [InlineData("8.5")]
    [InlineData("../../8.5.10")]
    [InlineData("8.5.10-rc1")]
    public void GetPort_RejectsUnsupportedVersion(string version)
    {
        Assert.ThrowsAny<ArgumentException>(() => PhpRuntimePoolManager.GetPort(version));
    }
}
