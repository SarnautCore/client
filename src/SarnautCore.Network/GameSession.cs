using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using Sarnaut.Protocol.V1;

namespace SarnautCore.Networking;

// Every System.Net.Quic call is guarded by QuicConnection.IsSupported. CA1416 does not infer that guard.
#pragma warning disable CA1416

public sealed class GameSession : IAsyncDisposable
{
    private const string ApplicationProtocol = "sarnaut/1";

    private readonly QuicConnection _connection;
    private readonly FramedProtobufStream _framedStream;

    private GameSession(
        QuicConnection connection,
        FramedProtobufStream framedStream,
        ServerHello serverHello,
        EnterZoneResponse enteredZone)
    {
        _connection = connection;
        _framedStream = framedStream;
        ServerHello = serverHello;
        EnteredZone = enteredZone;
    }

    public ServerHello ServerHello { get; }

    public EnterZoneResponse EnteredZone { get; }

    public string TransportMode => "QUIC ordered stream (System.Net.Quic/MsQuic)";

    public static async Task<GameSession> ConnectAsync(
        GameEndpoint endpoint,
        string zoneId,
        string buildId,
        bool allowUntrustedDevelopmentCertificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        if (!QuicConnection.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "System.Net.Quic is unavailable. Windows requires Windows 11 or Server 2022 and a .NET runtime with MsQuic.");
        }

        var sslOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [new SslApplicationProtocol(ApplicationProtocol)],
            EnabledSslProtocols = SslProtocols.Tls13,
            TargetHost = endpoint.Host,
        };
        if (allowUntrustedDevelopmentCertificate)
        {
            sslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }

        var connectionOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new DnsEndPoint(endpoint.Host, endpoint.Port),
            ClientAuthenticationOptions = sslOptions,
            DefaultCloseErrorCode = 0,
            DefaultStreamErrorCode = 0,
        };

        QuicConnection? connection = null;
        FramedProtobufStream? framedStream = null;
        try
        {
            connection = await QuicConnection.ConnectAsync(connectionOptions, cancellationToken).ConfigureAwait(false);
            QuicStream stream = await connection
                .OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken)
                .ConfigureAwait(false);
            framedStream = new FramedProtobufStream(stream);

            await framedStream.WriteAsync(new ClientHello
            {
                ProtocolVersion = ProtocolVersion._1,
                BuildId = buildId,
            }, cancellationToken).ConfigureAwait(false);
            ServerHello serverHello = await framedStream
                .ReadAsync(ServerHello.Parser, cancellationToken)
                .ConfigureAwait(false);
            if (serverHello.ProtocolVersion != ProtocolVersion._1)
            {
                throw new InvalidDataException(
                    $"Server protocol {serverHello.ProtocolVersion} does not match {ProtocolVersion._1}.");
            }

            await framedStream.WriteAsync(new EnterZoneRequest { ZoneId = zoneId }, cancellationToken)
                .ConfigureAwait(false);
            EnterZoneResponse enteredZone = await framedStream
                .ReadAsync(EnterZoneResponse.Parser, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(enteredZone.ZoneId, zoneId, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Server entered zone '{enteredZone.ZoneId}', expected '{zoneId}'.");
            }

            return new GameSession(connection, framedStream, serverHello, enteredZone);
        }
        catch
        {
            if (framedStream is not null)
            {
                await framedStream.DisposeAsync().ConfigureAwait(false);
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public ValueTask SendMoveIntentAsync(ClientMoveIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return _framedStream.WriteAsync(intent, cancellationToken);
    }

    public ValueTask<SnapshotBatch> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return _framedStream.ReadAsync(SnapshotBatch.Parser, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _framedStream.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

#pragma warning restore CA1416
