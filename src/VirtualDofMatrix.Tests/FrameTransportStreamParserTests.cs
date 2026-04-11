using VirtualDofMatrix.App.Transport;
using Xunit;

namespace VirtualDofMatrix.Tests;

public sealed class FrameTransportStreamParserTests
{
    [Fact]
    public void ParseNext_ShouldSkipGarbageBeforeMagicAndReturnFrame()
    {
        var parser = new FrameTransportStreamParser();
        var validFrame = BuildFrame(messageType: 1, sequence: 7, payload: [0x10, 0x20, 0x30]);
        var garbagePlusFrame = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00 }
            .Concat(validFrame)
            .ToArray();

        parser.Append(garbagePlusFrame);

        var result = parser.ParseNext();

        Assert.Equal(FrameTransportParseStatus.ParsedFrame, result.Status);
        Assert.Equal((byte)1, result.Frame.MessageType);
        Assert.Equal((ulong)7, result.Frame.RawSequence);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, result.Frame.Payload);
    }

    [Fact]
    public void ParseNext_ShouldHandleSplitHeaderAndPayloadAcrossReads()
    {
        var parser = new FrameTransportStreamParser();
        var validFrame = BuildFrame(messageType: 1, sequence: 11, payload: [0x01, 0x02, 0x03, 0x04]);

        parser.Append(validFrame.AsSpan(0, 6));
        Assert.Equal(FrameTransportParseStatus.NeedMoreData, parser.ParseNext().Status);

        parser.Append(validFrame.AsSpan(6, 5));
        Assert.Equal(FrameTransportParseStatus.NeedMoreData, parser.ParseNext().Status);

        parser.Append(validFrame.AsSpan(11));
        var result = parser.ParseNext();

        Assert.Equal(FrameTransportParseStatus.ParsedFrame, result.Status);
        Assert.Equal((ulong)11, result.Frame.RawSequence);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, result.Frame.Payload);
    }

    [Fact]
    public void ParseNext_ShouldRecoverAfterInvalidFrameAndParseFollowingValidFrame()
    {
        var parser = new FrameTransportStreamParser();
        var invalidFrame = BuildFrame(messageType: 9, sequence: 1, payload: []);
        var validFrame = BuildFrame(messageType: 2, sequence: 2, payload: [0x41]);

        parser.Append(invalidFrame.Concat(validFrame).ToArray());

        var invalidResult = parser.ParseNext();
        var validResult = parser.ParseNext();

        Assert.Equal(FrameTransportParseStatus.DroppedInvalidData, invalidResult.Status);
        Assert.Contains("Discarded invalid frame header", invalidResult.Warning);
        Assert.Equal(FrameTransportParseStatus.ParsedFrame, validResult.Status);
        Assert.Equal((byte)2, validResult.Frame.MessageType);
        Assert.Equal((ulong)2, validResult.Frame.RawSequence);
        Assert.Equal(new byte[] { 0x41 }, validResult.Frame.Payload);
    }

    private static byte[] BuildFrame(byte messageType, int sequence, byte[] payload)
    {
        var bytes = new byte[13 + payload.Length];
        bytes[0] = (byte)'V';
        bytes[1] = (byte)'D';
        bytes[2] = (byte)'M';
        bytes[3] = (byte)'F';
        bytes[4] = messageType;
        BitConverter.GetBytes(sequence).CopyTo(bytes, 5);
        BitConverter.GetBytes(payload.Length).CopyTo(bytes, 9);
        payload.CopyTo(bytes, 13);
        return bytes;
    }
}
