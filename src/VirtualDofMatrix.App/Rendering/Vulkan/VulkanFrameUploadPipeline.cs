using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering.Vulkan;

internal sealed class VulkanFrameUploadPipeline
{
    private const int UploadRingSize = 2;
    private GpuDotInstance[][] _stagingRing = Array.Empty<GpuDotInstance[]>();
    private int _capacity;
    private int _frameCursor;
    private MatrixConfig? _config;

    public void Configure(MatrixConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _capacity = Math.Max(1, config.Width * config.Height);
        _stagingRing = new GpuDotInstance[UploadRingSize][];
        for (var i = 0; i < _stagingRing.Length; i++)
        {
            _stagingRing[i] = new GpuDotInstance[_capacity];
        }
    }

    public (ReadOnlyMemory<GpuDotInstance> StagingInstances, int FrameSlot) Prepare(FramePresentation framePresentation)
    {
        if (_config is null || _stagingRing.Length == 0)
        {
            throw new InvalidOperationException("Upload pipeline must be configured before use.");
        }

        var slot = _frameCursor % _stagingRing.Length;
        var staging = _stagingRing[slot];
        var rgb = framePresentation.RgbMemory.Span;
        var requestedLedCount = Math.Max(framePresentation.HighestLedWritten, framePresentation.LedsPerChannel);
        var ledCount = Math.Min(Math.Min(requestedLedCount, rgb.Length / 3), _capacity);

        for (var logicalIndex = 0; logicalIndex < ledCount; logicalIndex++)
        {
            var mapped = MatrixMapper.MapLinearIndex(logicalIndex, _config.Width, _config.Height, _config.Mapping);
            var rgbOffset = logicalIndex * 3;
            var r = rgb[rgbOffset];
            var g = rgb[rgbOffset + 1];
            var b = rgb[rgbOffset + 2];

            staging[logicalIndex] = new GpuDotInstance
            {
                X = (ushort)mapped.X,
                Y = (ushort)mapped.Y,
                R = r,
                G = g,
                B = b,
                Intensity = Math.Max(r, Math.Max(g, b)),
                Flags = 0,
            };
        }

        _frameCursor++;
        return (staging.AsMemory(0, ledCount), slot);
    }
}
