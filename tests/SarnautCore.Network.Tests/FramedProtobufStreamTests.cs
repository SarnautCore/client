using System.Buffers.Binary;
using Sarnaut.Protocol.V1;
using SarnautCore.Networking;
using Xunit;

namespace SarnautCore.Network.Tests;

public sealed class FramedProtobufStreamTests
{
    [Fact]
    public async Task WritesBigEndianLengthAndReadsTheSameMessage()
    {
        var storage = new MemoryStream();
        await using (var writer = new FramedProtobufStream(storage))
        {
            await writer.WriteAsync(new ClientHello
            {
                ProtocolVersion = ProtocolVersion._1,
                BuildId = "client-test",
            });
        }

        byte[] frame = storage.ToArray();
        Assert.Equal((uint)(frame.Length - sizeof(uint)), BinaryPrimitives.ReadUInt32BigEndian(frame));

        await using var reader = new FramedProtobufStream(new MemoryStream(frame));
        ClientHello decoded = await reader.ReadAsync(ClientHello.Parser);
        Assert.Equal(ProtocolVersion._1, decoded.ProtocolVersion);
        Assert.Equal("client-test", decoded.BuildId);
    }

    [Fact]
    public async Task RejectsOversizedIncomingFrameBeforeAllocatingPayload()
    {
        byte[] header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(header, FramedProtobufStream.MaxFrameSize + 1U);
        await using var reader = new FramedProtobufStream(new MemoryStream(header));

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await reader.ReadAsync(ClientHello.Parser));
    }
}
