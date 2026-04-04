using VirtualDofMatrix.App.Rendering;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class BloomBlurStrategyTests
{
    [Theory]
    [InlineData(5, 4, 0)]
    [InlineData(5, 4, 1)]
    [InlineData(7, 3, 2)]
    public void BlurFromImmutableSource_ShouldMatchLegacyPath_ForRepresentativeFrames(int width, int height, int radius)
    {
        var source = CreateFrame(width, height, seed: 73 + radius);
        var legacyDestination = new float[source.Length];
        var legacyScratch = new float[source.Length];
        var refactorDestination = new float[source.Length];
        var refactorScratch = new float[source.Length];

        // This keeps a direct regression baseline from the pre-refactor "copy then blur in-place" behavior.
        BloomBlurStrategy.BlurWithLegacyCopyPath(source, legacyDestination, legacyScratch, width, height, radius);
        BloomBlurStrategy.BlurFromImmutableSource(source, refactorDestination, refactorScratch, width, height, radius);

        Assert.Equal(legacyDestination, refactorDestination);
    }

    [Fact]
    public void BlurFromImmutableSource_ShouldKeepNearAndFarLanesIndependent()
    {
        const int width = 6;
        const int height = 5;
        var source = CreateFrame(width, height, seed: 99);
        var sourceSnapshot = (float[])source.Clone();
        var nearDestination = new float[source.Length];
        var farDestination = new float[source.Length];
        var sharedScratch = new float[source.Length];

        // Near and far run back-to-back with shared scratch, but both read the same immutable source buffer.
        BloomBlurStrategy.BlurFromImmutableSource(source, nearDestination, sharedScratch, width, height, radius: 1);
        BloomBlurStrategy.BlurFromImmutableSource(source, farDestination, sharedScratch, width, height, radius: 3);

        Assert.Equal(sourceSnapshot, source);
        Assert.NotEqual(nearDestination, farDestination);
    }

    private static float[] CreateFrame(int width, int height, int seed)
    {
        var random = new Random(seed);
        var data = new float[width * height * 3];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = random.Next(0, 256);
        }

        return data;
    }
}
