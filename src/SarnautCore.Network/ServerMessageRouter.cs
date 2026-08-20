using Sarnaut.Protocol.V1;

namespace SarnautCore.Networking;

/// <summary>
/// Sends one <see cref="ServerMessage"/> to the handler for its oneof case.
/// </summary>
/// <remarks>
/// Routing is a lookup and nothing else: an unset or unrecognised case is
/// counted and offered to <see cref="Unrecognized"/> rather than thrown, so a
/// caller that reads a frame is never surprised by an exception from the
/// dispatch table itself.
///
/// The protocol rule that such a frame ends the session lives one layer up, in
/// <see cref="GameSession.ReadAndDispatchAsync"/>: ProtocolVersion is compared
/// for exact equality at both ends, so a case this build does not know is a
/// protocol violation and not a forward-compatibility event (ADR 0026).
/// Keeping the policy there is what lets a diagnostic tool drain a capture
/// without dying on the first frame it does not recognise.
/// </remarks>
public sealed class ServerMessageRouter
{
    public Action<SnapshotBatch>? SnapshotBatch { get; init; }

    public Action<SpawnEvent>? SpawnEvent { get; init; }

    public Action<DespawnEvent>? DespawnEvent { get; init; }

    public Action<CombatEvent>? CombatEvent { get; init; }

    public Action<DeathEvent>? DeathEvent { get; init; }

    public Action<LootOffer>? LootOffer { get; init; }

    public Action<LootResult>? LootResult { get; init; }

    public Action<InventoryUpdate>? InventoryUpdate { get; init; }

    public Action<QuestStateUpdate>? QuestStateUpdate { get; init; }

    public Action<Error>? Error { get; init; }

    /// <summary>Receives any frame no case handler claimed.</summary>
    public Action<ServerMessage>? Unrecognized { get; init; }

    /// <summary>Frames routed to <see cref="Unrecognized"/> so far.</summary>
    public int UnrecognizedCount { get; private set; }

    /// <summary>Routes one envelope and reports the case it carried.</summary>
    public ServerMessage.PayloadOneofCase Route(ServerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message.PayloadCase)
        {
            case ServerMessage.PayloadOneofCase.SnapshotBatch:
                SnapshotBatch?.Invoke(message.SnapshotBatch);
                break;
            case ServerMessage.PayloadOneofCase.SpawnEvent:
                SpawnEvent?.Invoke(message.SpawnEvent);
                break;
            case ServerMessage.PayloadOneofCase.DespawnEvent:
                DespawnEvent?.Invoke(message.DespawnEvent);
                break;
            case ServerMessage.PayloadOneofCase.CombatEvent:
                CombatEvent?.Invoke(message.CombatEvent);
                break;
            case ServerMessage.PayloadOneofCase.DeathEvent:
                DeathEvent?.Invoke(message.DeathEvent);
                break;
            case ServerMessage.PayloadOneofCase.LootOffer:
                LootOffer?.Invoke(message.LootOffer);
                break;
            case ServerMessage.PayloadOneofCase.LootResult:
                LootResult?.Invoke(message.LootResult);
                break;
            case ServerMessage.PayloadOneofCase.InventoryUpdate:
                InventoryUpdate?.Invoke(message.InventoryUpdate);
                break;
            case ServerMessage.PayloadOneofCase.QuestStateUpdate:
                QuestStateUpdate?.Invoke(message.QuestStateUpdate);
                break;
            case ServerMessage.PayloadOneofCase.Error:
                Error?.Invoke(message.Error);
                break;
            default:
                UnrecognizedCount++;
                Unrecognized?.Invoke(message);
                return ServerMessage.PayloadOneofCase.None;
        }

        return message.PayloadCase;
    }
}
