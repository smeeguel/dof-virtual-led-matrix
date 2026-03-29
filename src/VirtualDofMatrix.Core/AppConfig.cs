namespace VirtualDofMatrix.Core;

public sealed class AppConfig
{
    public SerialConfig Serial { get; init; } = new();

    public MatrixConfig Matrix { get; init; } = new();

    public WindowConfig Window { get; init; } = new();

    public DebugConfig Debug { get; init; } = new();
}

public sealed class SerialConfig
{
    public string PortName { get; init; } = "COM2";

    public int BaudRate { get; init; } = 9600;

    public int MaxLedsPerChannel { get; init; } = 1100;

    public int MaxStrips { get; init; } = 8;

    public int ReadTimeoutMs { get; init; } = 200;

    public int WriteTimeoutMs { get; init; } = 1000;

    public bool DtrEnable { get; init; } = true;
}

public sealed class MatrixConfig
{
    public int Width { get; init; } = 32;

    public int Height { get; init; } = 8;

    public string Mapping { get; init; } = "TopDownAlternateRightLeft";

    public bool InstantTrigger { get; init; } = true;
}

public sealed class WindowConfig
{
    public bool AlwaysOnTop { get; init; } = true;

    public bool Borderless { get; init; } = true;

    public double Left { get; init; } = 100;

    public double Top { get; init; } = 100;

    public double Width { get; init; } = 1280;

    public double Height { get; init; } = 320;
}

public sealed class DebugConfig
{
    public bool LogProtocol { get; init; } = true;

    public bool LogFrames { get; init; } = false;
}
