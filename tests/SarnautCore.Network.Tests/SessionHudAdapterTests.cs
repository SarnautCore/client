using Google.Protobuf;
using Sarnaut.Protocol.V1;
using SarnautCore.NativeHud;
using SarnautCore.Networking;
using Xunit;

namespace SarnautCore.Network.Tests;

public sealed class SessionHudAdapterTests
{
    [Fact]
    public void CapabilitiesDescribeOnlyWhatTheCurrentSessionCanCarry()
    {
        var adapter = NewAdapter();

        Assert.Equal(
            HudEventFamilies.Units | HudEventFamilies.CombatFeedback | HudEventFamilies.QuestTracker,
            adapter.Capabilities.Events);
        Assert.Equal(
            HudCommandFamilies.ActivateAction |
            HudCommandFamilies.SelectWorldEntity |
            HudCommandFamilies.InteractWorldEntity,
            adapter.Capabilities.Commands);
        Assert.False(adapter.Capabilities.Events.HasFlag(HudEventFamilies.Chat));
        Assert.False(adapter.Capabilities.Commands.HasFlag(HudCommandFamilies.SubmitChat));
    }

    [Fact]
    public void LatestSnapshotCoalescesPerEntityAndReportsTheDrop()
    {
        var adapter = NewAdapter(epoch: 7);

        Assert.Equal(SessionHudObservation.Projected, adapter.Observe(Snapshot(10, Entity(42, health: 90))));
        Assert.Equal(SessionHudObservation.Projected, adapter.Observe(Snapshot(11, Entity(42, health: 75))));

        HudEvent[] events = Read(adapter, 4, out HudSessionRead read);

        HudEvent item = Assert.Single(events);
        Assert.Equal(HudEventKind.UnitChanged, item.Kind);
        Assert.Equal((ulong)42, item.EntityId);
        Assert.Equal(75, item.Value);
        Assert.Equal(new HudStamp(7, 11, 1), item.Stamp);
        Assert.Equal(1, read.DroppedCount);
        Assert.Equal(HudSessionState.Open, read.State);
    }

    [Fact]
    public void SnapshotCoalescingKeepsDistinctEntityKeys()
    {
        var adapter = NewAdapter();

        adapter.Observe(Snapshot(4, Entity(1, health: 10), Entity(2, health: 20)));
        adapter.Observe(Snapshot(5, Entity(1, health: 30)));

        HudEvent[] events = Read(adapter, 8, out HudSessionRead read);

        Assert.Equal(2, events.Length);
        Assert.Equal([(ulong)2, 1], events.Select(item => item.EntityId));
        Assert.Equal([20, 30], events.Select(item => item.Value));
        Assert.Equal(1, read.DroppedCount);
    }

    [Fact]
    public void ReliableEventsStayInPublishOrderWhileSnapshotsMergeByAuthority()
    {
        var adapter = NewAdapter(ownEntityId: 9);

        adapter.Observe(Spawn(5, Entity(9, health: 100)));
        adapter.Observe(Snapshot(6, Entity(9, health: 80)));
        adapter.Observe(new ServerMessage
        {
            ServerTick = 7,
            CombatEvent = new CombatEvent
            {
                CasterId = 22,
                TargetId = 9,
                AbilityId = "ability.hit",
                Damage = 20,
                TargetHealth = 60,
                TargetMaxHealth = 100,
                Rejection = AbilityRejection.None,
            },
        });

        HudEvent[] events = Read(adapter, 8, out _);

        Assert.Equal(
            [
                HudEventKind.UnitChanged,
                HudEventKind.UnitChanged,
                HudEventKind.FeedbackRaised,
                HudEventKind.UnitChanged,
            ],
            events.Select(item => item.Kind));
        Assert.Equal([(ulong)5, 6, 7, 7], events.Select(item => item.Stamp.Revision));
        Assert.Equal(HudFeedbackKind.Avatar, events[2].FeedbackKind);
        Assert.Equal(20, events[2].Value);
        Assert.Equal(60, events[3].Value);
    }

    [Fact]
    public void RejectedCombatDoesNotOverwriteAuthoritativeUnitHealth()
    {
        var adapter = NewAdapter();
        adapter.Observe(Snapshot(1, Entity(1, health: 80)));
        _ = Read(adapter, 2, out _);

        adapter.Observe(new ServerMessage
        {
            ServerTick = 2,
            CombatEvent = new CombatEvent
            {
                CasterId = 1,
                TargetId = 1,
                AbilityId = "ability.refused",
                Rejection = AbilityRejection.OnCooldown,
            },
        });

        Assert.Empty(Read(adapter, 2, out HudSessionRead read));
        Assert.Equal(HudSessionState.Open, read.State);
    }

