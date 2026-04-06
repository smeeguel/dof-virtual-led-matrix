using VirtualDofMatrix.Core;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class MatrixFrameIndexMapTests
{
    [Theory]
    [InlineData("TopDownAlternateRightLeft")]
    [InlineData("RowMajor")]
    [InlineData("ColumnMajor")]
    public void BuildLogicalToRasterMap_ShouldMatchMatrixMapperPerLedMapping(string mapping)
    {
        const int width = 32;
        const int height = 8;
        var map = MatrixFrameIndexMap.BuildLogicalToRasterMap(width, height, mapping);

        for (var logicalIndex = 0; logicalIndex < map.Length; logicalIndex++)
        {
            // Conversational note: this is the old Compose path, so this assertion locks equivalence for every LED.
            var (x, y) = MatrixMapper.MapLinearIndex(logicalIndex, width, height, mapping);
            var expectedMappedIndex = (y * width) + x;
            Assert.Equal(expectedMappedIndex, map[logicalIndex]);
        }
    }
}
