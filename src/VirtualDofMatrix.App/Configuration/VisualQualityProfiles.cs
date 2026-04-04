using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Configuration;

public static class VisualQualityProfiles
{
    public const string Low = "Low";
    public const string High = "High";
    public const string Custom = "Custom";

    public static readonly string[] Names = [Low, High, Custom];

    public static void ApplyPreset(MatrixConfig matrix, string preset)
    {
        // Conversational note: quality presets should only toggle feature flags, not overwrite user-tuned numeric knobs.
        switch (preset)
        {
            case Low:
                matrix.Visual.FlatShading = true;
                matrix.ToneMapping.Enabled = false;
                matrix.TemporalSmoothing.Enabled = false;
                matrix.Bloom.Enabled = false;
                break;
            case High:
                matrix.Visual.FlatShading = false;
                matrix.ToneMapping.Enabled = true;
                matrix.TemporalSmoothing.Enabled = true;
                matrix.Bloom.Enabled = true;
                break;
            case Custom:
                break;
            default:
                ApplyPreset(matrix, High);
                break;
        }
    }
}
