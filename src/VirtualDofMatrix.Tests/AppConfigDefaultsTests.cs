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

    [Fact]
    public void VisualDefaults_ShouldPreferGpuDotPass()
    {
        var config = new AppConfig();
        Assert.False(config.Matrix.Visual.ForceCpuDotRasterFallback);
    }

    [Fact]
    public void SettingsDefaults_ShouldPointToDirectOutputConfigFolder()
    {
        var config = new AppConfig();

        Assert.Equal(@"C:\DirectOutput\Config", config.Settings.DofConfigFolderPath);
        Assert.Equal(@"C:\DirectOutput\Config\Cabinet.xml", config.Settings.CabinetXmlPath);
    }
}
