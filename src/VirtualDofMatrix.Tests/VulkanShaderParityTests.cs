using System.Security.Cryptography;
using System.Text;
using VirtualDofMatrix.App.Rendering;
using VirtualDofMatrix.App.Rendering.Vulkan;
using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class VulkanShaderParityTests
{
    [Theory]
    [InlineData(32, 8)]
    [InlineData(128, 32)]
    public void ComposerParityFixtures_ShouldRemainDeterministic_ForVulkanTargetResolutions(int width, int height)
    {
        var config = CreateConfig(width, height);
        var frame = CreateDeterministicFrame(width * height);

        var composerA = new MatrixFrameRasterComposer();
        composerA.Configure(config);
        var imageA = composerA.Compose(frame);

        var composerB = new MatrixFrameRasterComposer();
        composerB.Configure(config);
        var imageB = composerB.Compose(frame);

        Assert.Equal(imageA.Width, imageB.Width);
        Assert.Equal(imageA.Height, imageB.Height);
        Assert.Equal(imageA.Stride, imageB.Stride);
        Assert.Equal(ComputeHash(imageA.Pixels), ComputeHash(imageB.Pixels));
    }

    [Fact]
    public void ShaderParameterBlock_ShouldTrackVisualSemanticsWithoutRecompile()
    {
        var visual = new MatrixVisualConfig
        {
            BodyContribution = 0.55,
            CoreContribution = 1.2,
            SpecularContribution = 1.7,
            CoreBase = 0.15,
            CoreIntensityScale = 0.81,
            SpecularBase = 0.04,
            SpecularIntensityScale = 0.52,
            SpecularMax = 0.92,
            OffStateTintR = 120,
            OffStateTintG = 100,
            OffStateTintB = 90,
        };

        var block = VulkanShaderParameterBlock.FromVisual(visual);

        Assert.Equal(visual.BodyContribution, block.BodyContribution, 3);
        Assert.Equal(visual.CoreContribution, block.CoreContribution, 3);
        Assert.Equal(visual.SpecularContribution, block.SpecularContribution, 3);
        Assert.Equal(visual.CoreBase, block.CoreBase, 3);
        Assert.Equal(visual.CoreIntensityScale, block.CoreIntensityScale, 3);
        Assert.Equal(visual.SpecularBase, block.SpecularBase, 3);
        Assert.Equal(visual.SpecularIntensityScale, block.SpecularIntensityScale, 3);
        Assert.Equal((float)Math.Clamp(visual.SpecularMax, 0.0, 1.0), block.SpecularMax, 3);
    }

    private static MatrixConfig CreateConfig(int width, int height)
    {
        return new MatrixConfig
        {
            Width = width,
            Height = height,
            DotSize = 2,
            MinDotSpacing = 2,
            Mapping = "TopDownAlternateRightLeft",
            Visual = new MatrixVisualConfig
            {
                BodyContribution = 0.8,
                CoreContribution = 1.0,
                SpecularContribution = 0.65,
                CoreBase = 0.18,
                CoreIntensityScale = 0.72,
                SpecularBase = 0.06,
                SpecularIntensityScale = 0.42,
                SpecularMax = 0.7,
                OffStateTintR = 130,
                OffStateTintG = 135,
                OffStateTintB = 145,
            },
        };
    }

    private static FramePresentation CreateDeterministicFrame(int ledCount)
    {
        var payload = new byte[ledCount * 3];
        for (var i = 0; i < ledCount; i++)
        {
            payload[i * 3] = (byte)((i * 13) % 255);
            payload[(i * 3) + 1] = (byte)((i * 29) % 255);
            payload[(i * 3) + 2] = (byte)((255 - (i * 17)) % 255);
        }

        return new FramePresentation(payload, ledCount, ledCount, 1, DateTimeOffset.UtcNow);
    }

    private static string ComputeHash(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2"));
        }

        return builder.ToString();
    }
}
