namespace VirtualDofMatrix.Core;

public sealed class AppConfig
{
    public SerialConfig Serial { get; set; } = new();

    public MatrixConfig Matrix { get; set; } = new();

    public WindowConfig Window { get; set; } = new();

    public DebugConfig Debug { get; set; } = new();
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
    public int Width { get; set; } = 256;

    public int Height { get; set; } = 32;

    public string Mapping { get; set; } = "TopDownAlternateRightLeft";

    public string DotShape { get; set; } = "circle";

    public int DotSize { get; set; } = 3;

    public int DotSpacing { get; set; } = 1;

    public double Brightness { get; set; } = 1.0;

    public double Gamma { get; set; } = 1.0;

    public bool InstantTrigger { get; set; } = true;
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
    public bool LogProtocol { get; set; } = true;

    public bool LogFrames { get; set; } = false;
}
