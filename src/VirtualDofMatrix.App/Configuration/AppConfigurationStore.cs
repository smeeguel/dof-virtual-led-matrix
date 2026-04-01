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
        if (config.Transport is null)
        {
            config.Transport = new TransportConfig();
        }

        // Virtual matrix should default to named-pipe transport to eliminate
        // serial COM baud-rate as a frame throughput bottleneck.
        if (string.IsNullOrWhiteSpace(config.Transport.Mode) ||
            config.Transport.Mode.Equals("serial", StringComparison.OrdinalIgnoreCase))
        {
            config.Transport.Mode = "namedPipe";
        }

        if (string.IsNullOrWhiteSpace(config.Transport.PipeName))
        {
            config.Transport.PipeName = "VirtualDofMatrix";
        }

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
            config.Settings.VisualQuality = "Medium";
        }

        if (config.Matrix.MinDotSpacing < 2)
        {
            config.Matrix.MinDotSpacing = 2;
        }

        if (string.IsNullOrWhiteSpace(config.Matrix.Renderer))
        {
            config.Matrix.Renderer = "vulkan";
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

        if (config.Matrix.Vulkan is null)
        {
            config.Matrix.Vulkan = new VulkanRenderConfig();
        }

        return config;
    }
}
