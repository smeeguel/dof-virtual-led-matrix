using System.IO;
using System.Text.Json;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Configuration;

// Conversational overview: this store keeps settings.json compatible across schema tweaks by applying defaults after deserialize.
public sealed class AppConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public AppConfig Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new AppConfig();
        }

        var json = File.ReadAllText(filePath);
        var loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
        return ApplyLegacyDefaults(loaded ?? new AppConfig());
    }

    public void Save(string filePath, AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, SerializerOptions);
        File.WriteAllText(filePath, json);
    }

    private static AppConfig ApplyLegacyDefaults(AppConfig config)
    {
        // Backfill nested objects so older config files can still load without null-check noise everywhere else.
        if (config.Settings is null)
        {
            config.Settings = new SettingsConfig();
        }

        if (string.IsNullOrWhiteSpace(config.Settings.CabinetToyName))
        {
            config.Settings.CabinetToyName = "Matrix1";
        }

        if (string.IsNullOrWhiteSpace(config.Settings.VisualQuality))
        {
            config.Settings.VisualQuality = "High";
        }

        if (config.Matrix.MinDotSpacing < 2)
        {
            config.Matrix.MinDotSpacing = 2;
        }
        
        config.Matrix.Brightness = Math.Round(Math.Clamp(config.Matrix.Brightness, 0.0, 1.0) / 0.05, MidpointRounding.AwayFromZero) * 0.05;

        if (config.Matrix.ToneMapping is null)
        {
            config.Matrix.ToneMapping = new ToneMappingConfig();
        }

        if (config.Matrix.TemporalSmoothing is null)
        {
            config.Matrix.TemporalSmoothing = new TemporalSmoothingConfig();
        }

        if (config.Matrix.Visual is null)
        {
            config.Matrix.Visual = new MatrixVisualConfig();
        }
        
        config.Matrix.Visual.FullBrightnessRadiusMinPct = Math.Clamp(config.Matrix.Visual.FullBrightnessRadiusMinPct, 0.0, 1.0);

        if (config.Matrix.Bloom is null)
        {
            config.Matrix.Bloom = new BloomConfig();
        }

        return config;
    }
}