    [Fact]
    public void DespawnIsReliableAndAnOlderSnapshotCannotResurrectTheUnit()
    {
        var adapter = NewAdapter();
        adapter.Observe(Spawn(10, Entity(42)));
        _ = Read(adapter, 4, out _);

        adapter.Observe(new ServerMessage
        {
            ServerTick = 12,
            DespawnEvent = new DespawnEvent { EntityId = 42 },
        });
        adapter.Observe(Snapshot(11, Entity(42, health: 50)));

        HudEvent[] events = Read(adapter, 4, out HudSessionRead read);

        HudEvent removed = Assert.Single(events);
        Assert.Equal(HudEventKind.UnitRemoved, removed.Kind);
        Assert.Equal((ulong)42, removed.EntityId);
        Assert.Equal(1, read.DroppedCount);
    }

    [Fact]
    public void QuestTrackerProjectionIsAtomicOrderedAndDoesNotConsumeTheEnvelope()
    {
        var adapter = NewAdapter();
        var message = new ServerMessage
        {
            ServerTick = 30,
            QuestStateUpdate = new QuestStateUpdate
            {
                QuestId = "quest.league.first",
                State = QuestState.InProgress,
                Refusal = QuestRefusal.None,
            },
        };
        message.QuestStateUpdate.Objectives.Add(new QuestObjectiveProgress
        {
            Index = 0,
            Counter = 2,
            Limit = 3,
            ShowCount = true,
            CounterKey = "quest.league.first.kills",
        });
        message.QuestStateUpdate.Objectives.Add(new QuestObjectiveProgress
        {
            Index = 2,
            Counter = 1,
            Limit = 1,
            CounterKey = "quest.league.first.visit",
        });
        byte[] before = message.ToByteArray();

        Assert.Equal(SessionHudObservation.Projected, adapter.Observe(message));
        Assert.Equal(before, message.ToByteArray());
        HudEvent item = Assert.Single(Read(adapter, 4, out _));

        Assert.Equal(HudEventKind.QuestTracked, item.Kind);
        Assert.NotNull(item.Quest);
        Assert.Equal("quest.league.first", item.Quest.QuestId.Value);
        Assert.Equal("quest.league.first.title", item.Quest.TitleId.Value);
        Assert.False(item.Quest.Completable);
        Assert.Equal([0U, 2U], item.Quest.Objectives.ToArray().Select(objective => objective.Index));
        Assert.Equal([2, 1], item.Quest.Objectives.ToArray().Select(objective => objective.Current));

        QuestStateUpdate? routed = null;
        var nextConsumer = new ServerMessageRouter { QuestStateUpdate = update => routed = update };
        nextConsumer.Route(message);
        Assert.Same(message.QuestStateUpdate, routed);
    }

    [Fact]
    public void TurnInUntracksTheQuestAndRaisesExperienceFeedbackInOrder()
    {
        var adapter = NewAdapter(ownEntityId: 77);
        var message = new ServerMessage
        {
            ServerTick = 44,
            QuestStateUpdate = new QuestStateUpdate
            {
                QuestId = "quest.done",
                State = QuestState.TurnedIn,
                Refusal = QuestRefusal.None,
                Experience = 125,
            },
        };

        adapter.Observe(message);
        HudEvent[] events = Read(adapter, 4, out _);

        Assert.Equal([HudEventKind.QuestUntracked, HudEventKind.FeedbackRaised], events.Select(item => item.Kind));
        Assert.Equal(HudFeedbackKind.Experience, events[1].FeedbackKind);
        Assert.Equal((ulong)77, events[1].EntityId);
        Assert.Equal(125, events[1].Value);
    }

    [Fact]
    public void RefusedQuestUpdateDoesNotChangeTheTracker()
    {
        var adapter = NewAdapter();
        var message = new ServerMessage
        {
            ServerTick = 8,
            QuestStateUpdate = new QuestStateUpdate
            {
                QuestId = "quest.refused",
                State = QuestState.Accepted,
                Refusal = QuestRefusal.LogFull,
            },
        };

        Assert.Equal(SessionHudObservation.Observed, adapter.Observe(message));
        Assert.Empty(Read(adapter, 4, out HudSessionRead read));
        Assert.Equal(HudSessionState.Open, read.State);
    }

