using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void ToolchainSmoke() => Assert.Equal(1, Wire.ProtocolMajor);
}
