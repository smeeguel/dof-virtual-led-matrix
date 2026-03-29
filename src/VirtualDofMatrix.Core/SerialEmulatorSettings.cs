namespace VirtualDofMatrix.Core;

public sealed class SerialEmulatorSettings
{
    public int MaxLedsPerChannel { get; init; } = 1100;

    public int MaxStrips { get; init; } = 8;

    public int MaxTotalLeds => MaxLedsPerChannel * MaxStrips;
}
