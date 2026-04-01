namespace VirtualDofMatrix.Core;

public static class MatrixMappingTableBuilder
{
    public static int[] BuildLogicalToMappedIndex(int width, int height, string mapping)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Matrix width and height must be positive.");
        }

        var capacity = checked(width * height);
        var table = new int[capacity];

        for (var logicalIndex = 0; logicalIndex < capacity; logicalIndex++)
        {
            var mapped = MatrixMapper.MapLinearIndex(logicalIndex, width, height, mapping);
            table[logicalIndex] = (mapped.Y * width) + mapped.X;
        }

        return table;
    }
}
