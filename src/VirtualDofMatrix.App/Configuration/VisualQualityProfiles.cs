using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Configuration;

public static class VisualQualityProfiles
{
    public const string Low = "Low";
    public const string Medium = "Medium";
    public const string High = "High";
    public const string Custom = "Custom";

    public static readonly string[] Names = [Low, Medium, High, Custom];

    public static void ApplyPreset(MatrixConfig matrix, string preset)
    {
        switch (preset)
        {
            case Low:
                matrix.Renderer = "writeableBitmap";
                matrix.Visual.FlatShading = true;
                matrix.ToneMapping.Enabled = false;
                matrix.ToneMapping.Strength = 0.2;
                matrix.TemporalSmoothing.Enabled = false;
                matrix.TemporalSmoothing.RiseAlpha = 1.0;
                matrix.TemporalSmoothing.FallAlpha = 1.0;
                break;
            case Medium:
                matrix.Renderer = "primitive";
                matrix.Visual.FlatShading = false;
                matrix.ToneMapping.Enabled = false;
                matrix.TemporalSmoothing.Enabled = false;
                break;
            case High:
                matrix.Renderer = "writeableBitmap";
                matrix.Visual.FlatShading = false;
                matrix.ToneMapping.Enabled = true;
                matrix.ToneMapping.Strength = 0.55;
                matrix.TemporalSmoothing.Enabled = true;
                matrix.TemporalSmoothing.RiseAlpha = 0.5;
                matrix.TemporalSmoothing.FallAlpha = 0.3;
                break;
            case Custom:
                break;
            default:
                ApplyPreset(matrix, Medium);
                break;
        }

        // Bloom is currently experimental and excluded from quality presets.
        matrix.Bloom.Enabled = false;
    }
}
