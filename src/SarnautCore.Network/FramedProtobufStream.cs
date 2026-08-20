using System.Buffers.Binary;
using Google.Protobuf;

namespace SarnautCore.Networking;

public sealed class FramedProtobufStream(Stream stream) : IAsyncDisposable
{
    public const int MaxFrameSize = 1 << 20;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async ValueTask WriteAsync(IMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        byte[] payload = message.ToByteArray();
        if (payload.Length > MaxFrameSize)
        {
            throw new InvalidDataException($"Protobuf frame is {payload.Length} bytes; maximum is {MaxFrameSize}.");
        }

        byte[] header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask<T> ReadAsync<T>(MessageParser<T> parser, CancellationToken cancellationToken = default)
        where T : IMessage<T>
    {
        ArgumentNullException.ThrowIfNull(parser);
        byte[] header = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaxFrameSize)
        {
            throw new InvalidDataException($"Protobuf frame is {length} bytes; maximum is {MaxFrameSize}.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return parser.ParseFrom(payload);
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        await stream.DisposeAsync().ConfigureAwait(false);
    }
}
