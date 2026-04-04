using System.Numerics;

namespace VirtualDofMatrix.App.Rendering;

// Overview: blur helpers are shared by CPU/GPU bloom prep paths and keep radius math in one place.
internal static class BloomBlurStrategy
{
    internal static void BlurFromImmutableSource(float[] sourceRgb, float[] destinationRgb, float[] scratchRgb, int width, int height, int radius)
    {
        // Compatibility shim for existing tests/callers that still provide interleaved RGB arrays.
        var pixels = width * height;
        var source = InterleavedToPlanes(sourceRgb, pixels);
        var destination = InterleavedToPlanes(destinationRgb, pixels);
        var scratch = InterleavedToPlanes(scratchRgb, pixels);
        BlurFromImmutableSource(source, destination, scratch, width, height, radius);
        PlanesToInterleaved(destination, destinationRgb);
    }

    internal static void BlurWithLegacyCopyPath(float[] sourceRgb, float[] destinationRgb, float[] scratchRgb, int width, int height, int radius)
    {
        // Regression helper: this mirrors the pre-refactor flow that copied first, then blurred in-place.
        Array.Copy(sourceRgb, destinationRgb, sourceRgb.Length);
        if (radius <= 0)
        {
            return;
        }

        var pixels = width * height;
        var destination = InterleavedToPlanes(destinationRgb, pixels);
        var scratch = InterleavedToPlanes(scratchRgb, pixels);
        HorizontalBlurChannel(destination.R, scratch.R, width, height, radius, 0, 0, width - 1, height - 1);
        HorizontalBlurChannel(destination.G, scratch.G, width, height, radius, 0, 0, width - 1, height - 1);
        HorizontalBlurChannel(destination.B, scratch.B, width, height, radius, 0, 0, width - 1, height - 1);
        VerticalBlurChannel(scratch.R, destination.R, width, height, radius, 0, 0, width - 1, height - 1);
        VerticalBlurChannel(scratch.G, destination.G, width, height, radius, 0, 0, width - 1, height - 1);
        VerticalBlurChannel(scratch.B, destination.B, width, height, radius, 0, 0, width - 1, height - 1);
        PlanesToInterleaved(destination, destinationRgb);
    }

    internal static void BlurFromImmutableSource(RgbPlaneBuffer source, RgbPlaneBuffer destination, RgbPlaneBuffer scratch, int width, int height, int radius)
    {
        BlurFromImmutableSource(source, destination, scratch, width, height, radius, 0, 0, width - 1, height - 1);
    }

    internal static void BlurFromImmutableSource(RgbPlaneBuffer source, RgbPlaneBuffer destination, RgbPlaneBuffer scratch, int width, int height, int radius, int minX, int minY, int maxX, int maxY)
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

        // This lane always treats source as read-only so sibling lanes can reuse it safely.
        if (radius <= 0)
        {
            CopyRoi(source, destination, width, minX, minY, maxX, maxY);
            return;
        }

