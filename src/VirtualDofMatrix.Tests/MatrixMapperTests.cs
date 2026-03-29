using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class MatrixMapperTests
{
    [Fact]
    public void TopDownAlternateRightLeft_ShouldStartAtTopRight()
    {
        var p0 = MatrixMapper.MapLinearIndex(0, 32, 8, "TopDownAlternateRightLeft");
        var p7 = MatrixMapper.MapLinearIndex(7, 32, 8, "TopDownAlternateRightLeft");
        var p8 = MatrixMapper.MapLinearIndex(8, 32, 8, "TopDownAlternateRightLeft");

        Assert.Equal((31, 0), p0);
        Assert.Equal((31, 7), p7);
        Assert.Equal((30, 7), p8);
    }

    [Fact]
    public void AppConfig_Defaults_MatrixRenderingSettings()
    {
        var config = new AppConfig();

        Assert.Equal(256, config.Matrix.Width);
        Assert.Equal(32, config.Matrix.Height);
        Assert.Equal("TopDownAlternateRightLeft", config.Matrix.Mapping);
        Assert.Equal("circle", config.Matrix.DotShape);
        Assert.Equal(2, config.Matrix.MinDotSpacing);
        Assert.Equal(1.0, config.Matrix.Brightness);
        Assert.Equal(1.0, config.Matrix.Gamma);

        Assert.True(config.Matrix.MinDotSpacing >= 2);
    }
}
