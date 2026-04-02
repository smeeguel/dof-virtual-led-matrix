using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Rendering;

internal static class BloomProfileResolver
{
    public static BloomProfile Resolve(BloomConfig bloom)
    {
        if (!bloom.Enabled)
        {
            return BloomProfile.Disabled;
        }

        var preset = bloom.QualityPreset.Trim().ToLowerInvariant();
        var profile = preset switch
        {
            "low" => new BloomProfile(true, 2, 1, 2, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
            "medium" => new BloomProfile(true, 2, 2, 4, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
            "high" => new BloomProfile(true, 1, 3, 6, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
            "off" or "" => BloomProfile.Disabled,
            _ => new BloomProfile(true, bloom.BufferScaleDivisor, bloom.SmallRadius, bloom.WideRadius, bloom.Threshold, bloom.SmallStrength, bloom.WideStrength),
        };

        return profile with
        {
            ScaleDivisor = Math.Clamp(profile.ScaleDivisor, 1, 4),
            SmallRadius = Math.Clamp(profile.SmallRadius, 1, 8),
            WideRadius = Math.Clamp(profile.WideRadius, Math.Max(1, profile.SmallRadius), 16),
            Threshold = Math.Clamp(profile.Threshold, 0.0, 1.0),
        };
    }
}

internal sealed record BloomProfile(bool Enabled, int ScaleDivisor, int SmallRadius, int WideRadius, double Threshold, double SmallStrength, double WideStrength)
{
    public static BloomProfile Disabled => new(false, 1, 0, 0, 1.0, 0.0, 0.0);
}
