using System.Windows.Controls;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

// Conversational overview: these contracts isolate window logic from renderer backend details (CPU/GPU/etc).
public sealed record DotStyleConfig(
    string DotShape,
    string Mapping,
    int DotSize,
    int DotSpacing,
    double Brightness,
    double Gamma,
    MatrixVisualConfig Visual,
    ToneMappingConfig ToneMapping,
    TemporalSmoothingConfig TemporalSmoothing,
    BloomConfig Bloom,
    bool ForceCpuDotRasterFallback,
    bool GlowEnabled,
    double GlowIntensity);

public sealed record MatrixRendererSurface(Canvas PrimitiveCanvas, Image BitmapHost);

public interface IMatrixRenderer : IDisposable
{
    string BackendName { get; }

    bool UsesImageHost { get; }

    void Initialize(MatrixRendererSurface renderSurface, int width, int height, DotStyleConfig dotStyleConfig);

    void UpdateFrame(FramePresentation presentation);

    void Resize(double viewportWidth, double viewportHeight);

    void Render();

    void Clear();
}
