namespace VirtualDofMatrix.App.Rendering;

internal static class BloomBlurStrategy
{
    internal static void BlurFromImmutableSource(float[] sourceRgb, float[] destinationRgb, float[] scratchRgb, int width, int height, int radius)
    {
        BlurFromImmutableSource(sourceRgb, destinationRgb, scratchRgb, width, height, radius, 0, 0, width - 1, height - 1);
    }

    internal static void BlurFromImmutableSource(float[] sourceRgb, float[] destinationRgb, float[] scratchRgb, int width, int height, int radius, int minX, int minY, int maxX, int maxY)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        minX = Math.Clamp(minX, 0, width - 1);
        minY = Math.Clamp(minY, 0, height - 1);
        maxX = Math.Clamp(maxX, 0, width - 1);
        maxY = Math.Clamp(maxY, 0, height - 1);
        if (minX > maxX || minY > maxY)
        {
            return;
        }

        // This lane always treats sourceRgb as read-only so sibling lanes can reuse it safely.
        if (radius <= 0)
        {
            for (var y = minY; y <= maxY; y++)
            {
                var rowOffset = ((y * width) + minX) * 3;
                var length = ((maxX - minX) + 1) * 3;
                Array.Copy(sourceRgb, rowOffset, destinationRgb, rowOffset, length);
            }
            return;
        }

        EnsureScratchSize(sourceRgb, scratchRgb);
        HorizontalBlurRgb(sourceRgb, scratchRgb, width, height, radius, minX, minY, maxX, maxY);
        VerticalBlurRgb(scratchRgb, destinationRgb, width, height, radius, minX, minY, maxX, maxY);
    }

    internal static void BlurWithLegacyCopyPath(float[] sourceRgb, float[] destinationRgb, float[] scratchRgb, int width, int height, int radius)
    {
        // Regression helper: this mirrors the pre-refactor flow that copied first, then blurred in-place.
        Array.Copy(sourceRgb, destinationRgb, sourceRgb.Length);
        if (radius <= 0)
        {
            return;
        }

        EnsureScratchSize(sourceRgb, scratchRgb);
        HorizontalBlurRgb(destinationRgb, scratchRgb, width, height, radius);
        VerticalBlurRgb(scratchRgb, destinationRgb, width, height, radius);
    }

    private static void EnsureScratchSize(float[] sourceRgb, float[] scratchRgb)
    {
        if (scratchRgb.Length != sourceRgb.Length)
        {
            throw new ArgumentException("Scratch buffer must match source length.", nameof(scratchRgb));
        }
    }

    private static void HorizontalBlurRgb(float[] source, float[] destination, int width, int height, int radius, int minX, int minY, int maxX, int maxY)
    {
        for (var y = minY; y <= maxY; y++)
        {
            float sumR = 0, sumG = 0, sumB = 0;
            var samples = 0;
            var startSampleX = Math.Max(0, minX - radius);
            var endSampleX = Math.Min(width - 1, minX + radius);
            for (var sx = startSampleX; sx <= endSampleX; sx++)
            {
                var sampleOffset = ((y * width) + sx) * 3;
                sumR += source[sampleOffset];
                sumG += source[sampleOffset + 1];
                sumB += source[sampleOffset + 2];
                samples++;
            }

            for (var x = minX; x <= maxX; x++)
            {
                var dstOffset = ((y * width) + x) * 3;
                destination[dstOffset] = sumR / Math.Max(1, samples);
                destination[dstOffset + 1] = sumG / Math.Max(1, samples);
                destination[dstOffset + 2] = sumB / Math.Max(1, samples);

                var removeX = x - radius;
                if (removeX >= 0)
                {
                    var removeOffset = ((y * width) + removeX) * 3;
                    sumR -= source[removeOffset];
                    sumG -= source[removeOffset + 1];
                    sumB -= source[removeOffset + 2];
                    samples--;
                }

                var addX = x + radius + 1;
                if (addX < width)
                {
                    var addOffset = ((y * width) + addX) * 3;
                    sumR += source[addOffset];
                    sumG += source[addOffset + 1];
                    sumB += source[addOffset + 2];
                    samples++;
                }
            }
        }
    }

    private static void VerticalBlurRgb(float[] source, float[] destination, int width, int height, int radius, int minX, int minY, int maxX, int maxY)
    {
        for (var x = minX; x <= maxX; x++)
        {
            float sumR = 0, sumG = 0, sumB = 0;
            var samples = 0;
            var startSampleY = Math.Max(0, minY - radius);
            var endSampleY = Math.Min(height - 1, minY + radius);
            for (var sy = startSampleY; sy <= endSampleY; sy++)
            {
                var sampleOffset = ((sy * width) + x) * 3;
                sumR += source[sampleOffset];
                sumG += source[sampleOffset + 1];
                sumB += source[sampleOffset + 2];
                samples++;
            }

            for (var y = minY; y <= maxY; y++)
            {
                var dstOffset = ((y * width) + x) * 3;
                destination[dstOffset] = sumR / Math.Max(1, samples);
                destination[dstOffset + 1] = sumG / Math.Max(1, samples);
                destination[dstOffset + 2] = sumB / Math.Max(1, samples);

                var removeY = y - radius;
                if (removeY >= 0)
                {
                    var removeOffset = ((removeY * width) + x) * 3;
                    sumR -= source[removeOffset];
                    sumG -= source[removeOffset + 1];
                    sumB -= source[removeOffset + 2];
                    samples--;
                }

                var addY = y + radius + 1;
                if (addY < height)
                {
                    var addOffset = ((addY * width) + x) * 3;
                    sumR += source[addOffset];
                    sumG += source[addOffset + 1];
                    sumB += source[addOffset + 2];
                    samples++;
                }
            }
        }
    }
}
