using System.IO;
using System.Text.Json;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.App.Configuration;

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
        if (config.Matrix.Width == 32 && config.Matrix.Height == 8)
        {
            config.Matrix.Width = 256;
            config.Matrix.Height = 32;
        }

        if (config.Matrix.MinDotSpacing < 2)
        {
            config.Matrix.MinDotSpacing = 2;
        }

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

        return config;
    }
}
