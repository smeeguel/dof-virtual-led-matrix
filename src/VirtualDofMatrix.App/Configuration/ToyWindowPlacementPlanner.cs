using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Configuration;

// Overview: assigns initial toy window positions only when coordinates are incomplete,
// while preserving fully explicit user-provided placements.
public static class ToyWindowPlacementPlanner
{
    public static void AssignMissingWindowPositions(IList<ToyRouteConfig> toys, WindowConfig globalWindowDefaults, double placementGap)
    {
        ArgumentNullException.ThrowIfNull(toys);
        ArgumentNullException.ThrowIfNull(globalWindowDefaults);

        // Note: keep placement deterministic by walking toys in the provided order and maintaining
        // one lane cursor per major-axis orientation. Horizontal-major stacks down (Top grows),
        // vertical-major stacks right (Left grows).
        var horizontalLane = new LaneCursor(globalWindowDefaults.Left, globalWindowDefaults.Top, Axis.Vertical);
        var verticalLane = new LaneCursor(globalWindowDefaults.Left, globalWindowDefaults.Top, Axis.Horizontal);

        foreach (var toy in toys)
        {
            if (toy?.Window is null)
            {
                continue;
            }

            var hasExplicitLeft = toy.Window.Left.HasValue;
            var hasExplicitTop = toy.Window.Top.HasValue;

            if (hasExplicitLeft && hasExplicitTop)
            {
                continue;
            }

            var width = ResolveWindowDimension(toy.Window.Width, globalWindowDefaults.Width);
            var height = ResolveWindowDimension(toy.Window.Height, globalWindowDefaults.Height);
            var orientation = ResolveMajorAxis(toy);

            if (orientation == ToyMajorAxis.Horizontal)
            {
                var candidateLeft = horizontalLane.FixedCoordinate;
                var candidateTop = horizontalLane.NextStackCoordinate;
                if (!hasExplicitLeft)
                {
                    toy.Window.Left = candidateLeft;
                }

                if (!hasExplicitTop)
                {
                    toy.Window.Top = candidateTop;
                }

                horizontalLane.AdvanceWithPlacement(
                    left: toy.Window.Left ?? candidateLeft,
                    top: toy.Window.Top ?? candidateTop,
                    width,
                    height,
                    placementGap);
            }
            else
            {
                var candidateLeft = verticalLane.NextStackCoordinate;
                var candidateTop = verticalLane.FixedCoordinate;
                if (!hasExplicitLeft)
                {
                    toy.Window.Left = candidateLeft;
                }

                if (!hasExplicitTop)
                {
                    toy.Window.Top = candidateTop;
                }

                verticalLane.AdvanceWithPlacement(
                    left: toy.Window.Left ?? candidateLeft,
                    top: toy.Window.Top ?? candidateTop,
                    width,
                    height,
                    placementGap);
            }
        }
    }

    private static double ResolveWindowDimension(double? configured, double fallback)
    {
        var normalizedFallback = fallback > 0 ? fallback : 100d;
        return configured is > 0 ? configured.Value : normalizedFallback;
    }

    internal static ToyMajorAxis ResolveMajorAxis(ToyRouteConfig toy)
    {
        var width = Math.Max(1, toy.Mapping?.Width ?? 1);
        var height = Math.Max(1, toy.Mapping?.Height ?? 1);
        return width >= height ? ToyMajorAxis.Horizontal : ToyMajorAxis.Vertical;
    }

    internal enum ToyMajorAxis
    {
        Horizontal,
        Vertical,
    }

    private enum Axis
    {
        Horizontal,
        Vertical,
    }

    private sealed class LaneCursor(double fixedCoordinate, double baseStackCoordinate, Axis stackAxis)
    {
        public double FixedCoordinate { get; } = fixedCoordinate;
        public double NextStackCoordinate { get; private set; } = baseStackCoordinate;

        public void AdvanceWithPlacement(double left, double top, double width, double height, double gap)
        {
            var normalizedGap = Math.Max(0, gap);
            var end = stackAxis == Axis.Vertical
                ? top + height + normalizedGap
                : left + width + normalizedGap;

            NextStackCoordinate = Math.Max(NextStackCoordinate, end);
        }
    }
}
