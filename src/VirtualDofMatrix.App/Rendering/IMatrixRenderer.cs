using System.Windows.Controls;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public interface IMatrixRenderer
{
    bool UsesImageHost { get; }

    void Initialize(Canvas primitiveCanvas, Image bitmapHost, MatrixConfig config);

    void Render(FramePresentation framePresentation);
}