        EnsureScratchSize(source, scratch);
        HorizontalBlurChannel(source.R, scratch.R, width, height, radius, minX, minY, maxX, maxY);
        HorizontalBlurChannel(source.G, scratch.G, width, height, radius, minX, minY, maxX, maxY);
        HorizontalBlurChannel(source.B, scratch.B, width, height, radius, minX, minY, maxX, maxY);
        VerticalBlurChannel(scratch.R, destination.R, width, height, radius, minX, minY, maxX, maxY);
        VerticalBlurChannel(scratch.G, destination.G, width, height, radius, minX, minY, maxX, maxY);
        VerticalBlurChannel(scratch.B, destination.B, width, height, radius, minX, minY, maxX, maxY);
    }

    private static void EnsureScratchSize(RgbPlaneBuffer source, RgbPlaneBuffer scratch)
    {
        if (scratch.Length != source.Length)
        {
            throw new ArgumentException("Scratch buffer must match source length.", nameof(scratch));
        }
    }

    private static void CopyRoi(RgbPlaneBuffer source, RgbPlaneBuffer destination, int width, int minX, int minY, int maxX, int maxY)
    {
        for (var y = minY; y <= maxY; y++)
        {
            var rowOffset = (y * width) + minX;
            var length = (maxX - minX) + 1;
            Array.Copy(source.R, rowOffset, destination.R, rowOffset, length);
            Array.Copy(source.G, rowOffset, destination.G, rowOffset, length);
            Array.Copy(source.B, rowOffset, destination.B, rowOffset, length);
        }
    }

    private static void HorizontalBlurChannel(float[] source, float[] destination, int width, int height, int radius, int minX, int minY, int maxX, int maxY)
    {
        for (var y = minY; y <= maxY; y++)
        {
            float sum = 0f;
            var samples = 0;
            var startSampleX = Math.Max(0, minX - radius);
            var endSampleX = Math.Min(width - 1, minX + radius);
            var rowOffset = y * width;

            // We vectorize the bootstrap sum because channel samples are contiguous in SoA layout.
            var bootstrapOffset = rowOffset + startSampleX;
            var bootstrapCount = endSampleX - startSampleX + 1;
            sum += SumContiguousVectorized(source, bootstrapOffset, bootstrapCount);
            samples = bootstrapCount;

            for (var x = minX; x <= maxX; x++)
            {
                destination[rowOffset + x] = sum / Math.Max(1, samples);

                var removeX = x - radius;
                if (removeX >= 0)
                {
                    sum -= source[rowOffset + removeX];
                    samples--;
                }

                var addX = x + radius + 1;
                if (addX < width)
                {
                    sum += source[rowOffset + addX];
                    samples++;
                }
            }
        }
    }

    private static void VerticalBlurChannel(float[] source, float[] destination, int width, int height, int radius, int minX, int minY, int maxX, int maxY)
    {
        for (var x = minX; x <= maxX; x++)
        {
            var startSampleY = Math.Max(0, minY - radius);
            var endSampleY = Math.Min(height - 1, minY + radius);
            var sampleCount = endSampleY - startSampleY + 1;
            // Columns are strided in memory, so we use a strided SIMD helper to keep vertical bootstrap sums vectorized too.
            var sum = SumStridedVectorized(source, (startSampleY * width) + x, width, sampleCount);
            var samples = sampleCount;

            for (var y = minY; y <= maxY; y++)
            {
                destination[(y * width) + x] = sum / Math.Max(1, samples);

                var removeY = y - radius;
                if (removeY >= 0)
                {
                    sum -= source[(removeY * width) + x];
                    samples--;
                }

                var addY = y + radius + 1;
                if (addY < height)
                {
                    sum += source[(addY * width) + x];
                    samples++;
                }
            }
        }
    }

    private static float SumContiguousVectorized(float[] source, int offset, int count)
    {
        var laneWidth = Vector<float>.Count;
        var i = 0;
        var vectorSum = Vector<float>.Zero;
        for (; i <= count - laneWidth; i += laneWidth)
        {
            vectorSum += new Vector<float>(source, offset + i);
        }

        float sum = 0f;
        for (var lane = 0; lane < laneWidth; lane++)
        {
            sum += vectorSum[lane];
        }

        for (; i < count; i++)
        {
            sum += source[offset + i];
        }

        return sum;
    }

    private static float SumStridedVectorized(float[] source, int offset, int stride, int count)
    {
        var laneWidth = Vector<float>.Count;
        var i = 0;
        var vectorSum = Vector<float>.Zero;
        Span<float> lane = stackalloc float[Vector<float>.Count];
        for (; i <= count - laneWidth; i += laneWidth)
        {
            for (var laneIndex = 0; laneIndex < laneWidth; laneIndex++)
            {
                lane[laneIndex] = source[offset + ((i + laneIndex) * stride)];
            }

            vectorSum += new Vector<float>(lane);
        }

        float sum = 0f;
        for (var laneIndex = 0; laneIndex < laneWidth; laneIndex++)
        {
            sum += vectorSum[laneIndex];
        }

        for (; i < count; i++)
        {
            sum += source[offset + (i * stride)];
        }

        return sum;
    }

    private static RgbPlaneBuffer InterleavedToPlanes(float[] interleavedRgb, int pixels)
    {
        var planes = new RgbPlaneBuffer();
        planes.EnsureSize(pixels);
        for (var i = 0; i < pixels; i++)
        {
            var srcOffset = i * 3;
            planes.R[i] = interleavedRgb[srcOffset];
            planes.G[i] = interleavedRgb[srcOffset + 1];
            planes.B[i] = interleavedRgb[srcOffset + 2];
        }

        return planes;
    }

    private static void PlanesToInterleaved(RgbPlaneBuffer source, float[] interleavedRgb)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var dstOffset = i * 3;
            interleavedRgb[dstOffset] = source.R[i];
            interleavedRgb[dstOffset + 1] = source.G[i];
            interleavedRgb[dstOffset + 2] = source.B[i];
        }
    }
}

internal sealed class RgbPlaneBuffer
{
    public float[] R { get; private set; } = Array.Empty<float>();
    public float[] G { get; private set; } = Array.Empty<float>();
    public float[] B { get; private set; } = Array.Empty<float>();

    public int Length => R.Length;

    public void EnsureSize(int pixels)
    {
        if (R.Length == pixels)
        {
            return;
        }

        R = new float[pixels];
        G = new float[pixels];
        B = new float[pixels];
    }

    public void Clear()
    {
        Array.Clear(R, 0, R.Length);
        Array.Clear(G, 0, G.Length);
        Array.Clear(B, 0, B.Length);
    }
}
