namespace VirtualDofMatrix.Core;

public static class MatrixFrameIndexMap
{
    public static int[] BuildLogicalToRasterMap(int width, int height, string mapping)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var capacity = checked(width * height);
        var map = new int[capacity];
        for (var logicalIndex = 0; logicalIndex < capacity; logicalIndex++)
        {
            var (x, y) = MatrixMapper.MapLinearIndex(logicalIndex, width, height, mapping);
            map[logicalIndex] = (y * width) + x;
        }

        return map;
    }
}
