namespace VirtualDofMatrix.Core;

public static class MatrixMapper
{
    public static (int X, int Y) MapLinearIndex(int index, int width, int height, string mapping)
    {
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Matrix width and height must be positive.");
        }

        var max = width * height;
        if (index >= max)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} exceeds matrix capacity {max}.");
        }

        return mapping switch
        {
            "TopDownAlternateRightLeft" => MapTopDownAlternateRightLeft(index, width, height),
            "RowMajor" => (index % width, index / width),
            _ => throw new NotSupportedException($"Unsupported mapping mode '{mapping}'.")
        };
    }

    private static (int X, int Y) MapTopDownAlternateRightLeft(int index, int width, int height)
    {
        var column = index / height;
        var yInColumn = index % height;
        var x = width - 1 - column;

        var isEvenOffsetFromRight = ((width - 1 - x) % 2) == 0;
        var y = isEvenOffsetFromRight ? yInColumn : (height - 1 - yInColumn);

        return (x, y);
    }
}
