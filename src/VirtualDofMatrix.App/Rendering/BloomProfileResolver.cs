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
        var nearRadius = Math.Clamp((int)Math.Ceiling(bloom.NearRadiusPx / (double)scaleDivisor), 0, 32);
        var farRadius = Math.Clamp((int)Math.Ceiling(bloom.FarRadiusPx / (double)scaleDivisor), 0, 64);

        return new BloomProfile(
            true,
            scaleDivisor,
            Math.Clamp(bloom.Threshold, 0.0, 1.0),
            Math.Clamp(bloom.SoftKnee, 0.0, 1.0),
            nearRadius,
            farRadius,
            Math.Max(0.0, bloom.NearStrength),
            Math.Max(0.0, bloom.FarStrength));
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
