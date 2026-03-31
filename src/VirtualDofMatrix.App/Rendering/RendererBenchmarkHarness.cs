using System.Diagnostics;
using System.Text;
using System.Windows.Controls;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public static class RendererBenchmarkHarness
{
    private static readonly (int Width, int Height)[] MatrixSizes =
    [
        (32, 8),
        (64, 16),
        (128, 32),
        (128, 64),
    ];

    private static readonly (string Name, Func<IMatrixRenderer> Factory)[] Backends =
    [
        ("primitive", static () => new WpfPrimitiveMatrixRenderer()),
        ("writeableBitmap", static () => new WriteableBitmapMatrixRenderer()),
        ("direct3d", static () => new Direct3DMatrixRenderer()),
    ];

    public static string Run(int sampleFrames = 180, double targetFps = 60.0)
    {
        var report = new StringBuilder();
        report.AppendLine("Renderer benchmark (frame time, dropped frames, present latency)");
        report.AppendLine($"Frames per run: {sampleFrames}, target fps: {targetFps:0.##}");
        report.AppendLine();

        foreach (var (width, height) in MatrixSizes)
        {
            report.AppendLine($"Resolution {width}x{height}");

            foreach (var backend in Backends)
            {
                var result = BenchmarkBackend(backend.Factory(), backend.Name, width, height, sampleFrames, targetFps);
                report.AppendLine(
                    $"  - {result.Backend,-16} frame={result.AverageFrameMs,7:0.000} ms | " +
                    $"dropped={result.DroppedFrames,4}/{sampleFrames} | " +
                    $"present-lat={result.AveragePresentLatencyMs,7:0.000} ms");
            }

            report.AppendLine();
        }

        return report.ToString();
    }

    private static RendererBenchmarkResult BenchmarkBackend(
        IMatrixRenderer renderer,
        string backend,
        int width,
        int height,
        int sampleFrames,
        double targetFps)
    {
        var canvas = new Canvas();
        var image = new Image();
        var config = CreateBenchmarkConfig(width, height, backend);
        renderer.Initialize(canvas, image, config);

        var droppedFrames = 0;
        var totalFrameTicks = 0L;
        var totalPresentLatencyTicks = 0L;
        var frameBudgetTicks = Stopwatch.Frequency / targetFps;

        for (var i = 0; i < sampleFrames; i++)
        {
            var frame = CreateFrame(width, height, i);
            var startTicks = Stopwatch.GetTimestamp();
            renderer.Render(frame);
            var endTicks = Stopwatch.GetTimestamp();

            var elapsedTicks = endTicks - startTicks;
            totalFrameTicks += elapsedTicks;
            if (elapsedTicks > frameBudgetTicks)
            {
                droppedFrames++;
            }

            var presentLatency = DateTimeOffset.UtcNow - frame.PresentedAtUtc;
            totalPresentLatencyTicks += (long)(presentLatency.TotalSeconds * Stopwatch.Frequency);
        }

        var avgFrameMs = (totalFrameTicks * 1000.0) / Stopwatch.Frequency / sampleFrames;
        var avgLatencyMs = (totalPresentLatencyTicks * 1000.0) / Stopwatch.Frequency / sampleFrames;

        return new RendererBenchmarkResult(backend, avgFrameMs, droppedFrames, avgLatencyMs);
    }

    private static MatrixConfig CreateBenchmarkConfig(int width, int height, string backend)
    {
        var stride = 10;
        return new MatrixConfig
        {
            Width = width,
            Height = height,
            Renderer = backend,
            DotShape = "circle",
            DotSize = Math.Max(2, stride - 2),
            MinDotSpacing = 2,
            Mapping = "TopDownAlternateRightLeft",
            Brightness = 1.0,
            Gamma = 1.0,
            Visual = new MatrixVisualConfig
            {
                FlatShading = false,
                OffStateTintR = 140,
                OffStateTintG = 145,
                OffStateTintB = 160,
                OffStateAlpha = 0.2,
                LensFalloff = 0.45,
                SpecularHotspot = 0.28,
                RimHighlight = 0.22,
            },
            ToneMapping = new ToneMappingConfig
            {
                Enabled = true,
                KneeStart = 0.85,
                Strength = 0.5,
            },
            TemporalSmoothing = new TemporalSmoothingConfig
            {
                Enabled = true,
                RiseAlpha = 0.6,
                FallAlpha = 0.35,
            },
        };
    }

    private static FramePresentation CreateFrame(int width, int height, int frameIndex)
    {
        var ledCount = width * height;
        var payload = new byte[ledCount * 3];

        for (var i = 0; i < ledCount; i++)
        {
            var phase = (frameIndex + i) % 256;
            payload[i * 3] = (byte)phase;
            payload[(i * 3) + 1] = (byte)((phase * 3) % 256);
            payload[(i * 3) + 2] = (byte)((255 - phase) % 256);
        }

        return new FramePresentation(payload, ledCount, ledCount, (ulong)(frameIndex + 1), DateTimeOffset.UtcNow);
    }

    private readonly record struct RendererBenchmarkResult(
        string Backend,
        double AverageFrameMs,
        int DroppedFrames,
        double AveragePresentLatencyMs);
}
