using System.Text.Json.Serialization;

namespace VirtualDofMatrix.Core;

public sealed class AppConfig
{
    // Branch task note: transport selector for serial-vs-named-pipe runtime modes.
    public TransportConfig Transport { get; set; } = new();

    public SerialConfig Serial { get; set; } = new();

    public MatrixConfig Matrix { get; set; } = new();

    public WindowConfig Window { get; set; } = new();

    public DebugConfig Debug { get; set; } = new();

    public SettingsConfig Settings { get; set; } = new();
}

public sealed class TransportConfig
{
    // Branch task note:
    // - "serial": legacy VSPE/COM emulation path.
    // - "namedPipe": fallback DOF custom-controller path (VirtualLEDStripController).
    public string Mode { get; set; } = "serial";

    // Branch task note: local IPC endpoint used when mode == "namedPipe".
    public string PipeName { get; set; } = "VirtualDofMatrix";
}

public sealed class SerialConfig
{
    public string PortName { get; set; } = "COM2";

    public int BaudRate { get; set; } = 9600;

    public int MaxLedsPerChannel { get; set; } = 1100;

    public int MaxStrips { get; set; } = 8;

    public int ReadTimeoutMs { get; set; } = 200;

    public int WriteTimeoutMs { get; set; } = 1000;

    public bool DtrEnable { get; set; } = true;
}

public sealed class MatrixConfig
{
    public string Renderer { get; set; } = "primitive";

    public int Width { get; set; } = 64;

    public int Height { get; set; } = 16;

    public string Mapping { get; set; } = "TopDownAlternateRightLeft";

    public string DotShape { get; set; } = "circle";

    [JsonIgnore]
    public int DotSize { get; set; } = 2;

    public int MinDotSpacing { get; set; } = 2;

    public double Brightness { get; set; } = 1.0;

    public double Gamma { get; set; } = 1.0;


    public ToneMappingConfig ToneMapping { get; set; } = new();

    public TemporalSmoothingConfig TemporalSmoothing { get; set; } = new();

    public MatrixVisualConfig Visual { get; set; } = new();

    public BloomConfig Bloom { get; set; } = new();
}

public sealed class ToneMappingConfig
{
    public bool Enabled { get; set; } = false;

    public double KneeStart { get; set; } = 0.85;

    public double Strength { get; set; } = 0.35;
}

public sealed class TemporalSmoothingConfig
{
    public bool Enabled { get; set; } = false;

    public double RiseAlpha { get; set; } = 1.0;

    public double FallAlpha { get; set; } = 1.0;
}

public sealed class MatrixVisualConfig
{
    // Experimental quality flag: when true, use a single-pass flat RGB dot render path.
    public bool FlatShading { get; set; } = false;

    public byte OffStateTintR { get; set; } = 150;

    public byte OffStateTintG { get; set; } = 155;

    public byte OffStateTintB { get; set; } = 170;

    public double OffStateAlpha { get; set; } = 0.22;

    public double LensFalloff { get; set; } = 0.45;

    public double SpecularHotspot { get; set; } = 0.28;

    public double RimHighlight { get; set; } = 0.22;
}

public sealed class BloomConfig
{
    // Experimental: Bloom is intentionally disabled in the Settings UI due performance cost.
    // It can still be toggled manually via settings.json for testing.
    public bool Enabled { get; set; } = false;

    public string QualityPreset { get; set; } = "off";

    public double Threshold { get; set; } = 0.55;

    public int SmallRadius { get; set; } = 2;

    public int WideRadius { get; set; } = 5;

    public double SmallStrength { get; set; } = 0.6;

    public double WideStrength { get; set; } = 0.25;

    public int BufferScaleDivisor { get; set; } = 2;
}

public sealed class WindowConfig
{
    public bool AlwaysOnTop { get; set; } = true;

    public bool Borderless { get; set; } = true;

    public double Left { get; set; } = 100;

    public double Top { get; set; } = 100;

    public double Width { get; set; } = 1280;

    public double Height { get; set; } = 320;
}

public sealed class DebugConfig
{
    public bool ShowDebug { get; set; } = false;

    public bool LogProtocol { get; set; } = true;

    public bool LogFrames { get; set; } = false;
}

public sealed class SettingsConfig
{
    public string CabinetXmlPath { get; set; } = string.Empty;

    public string CabinetToyName { get; set; } = "Matrix1";

    public bool AutoUpdateCabinetOnResolutionChange { get; set; } = true;

    public string VisualQuality { get; set; } = "Medium";
}
