using Xunit;
using VirtualDofMatrix.Core;

namespace VirtualDofMatrix.Tests;

public sealed class TeensyProtocolEngineTests
{
    [Fact]
    public void Probe_ShouldAck()
    {
        var engine = CreateEngine();

        var result = engine.ProcessIncoming([0x00]);

        Assert.Equal([(byte)'A'], result.ResponseBytes);
    }

    [Fact]
    public void M_ShouldReturnConfiguredMaximumAndAck()
    {
        var engine = CreateEngine(maxLeds: 1100);

        var result = engine.ProcessIncoming([(byte)'M']);

        Assert.Equal(new byte[] { 0x04, 0x4C, (byte)'A' }, result.ResponseBytes);
    }

    [Fact]
    public void L_ShouldParseLedsPerChannelAndAck()
    {
        var frame = new FrameBuffer();
        var engine = CreateEngine(frameBuffer: frame);

        var result = engine.ProcessIncoming([(byte)'L', 0x01, 0x00]);

        Assert.Equal([(byte)'A'], result.ResponseBytes);
        Assert.Equal(256, frame.LedsPerChannel);
    }

    [Fact]
    public void R_ShouldParse256LedPayloadAndAck()
    {
        var frame = new FrameBuffer();
        var engine = CreateEngine(frameBuffer: frame);
        var payload = Enumerable.Range(0, 768).Select(i => (byte)(i % 256)).ToArray();

        var command = new List<byte>
        {
            (byte)'R',
            0x00,
            0x00,
            0x01,
            0x00,
        };
        command.AddRange(payload);

        var result = engine.ProcessIncoming(command.ToArray());

        Assert.Equal([(byte)'A'], result.ResponseBytes);
        Assert.Equal(768, frame.RgbBytes.Length);
        Assert.Equal(payload, frame.RgbBytes.ToArray());
    }

    [Fact]
    public void CombinedRead_ShouldParseMultipleCommandsFromSingleChunk()
    {
        var frame = new FrameBuffer();
        var engine = CreateEngine(frameBuffer: frame);
        var payload = Enumerable.Repeat((byte)0xAB, 6).ToArray(); // 2 LEDs
        var chunk = new byte[]
        {
            0x00,
            (byte)'M',
            (byte)'L', 0x00, 0x02,
            (byte)'R', 0x00, 0x00, 0x00, 0x02,
            0xAB, 0xAB, 0xAB, 0xAB, 0xAB, 0xAB,
            (byte)'O',
        };

        var result = engine.ProcessIncoming(chunk);

        Assert.Equal(new byte[] { (byte)'A', 0x04, 0x4C, (byte)'A', (byte)'A', (byte)'A', (byte)'A' }, result.ResponseBytes);
        Assert.Equal(payload, frame.RgbBytes.Slice(0, 6).ToArray());
        Assert.Equal((ulong)1, frame.OutputSequence);
    }

    [Fact]
    public void SplitRead_ShouldBufferUntilCompleteRPayloadThenAck()
    {
        var frame = new FrameBuffer();
        var engine = CreateEngine(frameBuffer: frame);

        // R, target=0, ledCount=2, payload=6 bytes split across multiple reads
        var part1 = new byte[] { (byte)'R', 0x00, 0x00, 0x00, 0x02, 0x01, 0x02 };
        var part2 = new byte[] { 0x03, 0x04 };
        var part3 = new byte[] { 0x05, 0x06 };

        var result1 = engine.ProcessIncoming(part1);
        var result2 = engine.ProcessIncoming(part2);
        var result3 = engine.ProcessIncoming(part3);

        Assert.Empty(result1.ResponseBytes);
        Assert.Empty(result2.ResponseBytes);
        Assert.Equal([(byte)'A'], result3.ResponseBytes);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, frame.RgbBytes.Slice(0, 6).ToArray());
    }

    [Fact]
    public void KnownGoodSequence_ShouldAckEveryCommand()
    {
        var frame = new FrameBuffer();
        var engine = CreateEngine(frameBuffer: frame);
        var payload = Enumerable.Range(0, 768).Select(i => (byte)(255 - (i % 256))).ToArray();

        Assert.Equal([(byte)'A'], engine.ProcessIncoming([0x00]).ResponseBytes);
        Assert.Equal(new byte[] { 0x04, 0x4C, (byte)'A' }, engine.ProcessIncoming([(byte)'M']).ResponseBytes);
        Assert.Equal([(byte)'A'], engine.ProcessIncoming([(byte)'L', 0x01, 0x00]).ResponseBytes);
        Assert.Equal([(byte)'A'], engine.ProcessIncoming([(byte)'C']).ResponseBytes);
        Assert.Equal([(byte)'A'], engine.ProcessIncoming([(byte)'O']).ResponseBytes);

        var r = new List<byte> { (byte)'R', 0x00, 0x00, 0x01, 0x00 };
        r.AddRange(payload);
        Assert.Equal([(byte)'A'], engine.ProcessIncoming(r.ToArray()).ResponseBytes);

        Assert.Equal([(byte)'A'], engine.ProcessIncoming([(byte)'O']).ResponseBytes);
        Assert.Equal((ulong)2, frame.OutputSequence);
        Assert.Equal(768, frame.RgbBytes.Length);
    }

    private static TeensyProtocolEngine CreateEngine(
        int maxLeds = 1100,
        FrameBuffer? frameBuffer = null)
    {
        var settings = new SerialEmulatorSettings
        {
            MaxLedsPerChannel = maxLeds,
            MaxStrips = 8,
        };

        return new TeensyProtocolEngine(settings, frameBuffer ?? new FrameBuffer());
    }
}
