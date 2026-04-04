using VirtualDofMatrix.App.Configuration;
using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class VisualQualityProfilesTests
{
    [Fact]
    public void ApplyPreset_ShouldOnlyToggleFeatureFlags_ForLow()
    {
        var matrix = new MatrixConfig
        {
            ToneMapping = new ToneMappingConfig { Enabled = true, Strength = 1.23 },
            TemporalSmoothing = new TemporalSmoothingConfig { Enabled = true, RiseAlpha = 0.77, FallAlpha = 0.66 },
            Bloom = new BloomConfig { Enabled = true },
            Visual = new MatrixVisualConfig { FlatShading = false },
        };

        VisualQualityProfiles.ApplyPreset(matrix, VisualQualityProfiles.Low);

        Assert.True(matrix.Visual.FlatShading);
        Assert.False(matrix.ToneMapping.Enabled);
        Assert.False(matrix.TemporalSmoothing.Enabled);
        Assert.False(matrix.Bloom.Enabled);
        Assert.Equal(1.23, matrix.ToneMapping.Strength);
        Assert.Equal(0.77, matrix.TemporalSmoothing.RiseAlpha);
        Assert.Equal(0.66, matrix.TemporalSmoothing.FallAlpha);
    }

    [Fact]
    public void ApplyPreset_ShouldOnlyToggleFeatureFlags_ForHigh()
    {
        var matrix = new MatrixConfig
        {
            ToneMapping = new ToneMappingConfig { Enabled = false, Strength = 1.11 },
            TemporalSmoothing = new TemporalSmoothingConfig { Enabled = false, RiseAlpha = 0.88, FallAlpha = 0.44 },
            Bloom = new BloomConfig { Enabled = false },
            Visual = new MatrixVisualConfig { FlatShading = true },
        };

        VisualQualityProfiles.ApplyPreset(matrix, VisualQualityProfiles.High);

        Assert.False(matrix.Visual.FlatShading);
        Assert.True(matrix.ToneMapping.Enabled);
        Assert.True(matrix.TemporalSmoothing.Enabled);
        Assert.True(matrix.Bloom.Enabled);
        Assert.Equal(1.11, matrix.ToneMapping.Strength);
        Assert.Equal(0.88, matrix.TemporalSmoothing.RiseAlpha);
        Assert.Equal(0.44, matrix.TemporalSmoothing.FallAlpha);
    }
}
