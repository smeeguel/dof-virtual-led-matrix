using System.Security.Cryptography;
using System.Text;
using VirtualDofMatrix.App.Rendering;
using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class RasterComposerSnapshotTests
{
    [Fact]
    public void Compose_ShouldRemainDeterministic_ForToneMappedBloomCircleDots()
    {
        var config = CreateBaseConfig();
        config.DotShape = "circle";
        config.ToneMapping.Enabled = true;
        config.Bloom.Enabled = true;
        config.Bloom.QualityPreset = "medium";
        config.Bloom.Threshold = 0.25;
        config.TemporalSmoothing.Enabled = true;
        config.TemporalSmoothing.RiseAlpha = 0.65;
        config.TemporalSmoothing.FallAlpha = 0.35;

        var frame = CreateGradientFrame(config.Width * config.Height);

        var firstComposer = new MatrixFrameRasterComposer();
        firstComposer.Configure(config);
        var secondComposer = new MatrixFrameRasterComposer();
        secondComposer.Configure(config);

        var first = firstComposer.Compose(frame);
        var second = secondComposer.Compose(frame);

        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.Stride, second.Stride);
        Assert.Equal(ComputeHash(first.Pixels), ComputeHash(second.Pixels));
    }

    [Fact]
    public void Compose_ShouldRemainDeterministic_ForSquareDotsWithoutBloom()
    {
        var config = CreateBaseConfig();
        config.DotShape = "square";
        config.Visual.SpecularHotspot = 0.6;
        config.Bloom.Enabled = false;
        config.ToneMapping.Enabled = false;

        var composer = new MatrixFrameRasterComposer();
        composer.Configure(config);
        var frame = CreatePulseFrame(config.Width * config.Height);

        var square = composer.Compose(frame);

        config.DotShape = "circle";
        composer.Configure(config);
        var circle = composer.Compose(frame);

        Assert.Equal(square.Width, circle.Width);
        Assert.Equal(square.Height, circle.Height);
        Assert.NotEqual(ComputeHash(square.Pixels), ComputeHash(circle.Pixels));
    }

    private static MatrixConfig CreateBaseConfig() =>
        new()
        {
            Width = 8,
            Height = 4,
            DotSize = 6,
            MinDotSpacing = 2,
            Mapping = "TopDownAlternateRightLeft",
            Brightness = 1.0,
            Gamma = 1.0,
            Visual = new MatrixVisualConfig
            {
                OffStateTintR = 150,
                OffStateTintG = 155,
                OffStateTintB = 170,
                OffStateAlpha = 0.22,
                LensFalloff = 0.45,
                SpecularHotspot = 0.28,
                RimHighlight = 0.22,
            },
        };

    private static FramePresentation CreateGradientFrame(int ledCount)
    {
        var payload = new byte[ledCount * 3];
        for (var i = 0; i < ledCount; i++)
        {
            payload[i * 3] = (byte)((i * 23) % 255);
            payload[(i * 3) + 1] = (byte)((255 - (i * 17)) % 255);
            payload[(i * 3) + 2] = (byte)((i * 31) % 255);
        }

        return new FramePresentation(payload, ledCount, ledCount, 1, DateTimeOffset.UtcNow);
    }

    private static FramePresentation CreatePulseFrame(int ledCount)
    {
        var payload = new byte[ledCount * 3];
        for (var i = 0; i < ledCount; i++)
        {
            var on = i % 3 == 0;
            payload[i * 3] = on ? (byte)255 : (byte)8;
            payload[(i * 3) + 1] = on ? (byte)32 : (byte)8;
            payload[(i * 3) + 2] = on ? (byte)16 : (byte)8;
        }

        return new FramePresentation(payload, ledCount, ledCount, 2, DateTimeOffset.UtcNow);
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
