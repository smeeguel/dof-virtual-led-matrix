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
                matrix.ToneMapping.Enabled = false;
                matrix.ToneMapping.Strength = 0.2;
                matrix.TemporalSmoothing.Enabled = false;
                matrix.TemporalSmoothing.RiseAlpha = 1.0;
                matrix.TemporalSmoothing.FallAlpha = 1.0;
                matrix.Bloom.Enabled = false;
                matrix.Bloom.QualityPreset = "off";
                break;
            case Medium:
                matrix.ToneMapping.Enabled = true;
                matrix.ToneMapping.Strength = 0.35;
                matrix.TemporalSmoothing.Enabled = true;
                matrix.TemporalSmoothing.RiseAlpha = 0.65;
                matrix.TemporalSmoothing.FallAlpha = 0.45;
                matrix.Bloom.Enabled = true;
                matrix.Bloom.QualityPreset = "medium";
                matrix.Bloom.SmallStrength = 0.4;
                matrix.Bloom.WideStrength = 0.18;
                break;
            case High:
                matrix.ToneMapping.Enabled = true;
                matrix.ToneMapping.Strength = 0.55;
                matrix.TemporalSmoothing.Enabled = true;
                matrix.TemporalSmoothing.RiseAlpha = 0.5;
                matrix.TemporalSmoothing.FallAlpha = 0.3;
                matrix.Bloom.Enabled = true;
                matrix.Bloom.QualityPreset = "high";
                matrix.Bloom.SmallStrength = 0.75;
                matrix.Bloom.WideStrength = 0.35;
                break;
            case Custom:
                break;
            default:
                ApplyPreset(matrix, Medium);
                break;
        }
    }
}
