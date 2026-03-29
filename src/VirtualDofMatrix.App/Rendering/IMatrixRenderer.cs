using System.Windows.Controls;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

public interface IMatrixRenderer
{
    void Initialize(Canvas targetCanvas, MatrixConfig config);

    void Render(FramePresentation framePresentation);
}