    [Fact]
    public void NonHudMessagesRemainByteExactForTheNextConsumer()
    {
        ServerMessage[] messages =
        [
            new() { ServerTick = 1, LootOffer = new LootOffer { CorpseEntityId = 9, Money = 5 } },
            new() { ServerTick = 2, LootResult = new LootResult { CorpseEntityId = 9, Refusal = LootRefusal.BagFull } },
            new() { ServerTick = 3, InventoryUpdate = new InventoryUpdate { Currency = 19 } },
            new() { ServerTick = 4, Error = new Sarnaut.Protocol.V1.Error { Code = ErrorCode.RateLimited, Detail = "slow" } },
        ];
        var adapter = NewAdapter();

        foreach (ServerMessage message in messages)
        {
            byte[] before = message.ToByteArray();
            Assert.Equal(SessionHudObservation.NotSubscribed, adapter.Observe(message));
            Assert.Equal(before, message.ToByteArray());
        }

        Assert.Empty(Read(adapter, 8, out HudSessionRead read));
        Assert.Equal(HudSessionState.Open, read.State);
    }

    [Fact]
    public void SnapshotCapacityFaultsWithoutPublishingAPartialBatch()
    {
        var adapter = NewAdapter(options: new SessionHudAdapterOptions(SnapshotEntityCapacity: 1));

        Assert.Equal(
            SessionHudObservation.Terminal,
            adapter.Observe(Snapshot(1, Entity(1), Entity(2))));

        Assert.Empty(Read(adapter, 8, out HudSessionRead read));
        Assert.Equal(HudSessionState.Faulted, read.State);
        Assert.Equal(SessionHudFaultCode.SnapshotEntityCapacityExceeded, adapter.Fault?.Code);
    }

    [Fact]
    public void ReliableOverflowFaultsWithoutPublishingHalfOfACombatProjection()
    {
        var adapter = NewAdapter(
            ownEntityId: 5,
            options: new SessionHudAdapterOptions(ReliableEventCapacity: 1));
        adapter.Observe(Snapshot(1, Entity(5)));
        _ = Read(adapter, 2, out _);

        SessionHudObservation result = adapter.Observe(new ServerMessage
        {
            ServerTick = 2,
            CombatEvent = new CombatEvent
            {
                CasterId = 6,
                TargetId = 5,
                AbilityId = "ability.double",
                Damage = 10,
                TargetHealth = 90,
                TargetMaxHealth = 100,
                Rejection = AbilityRejection.None,
            },
        });

        Assert.Equal(SessionHudObservation.Terminal, result);
        Assert.Empty(Read(adapter, 4, out HudSessionRead read));
        Assert.Equal(HudSessionState.Faulted, read.State);
        Assert.Equal(SessionHudFaultCode.ReliableEventQueueFull, adapter.Fault?.Code);
    }

    [Fact]
    public void TypedCommandsStayBoundedAndInOrderForTheSessionLoop()
    {
        var adapter = NewAdapter(options: new SessionHudAdapterOptions(CommandCapacity: 3));
        HudCommand select = HudCommand.SelectWorldEntity(41);
        HudCommand interact = HudCommand.InteractWorldEntity(41);
        HudCommand activate = HudCommand.ActivateAction(3, new HudId("ability.strike"));

        Assert.True(adapter.TryWrite(select));
        Assert.True(adapter.TryWrite(interact));
        Assert.True(adapter.TryWrite(activate));
        Assert.True(adapter.TryTakeCommand(out HudCommand first));
        Assert.True(adapter.TryTakeCommand(out HudCommand second));
        Assert.True(adapter.TryTakeCommand(out HudCommand third));
        Assert.Equal([select, interact, activate], [first, second, third]);
        Assert.False(adapter.TryTakeCommand(out _));
    }

    [Fact]
    public void CommandOverflowFaultsAndCancelsPreviouslyQueuedCommands()
    {
        var adapter = NewAdapter(options: new SessionHudAdapterOptions(CommandCapacity: 1));

        Assert.True(adapter.TryWrite(HudCommand.SelectWorldEntity(2)));
        Assert.False(adapter.TryWrite(HudCommand.InteractWorldEntity(2)));

        Assert.Equal(HudSessionState.Faulted, adapter.State);
        Assert.Equal(SessionHudFaultCode.CommandQueueFull, adapter.Fault?.Code);
        Assert.False(adapter.TryTakeCommand(out _));
    }

