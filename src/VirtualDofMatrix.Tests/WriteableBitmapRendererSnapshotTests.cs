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

    [Fact]
    public void Compose_ShouldContinueProducingDirtyUpdates_UntilSmoothedCellFullyTurnsOff()
    {
        var config = CreateBaseConfig();
        config.Width = 1;
        config.Height = 1;
        config.Bloom.Enabled = false;
        config.TemporalSmoothing.Enabled = true;
        config.TemporalSmoothing.RiseAlpha = 1.0;
        config.TemporalSmoothing.FallAlpha = 0.25;

        var composer = new MatrixFrameRasterComposer();
        composer.Configure(config);

        composer.Compose(CreateSolidFrame(1, 5, 0, 0, 1UL));

        var sawDirtyBeforeFullyOff = false;
        (int Width, int Height, int Stride, byte[] Pixels, IReadOnlyList<DirtyRect> DirtyRects, bool UseFullFrameWrite) frame =
            (0, 0, 0, Array.Empty<byte>(), Array.Empty<DirtyRect>(), false);
        for (ulong i = 0; i < 12; i++)
        {
            frame = composer.Compose(CreateSolidFrame(1, 0, 0, 0, 2UL + i));
            if (frame.DirtyRects.Count > 0)
            {
                sawDirtyBeforeFullyOff = true;
            }
        }

        var baselineComposer = new MatrixFrameRasterComposer();
        baselineComposer.Configure(config);
        var baselineOff = baselineComposer.Compose(CreateSolidFrame(1, 0, 0, 0, 500UL));

        Assert.True(sawDirtyBeforeFullyOff);
        // Conversational note: loop above always assigns `frame`, but we still defend against nullable flow uncertainty.
        Assert.NotNull(frame.Pixels);
        Assert.Equal(ComputeHash(baselineOff.Pixels), ComputeHash(frame.Pixels!));
    }

    [Fact]
    public void Compose_BloomCompositeProfiles_ShouldRemainPixelStable_AcrossHotLoop()
    {
        // Conversational note: this is a benchmark-style guard that hammers bloom compositing patterns repeatedly and
        // verifies we never drift pixels between iterations or across fresh composer instances.
        var profiles = new[]
        {
            // Use positional arguments here so constructor parameter casing differences can't break compile on named args.
            new BloomProfileCase("tight-near", 2, 4, 0.65, 0.35, 0.22, 0.15),
            new BloomProfileCase("wide-far", 1, 8, 0.35, 0.7, 0.18, 0.3),
            new BloomProfileCase("balanced", 4, 6, 0.5, 0.5, 0.25, 0.2),
        };

        foreach (var profile in profiles)
        {
            var config = CreateBaseConfig();
            config.Width = 16;
            config.Height = 8;
            config.DotSize = 7;
            config.Bloom.Enabled = true;
            config.Bloom.NearRadiusPx = profile.NearRadiusPx;
            config.Bloom.FarRadiusPx = profile.FarRadiusPx;
            config.Bloom.NearStrength = profile.NearStrength;
            config.Bloom.FarStrength = profile.FarStrength;
            config.Bloom.Threshold = profile.Threshold;
            config.Bloom.SoftKnee = profile.SoftKnee;
            config.TemporalSmoothing.Enabled = false;

            var ledCount = config.Width * config.Height;
            var baseFrame = CreateGradientFrame(ledCount);
            var pulseFrame = CreatePulseFrame(ledCount);

            var hotComposer = new MatrixFrameRasterComposer();
            hotComposer.Configure(config);
            var baselineHash = ComputeHash(hotComposer.Compose(baseFrame).Pixels);
            hotComposer.Compose(pulseFrame);

            for (ulong i = 0; i < 18; i++)
            {
                var frame = (i % 2 == 0) ? baseFrame : pulseFrame;
                var composed = hotComposer.Compose(new FramePresentation(frame.RgbBytes, frame.HighestLedWritten, frame.LedsPerChannel, frame.OutputSequence + i + 10, DateTimeOffset.UtcNow.AddMilliseconds(i)));
                if (i % 2 == 0)
                {
                    Assert.Equal(baselineHash, ComputeHash(composed.Pixels));
                }
            }

            var freshComposer = new MatrixFrameRasterComposer();
            freshComposer.Configure(config);
            var fresh = freshComposer.Compose(baseFrame);
            Assert.Equal(baselineHash, ComputeHash(fresh.Pixels));
        }
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

    private readonly record struct BloomProfileCase(
        string Name,
        int NearRadiusPx,
        int FarRadiusPx,
        double NearStrength,
        double FarStrength,
        double Threshold,
        double SoftKnee);
}
