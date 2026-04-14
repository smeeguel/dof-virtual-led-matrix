using System.Text.Json.Serialization;

namespace VirtualDofMatrix.Core;

public sealed class AppConfig
{
    public TransportConfig Transport { get; set; } = new();

    public MatrixConfig Matrix { get; set; } = new();

    public WindowConfig Window { get; set; } = new();

    public DebugConfig Debug { get; set; } = new();

    public SettingsConfig Settings { get; set; } = new();

    public RoutingConfig Routing { get; set; } = new();
}

public sealed class TransportConfig
{
    // Local IPC endpoint used by the VirtualLEDStripController named-pipe transport.
    public string PipeName { get; set; } = "VirtualDofMatrix";

    // Local IPC endpoint used for runtime show/hide control commands.
    public string ControlPipeName { get; set; } = "VirtualDofMatrix.Control";

    // Local IPC endpoint used by the multi-client toy frame broadcast adapter.
    public string BroadcastPipeName { get; set; } = "VirtualDofMatrix.Broadcast";

    // Maximum queued frame envelopes per client before latest-wins eviction starts.
    public int BroadcastMaxQueuePerClient { get; set; } = 8;
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

    public bool FillGapEnabled { get; set; } = false;

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
    // When true, renderers should keep non-dot/background pixels transparent instead of opaque black.
    public bool TransparentBackground { get; set; } = false;

    // Note: this explicit mode keeps runtime behavior easy to reason about.
    // - DirectPresentOnly (default): stay on the swapchain present path whenever interop is available.
    // - LegacyReadback: opt-in compatibility/debug mode that always uses GPU->CPU readback presentation.
    public string GpuPresentMode { get; set; } = "DirectPresentOnly";

    // When true, force the renderer to keep CPU dot rasterization for maximum compatibility.
    public bool ForceCpuDotRasterFallback { get; set; } = false;

    // When true, keep one diagnostic readback sample for direct-present parity debugging.
    public bool EnableDirectPresentParitySampling { get; set; } = false;

    // When true, keep explicit context flush in diagnostic capture paths.
    public bool EnableDiagnosticReadbackCapture { get; set; } = false;

    // Note: keep this alias for older settings.json files so legacy configs still map cleanly.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ForceLegacyReadbackPresent
    {
        get => GpuPresentMode.Equals("LegacyReadback", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
            {
                GpuPresentMode = "LegacyReadback";
            }
        }
    }

    // Note: old "prefer swapchain" flag now just flips the explicit mode back to direct present.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreferD3D11SwapChainPresent
    {
        get => !GpuPresentMode.Equals("LegacyReadback", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (!value)
            {
                GpuPresentMode = "LegacyReadback";
            }
        }
    }

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

    // Runtime-only normalized RGB background color sampled from the active window background.
    // Renderers consume this to keep GPU compositing aligned with the configured solid backdrop.
    [JsonIgnore]
    public float BackgroundColorR { get; set; } = 0f;

    [JsonIgnore]
    public float BackgroundColorG { get; set; } = 0f;

    [JsonIgnore]
    public float BackgroundColorB { get; set; } = 0f;

    // Runtime-only mirror of window background visibility so renderers can choose transparent vs. solid fills.
    [JsonIgnore]
    public bool BackgroundVisible { get; set; } = true;
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

    public bool LockAspectRatio { get; set; } = true;

    // Note: viewer backgrounds default to opaque black unless a toy/window opts into transparency.
    public bool BackgroundVisible { get; set; } = true;

    // Hex color used when BackgroundVisible is true (for example #000000).
    public string BackgroundColor { get; set; } = "#000000";

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
    // Preferred DOF config folder. Startup bootstrap probes this folder (or default) and validates required files.
    public string DofConfigFolderPath { get; set; } = @"C:\DirectOutput\Config";

    public string CabinetXmlPath { get; set; } = @"C:\DirectOutput\Config\Cabinet.xml";

    public string CabinetToyName { get; set; } = "Matrix1";

    public bool AutoUpdateCabinetOnResolutionChange { get; set; } = true;

    public string VisualQuality { get; set; } = "High";

