using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class AppConfigDefaultsTests
{
    [Fact]
    public void TemporalSmoothingDefaults_ShouldUseHighPersistence()
    {
        var config = new AppConfig();

        Assert.True(config.Matrix.TemporalSmoothing.Enabled);
        Assert.Equal(0.9, config.Matrix.TemporalSmoothing.RiseAlpha);
        Assert.Equal(0.9, config.Matrix.TemporalSmoothing.FallAlpha);
    }
}
