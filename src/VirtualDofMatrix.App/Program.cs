using System.IO.Ports;
using VirtualDofMatrix.Core;

var options = AppOptions.Parse(args);

Console.WriteLine("Virtual DOF Matrix Emulator - Milestone 1");
Console.WriteLine($"Port={options.PortName} Baud={options.BaudRate} MaxLedsPerChannel={options.MaxLedsPerChannel}");

var settings = new SerialEmulatorSettings
{
    MaxLedsPerChannel = options.MaxLedsPerChannel,
    MaxStrips = options.MaxStrips,
};

var engine = new TeensyProtocolEngine(settings, new FrameBuffer());

using var serialPort = new SerialPort(options.PortName, options.BaudRate)
{
    ReadTimeout = options.ReadTimeoutMs,
    WriteTimeout = options.WriteTimeoutMs,
    DtrEnable = options.DtrEnable,
};

serialPort.Open();
Console.WriteLine("Serial port opened. Waiting for DOF traffic...");

var readBuffer = new byte[8192];
while (true)
{
    try
    {
        var read = serialPort.Read(readBuffer, 0, readBuffer.Length);
        if (read <= 0)
        {
            continue;
        }

        var result = engine.ProcessIncoming(readBuffer.AsSpan(0, read));

        foreach (var log in result.Logs)
        {
            Console.WriteLine($"[{log.TimestampUtc:O}] {log.Message}");
        }

        if (result.ResponseBytes.Length > 0)
        {
            serialPort.Write(result.ResponseBytes, 0, result.ResponseBytes.Length);
        }
    }
    catch (TimeoutException)
    {
        // Normal idle behavior.
    }
}

internal sealed class AppOptions
{
    public required string PortName { get; init; }
    public required int BaudRate { get; init; }
    public required int MaxLedsPerChannel { get; init; }
    public required int MaxStrips { get; init; }
    public required int ReadTimeoutMs { get; init; }
    public required int WriteTimeoutMs { get; init; }
    public required bool DtrEnable { get; init; }

    public static AppOptions Parse(string[] args)
    {
        string port = "COM2";
        int baud = 9600;
        int maxLeds = 1100;
        int maxStrips = 8;
        int readTimeoutMs = 200;
        int writeTimeoutMs = 1000;
        bool dtrEnable = true;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":
                    port = RequireValue(args, ref i);
                    break;
                case "--baud":
                    baud = int.Parse(RequireValue(args, ref i));
                    break;
                case "--max-leds":
                    maxLeds = int.Parse(RequireValue(args, ref i));
                    break;
                case "--max-strips":
                    maxStrips = int.Parse(RequireValue(args, ref i));
                    break;
                case "--read-timeout-ms":
                    readTimeoutMs = int.Parse(RequireValue(args, ref i));
                    break;
                case "--write-timeout-ms":
                    writeTimeoutMs = int.Parse(RequireValue(args, ref i));
                    break;
                case "--no-dtr":
                    dtrEnable = false;
                    break;
            }
        }

        return new AppOptions
        {
            PortName = port,
            BaudRate = baud,
            MaxLedsPerChannel = maxLeds,
            MaxStrips = maxStrips,
            ReadTimeoutMs = readTimeoutMs,
            WriteTimeoutMs = writeTimeoutMs,
            DtrEnable = dtrEnable,
        };
    }

    private static string RequireValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for argument '{args[i]}'.");
        }

        i++;
        return args[i];
    }
}
