using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

internal static class BloomProfileResolver
{
    public static BloomProfile Resolve(BloomConfig bloom)
    {
        // If bloom is off, we can bail out early and skip any buffer work.
        if (!bloom.Enabled)
        {
            return BloomProfile.Disabled;
        }

        // We clamp the knobs here so downstream rendering code can stay lean and trust the profile.
        var scaleDivisor = Math.Clamp(bloom.DownsampleDivisor, 1, 4);
        var nearRadius = Math.Clamp(bloom.NearRadiusPx / scaleDivisor, 1, 16);
        var farRadius = Math.Clamp(bloom.FarRadiusPx / scaleDivisor, nearRadius, 32);

        return new BloomProfile(
            true,
            scaleDivisor,
            Math.Clamp(bloom.Threshold, 0.0, 1.0),
            Math.Clamp(bloom.SoftKnee, 0.0, 1.0),
            nearRadius,
            farRadius,
            Math.Clamp(bloom.NearStrength, 0.0, 2.0),
            Math.Clamp(bloom.FarStrength, 0.0, 2.0));
    }
}

internal sealed record BloomProfile(
    bool Enabled,
    int ScaleDivisor,
    double Threshold,
    double SoftKnee,
    int NearRadius,
    int FarRadius,
    double NearStrength,
    double FarStrength)
{
    public static BloomProfile Disabled => new(false, 1, 1.0, 0.0, 0, 0, 0.0, 0.0);
}
