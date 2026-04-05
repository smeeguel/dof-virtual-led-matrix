using System.Text.Json.Serialization;

namespace VirtualDofMatrix.Core;

public sealed class AppConfig
{
    public TransportConfig Transport { get; set; } = new();

    public MatrixConfig Matrix { get; set; } = new();

    public WindowConfig Window { get; set; } = new();

    public DebugConfig Debug { get; set; } = new();

    public SettingsConfig Settings { get; set; } = new();
}

public sealed class TransportConfig
{
    // Local IPC endpoint used by the VirtualLEDStripController named-pipe transport.
    public string PipeName { get; set; } = "VirtualDofMatrix";

    // Local IPC endpoint used for runtime show/hide control commands.
    public string ControlPipeName { get; set; } = "VirtualDofMatrix.Control";
}

public sealed class MatrixConfig
{
    public string Renderer { get; set; } = "gpu";

    public int Width { get; set; } = 128;

    public int Height { get; set; } = 32;

    public string Mapping { get; set; } = "TopDownAlternateRightLeft";

    public string DotShape { get; set; } = "circle";

    [JsonIgnore]
    public int DotSize { get; set; } = 2;

    public int MinDotSpacing { get; set; } = 2;

    public double Brightness { get; set; } = 1.0;

    public double Gamma { get; set; } = 0.8;


    public ToneMappingConfig ToneMapping { get; set; } = new();

    public TemporalSmoothingConfig TemporalSmoothing { get; set; } = new();

    public MatrixVisualConfig Visual { get; set; } = new();

    public BloomConfig Bloom { get; set; } = new();
}

public sealed class ToneMappingConfig
{
    public bool Enabled { get; set; } = true;

    public double KneeStart { get; set; } = 0.95;

    public double Strength { get; set; } = 0.55;
}

public sealed class TemporalSmoothingConfig
{
    public bool Enabled { get; set; } = true;

    public double RiseAlpha { get; set; } = 0.9;

    public double FallAlpha { get; set; } = 0.9;
}

public sealed class MatrixVisualConfig
{
    // When true, force the renderer to keep CPU dot rasterization for maximum compatibility.
    public bool ForceCpuDotRasterFallback { get; set; } = false;

    // When true, force GPU bloom output to use legacy D3D11->CPU readback instead of D3DImage interop presentation.
    public bool ForceLegacyReadbackPresent { get; set; } = false;

    // Experimental quality flag: when true, use a single-pass flat RGB dot render path.
    public bool FlatShading { get; set; } = false;

    // Inner-radius percentage (0..1) kept at full intensity before radial falloff starts.
    public double FullBrightnessRadiusMinPct { get; set; } = 0.8;

    public byte OffStateTintR { get; set; } = 20;

    public byte OffStateTintG { get; set; } = 20;

    public byte OffStateTintB { get; set; } = 20;

    public double OffStateAlpha { get; set; } = 0.2;

    public double LensFalloff { get; set; } = 0.45;

    public double SpecularHotspot { get; set; } = 0.28;

    public double RimHighlight { get; set; } = 0.22;
}

public sealed class BloomConfig
{
    public bool Enabled { get; set; } = true;

    public double Threshold { get; set; } = 0.72;

    // Width of the smooth transition above threshold where bloom starts to ramp in.
    public double SoftKnee { get; set; } = 0.18;

    // Screen-space downsample factor used by bloom buffers.
    public int DownsampleDivisor { get; set; } = 2;

    // Tight, near-field glow radius in final rendered pixels.
    public int NearRadiusPx { get; set; } = 2;

    // Wide, lower-intensity halo radius in final rendered pixels.
    public int FarRadiusPx { get; set; } = 10;

    public double NearStrength { get; set; } = 1.0;

    public double FarStrength { get; set; } = 0.2;
}

public sealed class WindowConfig
{
    public bool AlwaysOnTop { get; set; } = true;

    public bool Borderless { get; set; } = true;

    public double Left { get; set; } = 10;

    public double Top { get; set; } = 6;

    public double Width { get; set; } = 1412;

    public double Height { get; set; } = 353;
}

public sealed class DebugConfig
{
    public bool ShowDebug { get; set; } = false;

    public bool LogProtocol { get; set; } = false;

    public bool LogFrames { get; set; } = false;
}

public sealed class SettingsConfig
{
    public string CabinetXmlPath { get; set; } = @"C:\DirectOutput\Config\Cabinet.xml";

    public string CabinetToyName { get; set; } = "Matrix1";

    public bool AutoUpdateCabinetOnResolutionChange { get; set; } = true;

    public string VisualQuality { get; set; } = "High";
}
