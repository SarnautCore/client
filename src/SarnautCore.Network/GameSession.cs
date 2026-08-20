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

    /// <summary>Connects, agrees a protocol version and content pack, and enters a zone.</summary>
    /// <param name="packId">
    /// The runtime pack digest this build loaded (ADR 0029). Empty claims
    /// nothing and skips the check, which a shard accepts only while it carries
    /// no pack of its own.
    /// </param>
    /// <param name="ticket">
    /// The opaque single-use shard ticket obtained from the auth service
    /// (ADR 0030). Empty until that service exists.
    /// </param>
    public static async Task<GameSession> ConnectAsync(
        GameEndpoint endpoint,
        string zoneId,
        string buildId,
        bool allowUntrustedDevelopmentCertificate,
        string packId = "",
        string ticket = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        ArgumentNullException.ThrowIfNull(packId);
        ArgumentNullException.ThrowIfNull(ticket);
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
                PackId = packId,
            }, cancellationToken).ConfigureAwait(false);
            ServerHello serverHello = await framedStream
                .ReadAsync(ServerHello.Parser, cancellationToken)
                .ConfigureAwait(false);
            if (serverHello.ProtocolVersion != ProtocolVersion._1)
            {
                throw new InvalidDataException(
                    $"Server protocol {serverHello.ProtocolVersion} does not match {ProtocolVersion._1}.");
            }

            // Content identity is checked here, before EnterZoneRequest is sent.
            // Two peers that agree on message shape and disagree on content
            // tables produce plausible-looking wrong gameplay, which is far more
            // expensive to diagnose than a refused connection (ADR 0027).
            if (packId.Length > 0 && !string.Equals(serverHello.PackId, packId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Server content pack '{serverHello.PackId}' does not match client pack '{packId}'.");
            }

            await framedStream
                .WriteAsync(new EnterZoneRequest { ZoneId = zoneId, Ticket = ticket }, cancellationToken)
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

    /// <summary>Writes one client envelope. Every post-handshake frame is one of these.</summary>
    public ValueTask SendAsync(ClientMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _framedStream.WriteAsync(message, cancellationToken);
    }

    public ValueTask SendMoveIntentAsync(ClientMoveIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return SendAsync(
            new ClientMessage { ClientSeq = intent.Seq, MoveIntent = intent },
            cancellationToken);
    }

    /// <summary>
    /// Asks for a clean exit, so the shard's save checkpoint runs ahead of the
    /// disconnect rather than racing it.
    /// </summary>
    public ValueTask SendLogoutAsync(CancellationToken cancellationToken = default)
    {
        return SendAsync(new ClientMessage { Logout = new Logout() }, cancellationToken);
    }

    /// <summary>Reads one server envelope, whatever case it carries.</summary>
    public ValueTask<ServerMessage> ReadAsync(CancellationToken cancellationToken = default)
    {
        return _framedStream.ReadAsync(ServerMessage.Parser, cancellationToken);
    }

    /// <summary>
    /// Reads and routes server envelopes until the token is cancelled.
    /// </summary>
    /// <remarks>
    /// A frame whose case this build does not know ends the session: both ends
    /// compare ProtocolVersion for exact equality, so there is no version in
    /// which such a frame is legitimate (ADR 0026). The routing table itself
    /// stays total; only this loop refuses.
    /// </remarks>
    public async Task ReadAndDispatchAsync(
        ServerMessageRouter router,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(router);
        while (!cancellationToken.IsCancellationRequested)
        {
            ServerMessage message = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (router.Route(message) == ServerMessage.PayloadOneofCase.None)
            {
                throw new InvalidDataException(
                    $"Server message carries no case this build recognises (server_tick {message.ServerTick}).");
            }
        }
    }

    /// <summary>
    /// Reads server envelopes until one carries a snapshot batch.
    /// </summary>
    /// <remarks>
    /// A typed refusal is raised as an exception; any other case is skipped,
    /// because a snapshot reader is not the place to handle combat.
    /// </remarks>
    public async ValueTask<SnapshotBatch> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ServerMessage message = await ReadAsync(cancellationToken).ConfigureAwait(false);
            switch (message.PayloadCase)
            {
                case ServerMessage.PayloadOneofCase.SnapshotBatch:
                    return message.SnapshotBatch;
                case ServerMessage.PayloadOneofCase.Error:
                    throw new InvalidDataException(
                        $"Server refused the session: {message.Error.Code} {message.Error.Detail}");
                default:
                    continue;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _framedStream.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

#pragma warning restore CA1416
