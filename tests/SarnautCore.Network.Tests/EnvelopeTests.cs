using Google.Protobuf;
using Sarnaut.Protocol.V1;
using SarnautCore.Networking;
using Xunit;

namespace SarnautCore.Network.Tests;

public sealed class EnvelopeTests
{
    [Fact]
    public async Task ClientEnvelopeSurvivesAFramedRoundTrip()
    {
        var storage = new MemoryStream();
        var sent = new ClientMessage
        {
            ClientSeq = 42,
            MoveIntent = new ClientMoveIntent
            {
                Seq = 42,
                Input = new Vec3 { X = 1, Y = -0.5f },
                Heading = 1.25f,
                DtSeconds = 0.1f,
            },
        };

        await using (var writer = new FramedProtobufStream(storage))
        {
            await writer.WriteAsync(sent);
        }

        await using var reader = new FramedProtobufStream(new MemoryStream(storage.ToArray()));
        ClientMessage received = await reader.ReadAsync(ClientMessage.Parser);

        Assert.Equal(ClientMessage.PayloadOneofCase.MoveIntent, received.PayloadCase);
        Assert.Equal((ulong)42, received.ClientSeq);
        Assert.Equal(1, received.MoveIntent.Input.X);
        Assert.Equal(1.25f, received.MoveIntent.Heading);
        Assert.Equal(sent, received);
    }

    [Fact]
    public async Task ServerEnvelopeSurvivesAFramedRoundTrip()
    {
        var storage = new MemoryStream();
        var batch = new SnapshotBatch { ServerTick = 900 };
        batch.Entities.Add(new EntitySnapshot
        {
            EntityId = 3,
            Kind = EntityKind.Npc,
            Position = new Vec3 { X = 2, Y = 3, Z = 4 },
            Velocity = new Vec3(),
            AnimationState = AnimationState.Idle,
            ContentId = "mob.inst-league1.earth-elemental.earth-elemental2-1",
            NameKey = "mob.earth-elemental.name",
            Level = 2,
            Faction = "faction.wild",
            Health = 84,
            MaxHealth = 120,
            Alive = true,
        });

        await using (var writer = new FramedProtobufStream(storage))
        {
            await writer.WriteAsync(new ServerMessage { ServerTick = 900, SnapshotBatch = batch });
        }

        await using var reader = new FramedProtobufStream(new MemoryStream(storage.ToArray()));
        ServerMessage received = await reader.ReadAsync(ServerMessage.Parser);

        Assert.Equal(ServerMessage.PayloadOneofCase.SnapshotBatch, received.PayloadCase);
        Assert.Equal((ulong)900, received.ServerTick);
        EntitySnapshot entity = Assert.Single(received.SnapshotBatch.Entities);
        Assert.Equal("mob.inst-league1.earth-elemental.earth-elemental2-1", entity.ContentId);
        Assert.Equal("mob.earth-elemental.name", entity.NameKey);
        Assert.Equal((uint)2, entity.Level);
        Assert.Equal("faction.wild", entity.Faction);
        Assert.Equal(84, entity.Health);
        Assert.Equal(120, entity.MaxHealth);
        Assert.True(entity.Alive);
    }

