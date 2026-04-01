namespace VirtualDofMatrix.Core;

public static class GpuFrameUpload
{
    public static byte[] BuildBgraFrame(ReadOnlySpan<Rgb24> logicalFrame, ReadOnlySpan<int> logicalToRasterMap, int width, int height)
    {
        var destination = new byte[checked(width * height * 4)];
        var count = Math.Min(logicalFrame.Length, logicalToRasterMap.Length);
        for (var logicalIndex = 0; logicalIndex < count; logicalIndex++)
        {
            var rasterIndex = logicalToRasterMap[logicalIndex];
            if ((uint)rasterIndex >= (uint)(width * height))
            {
                continue;
            }

            var offset = rasterIndex * 4;
            var color = logicalFrame[logicalIndex];
            destination[offset] = color.B;
            destination[offset + 1] = color.G;
            destination[offset + 2] = color.R;
            destination[offset + 3] = 255;
        }

        return destination;
    }
}
