using System.Windows.Controls;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public sealed record DotStyleConfig(
    string DotShape,
    string Mapping,
    int DotSpacing,
    double Brightness,
    double Gamma,
    MatrixVisualConfig Visual,
    ToneMappingConfig ToneMapping,
    TemporalSmoothingConfig TemporalSmoothing,
    BloomConfig Bloom,
    bool GlowEnabled,
    double GlowIntensity);

public sealed record MatrixRendererSurface(Canvas PrimitiveCanvas, Image BitmapHost);

public interface IMatrixRenderer : IDisposable
{
    string BackendName { get; }

    bool UsesImageHost { get; }

    void Initialize(MatrixRendererSurface renderSurface, int width, int height, DotStyleConfig dotStyleConfig);

    void UpdateFrame(ReadOnlySpan<Rgb24> logicalFrame);

    void Resize(double viewportWidth, double viewportHeight);

    void Render();
}
