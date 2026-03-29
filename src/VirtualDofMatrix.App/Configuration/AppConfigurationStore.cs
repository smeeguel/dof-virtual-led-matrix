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

        if (config.Matrix.DotSize == 18 && config.Matrix.DotSpacing == 4)
        {
            config.Matrix.DotSize = 2;
            config.Matrix.DotSpacing = 2;
        }

        if (config.Matrix.DotSpacing < 2)
        {
            config.Matrix.DotSpacing = 2;
        }

        if (config.Matrix.Visual is null)
        {
            config.Matrix.Visual = new MatrixVisualConfig();
        }

        return config;
    }
}