    [Fact]
    public void RouterSendsEachCaseToItsOwnHandler()
    {
        var routed = new List<string>();
        var router = new ServerMessageRouter
        {
            SnapshotBatch = batch => routed.Add($"snapshot:{batch.ServerTick}"),
            SpawnEvent = spawn => routed.Add($"spawn:{spawn.Entity.EntityId}"),
            DespawnEvent = despawn => routed.Add($"despawn:{despawn.EntityId}"),
            CombatEvent = _ => routed.Add("combat"),
            DeathEvent = _ => routed.Add("death"),
            LootOffer = _ => routed.Add("loot_offer"),
            LootResult = _ => routed.Add("loot_result"),
            InventoryUpdate = _ => routed.Add("inventory"),
            QuestStateUpdate = _ => routed.Add("quest_state_update"),
            Error = failure => routed.Add($"error:{failure.Code}"),
        };

        Assert.Equal(
            ServerMessage.PayloadOneofCase.SnapshotBatch,
            router.Route(new ServerMessage { SnapshotBatch = new SnapshotBatch { ServerTick = 5 } }));
        Assert.Equal(
            ServerMessage.PayloadOneofCase.SpawnEvent,
            router.Route(new ServerMessage
            {
                SpawnEvent = new SpawnEvent { Entity = new EntitySnapshot { EntityId = 41 } },
            }));
        Assert.Equal(
            ServerMessage.PayloadOneofCase.DespawnEvent,
            router.Route(new ServerMessage { DespawnEvent = new DespawnEvent { EntityId = 39 } }));
        Assert.Equal(
            ServerMessage.PayloadOneofCase.CombatEvent,
            router.Route(new ServerMessage { CombatEvent = new CombatEvent() }));
        Assert.Equal(
            ServerMessage.PayloadOneofCase.DeathEvent,
            router.Route(new ServerMessage { DeathEvent = new DeathEvent() }));
        Assert.Equal(
            ServerMessage.PayloadOneofCase.LootOffer,
            router.Route(new ServerMessage { LootOffer = new LootOffer() }));
        Assert.Equal(
            ServerMessage.PayloadOneofCase.LootResult,
            router.Route(new ServerMessage { LootResult = new LootResult() }));
        Assert.Equal(
            ServerMessage.PayloadOneofCase.InventoryUpdate,
            router.Route(new ServerMessage { InventoryUpdate = new InventoryUpdate() }));
        Assert.Equal(
            ServerMessage.PayloadOneofCase.QuestStateUpdate,
            router.Route(new ServerMessage { QuestStateUpdate = new QuestStateUpdate() }));
        Assert.Equal(
            ServerMessage.PayloadOneofCase.Error,
            router.Route(new ServerMessage
            {
                Error = new Error { Code = ErrorCode.UnsupportedMessage, Detail = "no case" },
            }));

        Assert.Equal(
            [
                "snapshot:5",
                "spawn:41",
                "despawn:39",
                "combat",
                "death",
                "loot_offer",
                "loot_result",
                "inventory",
                "quest_state_update",
                "error:UnsupportedMessage",
            ],
            routed);
        Assert.Equal(0, router.UnrecognizedCount);
    }

    [Fact]
    public void RouterIgnoresAnUnsetOrUnknownCaseWithoutThrowing()
    {
        var unrecognized = new List<ulong>();
        var router = new ServerMessageRouter
        {
            SnapshotBatch = _ => Assert.Fail("a frame with no case reached the snapshot handler"),
            Unrecognized = message => unrecognized.Add(message.ServerTick),
        };

        // No case set at all.
        Assert.Equal(
            ServerMessage.PayloadOneofCase.None,
            router.Route(new ServerMessage { ServerTick = 11 }));

        // A case this build's schema does not define. Field 20 is the next free
        // oneof number: 0xa2 0x01 is its length-delimited tag and 0x00 an empty
        // payload, so the frame decodes with PayloadCase unset and the case in
        // unknown fields, which is exactly how a newer peer looks from here.
        var future = new ServerMessage { ServerTick = 12 };
        ServerMessage decoded = ServerMessage.Parser.ParseFrom(
            Concat(future.ToByteArray(), [0xa2, 0x01, 0x00]));
        Assert.Equal(ServerMessage.PayloadOneofCase.None, router.Route(decoded));

        Assert.Equal([(ulong)11, 12], unrecognized);
        Assert.Equal(2, router.UnrecognizedCount);
    }

    private static byte[] Concat(byte[] left, byte[] right)
    {
        byte[] result = new byte[left.Length + right.Length];
        left.CopyTo(result, 0);
        right.CopyTo(result, left.Length);
        return result;
    }
}