    [Fact]
    public void UnsupportedChatCommandFailsClosedUntilTheWireSupportsIt()
    {
        var adapter = NewAdapter();

        Assert.False(adapter.TryWrite(HudCommand.SubmitChat(new HudId("hello"))));

        Assert.Equal(HudSessionState.Faulted, adapter.State);
        Assert.Equal(SessionHudFaultCode.UnsupportedCommand, adapter.Fault?.Code);
    }

    [Fact]
    public void CloseAndTransportFaultAreVisibleThroughTheNarrowSessionPort()
    {
        var closed = NewAdapter();
        closed.Close();
        Assert.Equal(HudSessionState.Closed, closed.Read([]).State);
        Assert.False(closed.TryWrite(HudCommand.SelectWorldEntity(2)));

        var faulted = NewAdapter();
        faulted.ReportTransportFault("peer reset");
        Assert.Equal(HudSessionState.Faulted, faulted.Read([]).State);
        Assert.Equal(new SessionHudFault(SessionHudFaultCode.Transport, "peer reset"), faulted.Fault);
    }

    [Fact]
    public void SessionLoopCanBindAdmissionIdentityBeforeTrafficStarts()
    {
        var adapter = new SessionHudAdapter(3);

        adapter.BindOwnEntity(55);
        adapter.BindOwnEntity(55);
        Assert.Equal((ulong)55, adapter.OwnEntityId);
        Assert.Equal(SessionHudObservation.Projected, adapter.Observe(Snapshot(1, Entity(55))));
        Assert.Throws<InvalidOperationException>(() => adapter.BindOwnEntity(56));
    }

    [Fact]
    public void AuthorityAndTransientIdentifiersAreDeterministicForTheSameSessionStream()
    {
        HudEvent[] left = RunDeterministicStream();
        HudEvent[] right = RunDeterministicStream();

        Assert.Equal(left.Select(item => item.Kind), right.Select(item => item.Kind));
        Assert.Equal(left.Select(item => item.Stamp), right.Select(item => item.Stamp));
        Assert.Equal(left.Select(item => item.EventId), right.Select(item => item.EventId));
        Assert.All(left, item => Assert.Equal((uint)19, item.Stamp.SourceEpoch));
    }

    [Fact]
    public void SnapshotAuthorityMismatchFaultsTheAdapter()
    {
        var adapter = NewAdapter();
        ServerMessage message = Snapshot(8, Entity(1));
        message.SnapshotBatch.ServerTick = 7;

        Assert.Equal(SessionHudObservation.Terminal, adapter.Observe(message));
        Assert.Equal(SessionHudFaultCode.InvalidServerPayload, adapter.Fault?.Code);
    }

    private static HudEvent[] RunDeterministicStream()
    {
        var adapter = NewAdapter(epoch: 19, ownEntityId: 2);
        adapter.Observe(Spawn(3, Entity(2)));
        adapter.Observe(new ServerMessage
        {
            ServerTick = 4,
            CombatEvent = new CombatEvent
            {
                CasterId = 7,
                TargetId = 2,
                AbilityId = "ability.test",
                Damage = 9,
                TargetHealth = 91,
                TargetMaxHealth = 100,
                Rejection = AbilityRejection.None,
            },
        });
        return Read(adapter, 8, out _);
    }

    private static SessionHudAdapter NewAdapter(
        uint epoch = 1,
        ulong ownEntityId = 1,
        SessionHudAdapterOptions? options = null) => new(epoch, ownEntityId, options);

    private static ServerMessage Snapshot(ulong tick, params EntitySnapshot[] entities)
    {
        var batch = new SnapshotBatch { ServerTick = tick, ChunkCount = 1 };
        batch.Entities.Add(entities);
        return new ServerMessage { ServerTick = tick, SnapshotBatch = batch };
    }

    private static ServerMessage Spawn(ulong tick, EntitySnapshot entity) => new()
    {
        ServerTick = tick,
        SpawnEvent = new SpawnEvent { Entity = entity },
    };

    private static EntitySnapshot Entity(ulong id, int health = 100) => new()
    {
        EntityId = id,
        Kind = EntityKind.Npc,
        ContentId = $"mob.{id}",
        NameKey = $"mob.{id}.name",
        Level = 2,
        Health = health,
        MaxHealth = 100,
        Alive = health > 0,
    };

    private static HudEvent[] Read(SessionHudAdapter adapter, int capacity, out HudSessionRead read)
    {
        var buffer = new HudEvent[capacity];
        read = adapter.Read(buffer);
        return buffer[..read.Count];
    }
}