    // Default strip bulb size (pixels) used by Toy Wizard initial strip window sizing.
    public int DefaultStripBulbSize { get; set; } = 32;
}

public sealed class RoutingConfig
{
    // Note: this version string lets routing changes evolve without breaking legacy settings files.
    public string RoutingSchemaVersion { get; set; } = "1";

    public RoutingPolicyConfig Policy { get; set; } = new();

    // Optional INI file used to define toy-specific routing/visual settings with comments.
    public string ToyConfigIniPath { get; set; } = "toys.ini";

    public List<ToyRouteConfig> Toys { get; set; } = [];
}

public sealed class RoutingPolicyConfig
{
    // Default strip length used when source ranges only provide strip+offset and no canonical start.
    public int DefaultStripLength { get; set; } = 1100;

    // If true, invalid toy entries are ignored instead of hard-failing app startup.
    public bool SkipInvalidToys { get; set; } = true;

    // Actionable behavior toggle for unknown mapping names from config payloads.
    public bool FallbackToMatrixMapping { get; set; } = true;

    // Supported values: drop, partial-black-fill, hold-last.
    public string OnMissingData { get; set; } = "partial-black-fill";

    // Supported values: reject-config, clamp.
    public string OnOversizeRange { get; set; } = "clamp";

    // Supported values: latest-wins, drop-oldest.
    // Approved default keeps live display responsive by preferring the newest frame.
    public string OnFrameRateSpike { get; set; } = "latest-wins";
}

public sealed class ToyRouteConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public string Kind { get; set; } = "matrix";

    public ToySourceConfig Source { get; set; } = new();

    public ToyMappingConfig Mapping { get; set; } = new();

    public ToyWindowOptionsConfig Window { get; set; } = new();

    public ToyRenderOptionsConfig Render { get; set; } = new();

    public ToyBloomOptionsConfig Bloom { get; set; } = new();

    public List<ToyAdapterTargetConfig> OutputTargets { get; set; } = [];
}

public sealed class ToySourceConfig
{
    // Canonical index into the global flattened input stream.
    public int? CanonicalStart { get; set; }

    // Number of LEDs from canonical start consumed by this toy.
    public int Length { get; set; }

    // Optional legacy source coordinates (strip + offset) for normalization.
    public int? StripIndex { get; set; }

    public int? StripOffset { get; set; }
}

public sealed class ToyMappingConfig
{
    public int Width { get; set; } = 32;

    public int Height { get; set; } = 8;

    public string Mode { get; set; } = "TopDownAlternateRightLeft";
}

public sealed class ToyWindowOptionsConfig
{
    public bool UseGlobalWindow { get; set; } = true;

    public bool AlwaysOnTop { get; set; } = true;

    public bool Borderless { get; set; } = true;

    public bool LockAspectRatio { get; set; } = true;

    // When false, the toy viewport background is transparent so only rendered LEDs are shown.
    public bool BackgroundVisible { get; set; } = true;

    // Hex color used when BackgroundVisible is true (for example #000000).
    public string BackgroundColor { get; set; } = "#000000";

    public double? Left { get; set; }

    public double? Top { get; set; }

    public double? Width { get; set; }

    public double? Height { get; set; }
}

public sealed class ToyRenderOptionsConfig
{
    public string DotShape { get; set; } = "circle";

    public int MinDotSpacing { get; set; } = 2;

    public bool FillGapEnabled { get; set; } = false;

    public double Brightness { get; set; } = 1.0;

    public double Gamma { get; set; } = 0.8;
}


public sealed class ToyBloomOptionsConfig
{
    public bool Enabled { get; set; } = true;

    public double Threshold { get; set; } = 0.72;

    public double SoftKnee { get; set; } = 0.18;

    public int NearRadiusPx { get; set; } = 2;

    public int FarRadiusPx { get; set; } = 10;

    public double NearStrength { get; set; } = 1.0;

    public double FarStrength { get; set; } = 0.2;
}
public sealed class ToyAdapterTargetConfig
{
    public string Adapter { get; set; } = "viewer";

    public bool Enabled { get; set; } = true;

    // Free-form adapter payload so each output adapter can own its own small settings contract.
    public Dictionary<string, string> Options { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
