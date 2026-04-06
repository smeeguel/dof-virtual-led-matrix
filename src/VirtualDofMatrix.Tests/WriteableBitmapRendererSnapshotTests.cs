using System.Security.Cryptography;
using System.Text;
using VirtualDofMatrix.App.Rendering;
using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class WriteableBitmapRendererSnapshotTests
{
    [Fact]
    public void Compose_ShouldRemainDeterministic_ForToneMappedBloomCircleDots()
    {
        var config = CreateBaseConfig();
        config.DotShape = "circle";
        config.ToneMapping.Enabled = true;
        config.Bloom.Enabled = true;
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

    [Fact]
    public void Compose_ShouldSnapResidualSmoothingToOffState_ForRepeatedBlackFrames()
    {
        var config = CreateBaseConfig();
        config.Width = 4;
        config.Height = 2;
        config.Bloom.Enabled = false;
        config.TemporalSmoothing.Enabled = true;
        config.TemporalSmoothing.RiseAlpha = 0.5;
        config.TemporalSmoothing.FallAlpha = 0.3;

        var historyComposer = new MatrixFrameRasterComposer();
        historyComposer.Configure(config);

        historyComposer.Compose(CreateSolidFrame(config.Width * config.Height, 255, 20, 20, 3UL));
        for (var i = 0; i < 24; i++)
        {
            historyComposer.Compose(CreateSolidFrame(config.Width * config.Height, 0, 0, 0, (ulong)(4 + i)));
        }

        var decayed = historyComposer.Compose(CreateSolidFrame(config.Width * config.Height, 0, 0, 0, 100UL));

        var baselineComposer = new MatrixFrameRasterComposer();
        baselineComposer.Configure(config);
        var baseline = baselineComposer.Compose(CreateSolidFrame(config.Width * config.Height, 0, 0, 0, 200UL));

        Assert.Equal(ComputeHash(baseline.Pixels), ComputeHash(decayed.Pixels));
    }

    [Fact]
    public void Compose_ShouldSkipRaster_WhenFrameHasNoChangedCells()
    {
        var config = CreateBaseConfig();
        config.Bloom.Enabled = false;
        config.TemporalSmoothing.Enabled = false;

        var composer = new MatrixFrameRasterComposer();
        composer.Configure(config);
        var frame = CreateSolidFrame(config.Width * config.Height, 20, 60, 120, 10UL);

        var first = composer.Compose(frame);
        var second = composer.Compose(frame);

        Assert.False(second.UseFullFrameWrite);
        Assert.Empty(second.DirtyRects);
        Assert.Equal(ComputeHash(first.Pixels), ComputeHash(second.Pixels));
    }

    [Fact]
    public void Compose_ShouldIncrementallyRedraw_WhenCellTurnsOffWithBloomEnabled()
    {
        var config = CreateBaseConfig();
        config.Width = 6;
        config.Height = 3;
        config.Bloom.Enabled = true;
        config.Bloom.NearRadiusPx = 3;
        config.Bloom.FarRadiusPx = 5;
        config.Bloom.NearStrength = 0.55;
        config.Bloom.FarStrength = 0.4;
        config.TemporalSmoothing.Enabled = false;

        var composer = new MatrixFrameRasterComposer();
        composer.Configure(config);

        var ledCount = config.Width * config.Height;
        var firstPayload = new byte[ledCount * 3];
        firstPayload[0] = 255;
        firstPayload[1] = 64;
        firstPayload[2] = 24;
        composer.Compose(new FramePresentation(firstPayload, ledCount, ledCount, 1, DateTimeOffset.UtcNow));

        var offFrame = CreateSolidFrame(ledCount, 0, 0, 0, 2UL);
        var incremental = composer.Compose(offFrame);

        var baselineComposer = new MatrixFrameRasterComposer();
        baselineComposer.Configure(config);
        var baseline = baselineComposer.Compose(offFrame);

        Assert.False(incremental.UseFullFrameWrite);
        Assert.NotEmpty(incremental.DirtyRects);
        Assert.Equal(ComputeHash(baseline.Pixels), ComputeHash(incremental.Pixels));
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

    private static FramePresentation CreateSolidFrame(int ledCount, byte r, byte g, byte b, ulong sequence)
    {
        var payload = new byte[ledCount * 3];
        for (var i = 0; i < ledCount; i++)
        {
            payload[i * 3] = r;
            payload[(i * 3) + 1] = g;
            payload[(i * 3) + 2] = b;
        }

        return new FramePresentation(payload, ledCount, ledCount, sequence, DateTimeOffset.UtcNow);
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
