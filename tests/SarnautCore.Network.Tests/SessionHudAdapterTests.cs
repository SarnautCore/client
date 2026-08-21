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
            HudEventFamilies.ActionSlots |
            HudEventFamilies.TargetSelection |
            HudEventFamilies.Units |
            HudEventFamilies.CombatFeedback |
            HudEventFamilies.QuestTracker |
            HudEventFamilies.Inventory |
            HudEventFamilies.Loot |
            HudEventFamilies.QuestLog |
            HudEventFamilies.QuestInfo |
            HudEventFamilies.Character,
            adapter.Capabilities.Events);
        Assert.Equal(HudCommandFamilies.All, adapter.Capabilities.Commands);
        Assert.False(adapter.Capabilities.Events.HasFlag(HudEventFamilies.Chat));
        Assert.True(adapter.Capabilities.Commands.HasFlag(HudCommandFamilies.SubmitChat));
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
        Assert.Equal(new HudPlateAssignment(new HudId("avatar")), events[3].UnitPresentation.Plate);
        Assert.False(events[3].UnitPresentation.OvertipCandidate);
    }

    [Fact]
    public void UnitProjectionAssignsOnlyTheAdmittedEntityToTheAvatarPlate()
    {
        var adapter = NewAdapter(ownEntityId: 9);

        adapter.Observe(Snapshot(1, Entity(9), Entity(10)));

        HudEvent[] events = Read(adapter, 4, out _);
        HudEvent avatar = Assert.Single(events, item => item.EntityId == 9);
        HudEvent worldUnit = Assert.Single(events, item => item.EntityId == 10);
        Assert.Equal(new HudPlateAssignment(new HudId("avatar")), avatar.UnitPresentation.Plate);
        Assert.False(avatar.UnitPresentation.OvertipCandidate);
        Assert.True(worldUnit.UnitPresentation.Plate.IsNone);
        Assert.True(worldUnit.UnitPresentation.OvertipCandidate);
    }

    [Fact]
    public void DeathRefreshPreservesTheUnitPresentationAssignment()
    {
        var adapter = NewAdapter(ownEntityId: 9);
        adapter.Observe(Spawn(1, Entity(9)));
        _ = Read(adapter, 2, out _);

        adapter.Observe(new ServerMessage
        {
            ServerTick = 2,
            DeathEvent = new DeathEvent { VictimEntityId = 9 },
        });

        HudEvent item = Assert.Single(Read(adapter, 2, out _));
        Assert.Equal(0, item.Value);
        Assert.Equal(new HudPlateAssignment(new HudId("avatar")), item.UnitPresentation.Plate);
        Assert.False(item.UnitPresentation.OvertipCandidate);
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
    public void ActionBarReplacementPublishesExactlyThirtySixOrderedSlotsAtomically()
    {
        var adapter = NewAdapter(options: new SessionHudAdapterOptions(ReliableEventCapacity: 36));
        ActionBarReplacement replacement = ActionBar(70);
        replacement.Slots[5] = new ActionBarSlotState
        {
            SlotIndex = 5,
            AbilityId = "ability.warrior.auto-attack",
            CooldownRemainingMilliseconds = 200,
            CooldownDurationMilliseconds = 500,
            Available = false,
            UnavailableReason = ActionUnavailableReason.OnCooldown,
        };
        var message = new ServerMessage { ServerTick = 900, ActionBarReplacement = replacement };
        byte[] before = message.ToByteArray();

        Assert.Equal(SessionHudObservation.Projected, adapter.Observe(message));
        Assert.Equal(before, message.ToByteArray());
        HudEvent[] events = Read(adapter, 36, out _);

        Assert.Equal(36, events.Length);
        Assert.Equal(Enumerable.Range(0, 36), events.Select(item => item.Slot));
        Assert.All(events, item => Assert.Equal((ulong)70, item.Stamp.Revision));
        Assert.Equal(HudEventKind.ActionSlotChanged, events[5].Kind);
        Assert.Equal("ability.warrior.auto-attack", events[5].ContentId.Value);
        Assert.Equal(200, events[5].Value);
        Assert.Equal(500, events[5].Auxiliary);
        Assert.False(events[5].Flag);
    }

    [Fact]
    public void MalformedActionBarFaultsWithoutPublishingAPartialReplacement()
    {
        var adapter = NewAdapter();
        ActionBarReplacement replacement = ActionBar(7);
        replacement.Slots.RemoveAt(35);

        Assert.Equal(
            SessionHudObservation.Terminal,
            adapter.Observe(new ServerMessage { ActionBarReplacement = replacement }));

        Assert.Empty(Read(adapter, 64, out HudSessionRead read));
        Assert.Equal(HudSessionState.Faulted, read.State);
        Assert.Equal(SessionHudFaultCode.InvalidServerPayload, adapter.Fault?.Code);
    }

    [Fact]
    public void TargetReplacementAtomicallyReassignsTheAuthoredTargetPlate()
    {
        var adapter = NewAdapter();
        adapter.Observe(Spawn(1, Entity(10)));
        adapter.Observe(Spawn(2, Entity(11)));
        _ = Read(adapter, 4, out _);

        adapter.Observe(new ServerMessage
        {
            TargetStateReplacement = new TargetStateReplacement
            {
                Revision = 10,
                HasAuthority = true,
                SelectedEntityId = 10,
                Refusal = TargetSelectRefusal.None,
            },
        });
        HudEvent[] first = Read(adapter, 4, out _);
        Assert.Equal([HudEventKind.UnitChanged, HudEventKind.TargetSelectionChanged], first.Select(item => item.Kind));
        Assert.Equal("target", first[0].UnitPresentation.Plate.SemanticId.Value);

        adapter.Observe(new ServerMessage
        {
            TargetStateReplacement = new TargetStateReplacement
            {
                Revision = 11,
                HasAuthority = true,
                SelectedEntityId = 11,
                Refusal = TargetSelectRefusal.None,
            },
        });
        HudEvent[] reassigned = Read(adapter, 5, out _);

        Assert.Equal([HudEventKind.UnitChanged, HudEventKind.UnitChanged, HudEventKind.TargetSelectionChanged],
            reassigned.Select(item => item.Kind));
        Assert.Equal((ulong)10, reassigned[0].EntityId);
        Assert.True(reassigned[0].UnitPresentation.Plate.IsNone);
        Assert.True(reassigned[0].UnitPresentation.OvertipCandidate);
        Assert.Equal((ulong)11, reassigned[1].EntityId);
        Assert.Equal("target", reassigned[1].UnitPresentation.Plate.SemanticId.Value);
        Assert.Equal((ulong)11, reassigned[2].EntityId);
    }

    [Fact]
    public void CharacterAndSparseInventoryReplacementsPreserveRetailCensusesAndCooldowns()
    {
        var adapter = NewAdapter(ownEntityId: 77);
        Assert.Equal(SessionHudObservation.Projected, adapter.Observe(new ServerMessage
        {
            CharacterStateReplacement = Character(20, 77, bagInstance: 900),
        }));
        HudEvent characterEvent = Assert.Single(Read(adapter, 2, out _));
        Assert.Equal(HudEventKind.CharacterReplaced, characterEvent.Kind);
        Assert.Equal(21, characterEvent.Character!.Equipment.Length);
        Assert.Equal(14, characterEvent.Character.Stats.Length);
        Assert.Equal((ulong)900, characterEvent.Character.Bag!.Value.InstanceId);

        InventoryStateReplacement inventory = Inventory(21, bagInstance: 900);
        inventory.Slots.Add(new InventorySlotState
        {
            SlotIndex = 17,
            Item = Item(102, "item.quest", 2),
        });
        Assert.Equal(SessionHudObservation.Projected, adapter.Observe(new ServerMessage
        {
            InventoryStateReplacement = inventory,
        }));
        HudEvent inventoryEvent = Assert.Single(Read(adapter, 2, out _));

        Assert.Equal(HudEventKind.InventoryReplaced, inventoryEvent.Kind);
        Assert.Equal(18, inventoryEvent.Inventory!.Slots.Length);
        Assert.Null(inventoryEvent.Inventory.Slots[0]);
        Assert.Equal((ulong)101, inventoryEvent.Inventory.Slots[2]!.Value.InstanceId);
        Assert.Equal("spell.item.heal", inventoryEvent.Inventory.Cooldowns[2]!.Value.SpellId.Value);
        Assert.Equal((ulong)102, inventoryEvent.Inventory.Slots[17]!.Value.InstanceId);
    }

    [Fact]
    public void InventoryCooldownRequiresMatchingRevisionAndItemAndMapsStartAndFinish()
    {
        var adapter = NewAdapter(ownEntityId: 77);
        adapter.Observe(new ServerMessage { CharacterStateReplacement = Character(10, 77, 900) });
        _ = Read(adapter, 2, out _);
        adapter.Observe(new ServerMessage { InventoryStateReplacement = Inventory(11, 900) });
        _ = Read(adapter, 2, out _);

        Assert.Equal(SessionHudObservation.Observed, adapter.Observe(new ServerMessage
        {
            InventorySlotCooldownUpdate = Cooldown(10, 2, 101, "spell.stale", 20, 30),
        }));
        Assert.Empty(Read(adapter, 2, out _));

        Assert.Equal(SessionHudObservation.Projected, adapter.Observe(new ServerMessage
        {
            InventorySlotCooldownUpdate = Cooldown(11, 2, 101, "spell.new", 250, 500),
        }));
        HudEvent started = Assert.Single(Read(adapter, 2, out _));
        Assert.Equal(HudEventKind.InventoryCooldownStarted, started.Kind);
        Assert.Equal("spell.new", started.ContentId.Value);

        Assert.Equal(SessionHudObservation.Projected, adapter.Observe(new ServerMessage
        {
            InventorySlotCooldownUpdate = new InventorySlotCooldownUpdate
            {
                InventoryRevision = 11,
                SlotIndex = 2,
                ItemInstanceId = 101,
            },
        }));
        HudEvent finished = Assert.Single(Read(adapter, 2, out _));
        Assert.Equal(HudEventKind.InventoryCooldownFinished, finished.Kind);
        Assert.Equal("spell.new", finished.ContentId.Value);
    }

    [Fact]
    public void LootReplacementKeepsTheDistinctIndexedLootShapeAndTypedRefusal()
    {
        var adapter = NewAdapter();
        var replacement = new LootStateReplacement
        {
            Revision = 30,
            RequestId = 4,
            LootEntityId = 91,
            Open = true,
            Refusal = LootUiRefusal.NotYourLoot,
            Money = 12,
            TotalCount = 2,
            PageSize = 4,
        };
        replacement.Items.Add(new LootItemState
        {
            ItemIndex = 1,
            ProductItemId = "item.second",
            Count = 3,
            IsCursed = true,
        });
        replacement.Items.Add(new LootItemState
        {
            ItemIndex = 0,
            ProductItemId = "item.first",
            Count = 1,
        });

        Assert.Equal(SessionHudObservation.Projected, adapter.Observe(new ServerMessage
        {
            LootStateReplacement = replacement,
        }));
        HudLootSnapshot loot = Assert.Single(Read(adapter, 2, out _)).Loot!;

        Assert.Equal(HudLootRefusal.NotOwner, loot.Refusal);
        Assert.Equal(["item.first", "item.second"], loot.Items.ToArray().Select(item => item.ItemId.Value));
        Assert.True(loot.Items[1].Cursed);
    }

    [Fact]
    public void QuestLogAndQuestInfoMapBoundedDocumentsRewardsAndTypedRefusals()
    {
        var adapter = NewAdapter();
        QuestLogReplacement log = QuestLog(40);

        Assert.Equal(SessionHudObservation.Projected, adapter.Observe(new ServerMessage
        {
            QuestLogReplacement = log,
        }));
        HudQuestLogSnapshot logSnapshot = Assert.Single(Read(adapter, 2, out _)).QuestLog!;
        HudQuestDocument logQuest = Assert.Single(logSnapshot.Quests.ToArray());
        Assert.Equal(HudQuestClientState.Completable, logQuest.State);
        Assert.Equal(3, logQuest.Objectives[0].Required);

        QuestInfoReplacement info = QuestInfo(41);
        Assert.Equal(SessionHudObservation.Projected, adapter.Observe(new ServerMessage
        {
            QuestInfoReplacement = info,
        }));
        HudQuestInfoSnapshot infoSnapshot = Assert.Single(Read(adapter, 2, out _)).QuestInfo!;

        Assert.Equal(HudQuestInfoMode.TurnIn, infoSnapshot.Mode);
        Assert.Equal(HudQuestRefusal.BagFull, infoSnapshot.Refusal);
        Assert.Equal("item.reward", Assert.Single(infoSnapshot.Reward.MandatoryItems.ToArray()).ItemId.Value);
        Assert.Equal((ulong)88, infoSnapshot.NpcEntityId);
    }

    [Fact]
    public void CharacterCensusFailureIsTerminalAndPublishesNothing()
    {
        var adapter = NewAdapter(ownEntityId: 77);
        CharacterStateReplacement character = Character(8, 77, 900);
        character.Equipment.RemoveAt(character.Equipment.Count - 1);

        Assert.Equal(SessionHudObservation.Terminal, adapter.Observe(new ServerMessage
        {
            CharacterStateReplacement = character,
        }));
        Assert.Empty(Read(adapter, 4, out HudSessionRead read));
        Assert.Equal(HudSessionState.Faulted, read.State);
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
        HudCommand activate = HudCommand.ActivateAction(3, new HudStamp(1, 9, 0));

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
    public void EveryStableCommandFamilyQueuesWithItsAuthorityRevision()
    {
        var adapter = NewAdapter(options: new SessionHudAdapterOptions(CommandCapacity: 24));
        var revision = new HudStamp(1, 50, 7);
        HudCommand[] commands =
        [
            HudCommand.ActivateAction(35, revision),
            HudCommand.SelectWorldEntity(0),
            HudCommand.SubmitChat(new HudId("hello")),
            HudCommand.InteractWorldEntity(3),
            HudCommand.MoveInventoryItem(1, 2, false, revision),
            HudCommand.DropInventoryItem(3, 2, revision),
            HudCommand.UseInventoryItem(4, revision),
            HudCommand.DressInventoryItem(5, revision),
            HudCommand.UndressInventoryItem(20, revision),
            HudCommand.TakeLootItem(9, 19, revision),
            HudCommand.TakeLootMoney(9, -1, revision),
            HudCommand.TakeAllLoot(9, revision),
            HudCommand.CloseLoot(),
            HudCommand.AbandonQuest(new HudId("quest.one"), revision),
            HudCommand.ShareQuest(new HudId("quest.one"), revision),
            HudCommand.AcceptSharedQuest(new HudId("share.1"), new HudId("quest.one"), revision),
            HudCommand.DeclineSharedQuest(new HudId("share.1"), new HudId("quest.one"), revision),
            HudCommand.AcceptQuest(new HudId("quest.offer"), 8, revision),
            HudCommand.TurnInQuest(new HudId("quest.done"), 8, -1, revision),
        ];

        Assert.All(commands, command => Assert.True(adapter.TryWrite(command), command.Kind.ToString()));
        var queued = new List<HudCommand>();
        while (adapter.TryTakeCommand(out HudCommand command))
        {
            queued.Add(command);
        }

        Assert.Equal(commands, queued);
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
    public void InvalidRevisionedCommandFailsClosed()
    {
        var adapter = NewAdapter();

        Assert.False(adapter.TryWrite(HudCommand.ActivateAction(2, new HudStamp(9, 1, 0))));

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

    private static ActionBarReplacement ActionBar(ulong revision)
    {
        var replacement = new ActionBarReplacement
        {
            Revision = revision,
            ActivationRefusal = ActionActivationRefusal.None,
        };
        for (uint slot = 0; slot < 36; slot++)
        {
            replacement.Slots.Add(new ActionBarSlotState
            {
                SlotIndex = slot,
                Available = false,
                UnavailableReason = ActionUnavailableReason.EmptySlot,
            });
        }

        return replacement;
    }

    private static CharacterStateReplacement Character(ulong revision, ulong entityId, ulong bagInstance)
    {
        var replacement = new CharacterStateReplacement
        {
            Revision = revision,
            CharacterEntityId = entityId,
            Name = "Ayla",
            Level = 7,
            Bag = Item(bagInstance, "item.bag-18", 1),
        };
        foreach (EquipmentSlotId slot in Enum.GetValues<EquipmentSlotId>())
        {
            replacement.Equipment.Add(new EquipmentSlotState { Slot = slot });
        }

        foreach (CharacterStatId stat in Enum.GetValues<CharacterStatId>())
        {
            replacement.Stats.Add(new CharacterStatState { Stat = stat, Base = (float)stat + 1 });
        }

        return replacement;
    }

    private static InventoryStateReplacement Inventory(ulong revision, ulong bagInstance)
    {
        var replacement = new InventoryStateReplacement
        {
            Revision = revision,
            LayoutId = InventoryLayoutId._18,
            Capacity = 18,
            Currency = 77,
            EquippedBagItemId = bagInstance,
        };
        replacement.PartitionSizes.Add(12);
        replacement.PartitionSizes.Add(6);
        replacement.Slots.Add(new InventorySlotState
        {
            SlotIndex = 2,
            Item = Item(101, "item.heal", 3),
            SpellCooldown = new ItemSlotSpellCooldownState
            {
                ProductSpellId = "spell.item.heal",
                RemainingMilliseconds = 100,
                DurationMilliseconds = 300,
            },
        });
        return replacement;
    }

    private static InventorySlotCooldownUpdate Cooldown(
        ulong revision,
        uint slot,
        ulong itemInstance,
        string spell,
        long remaining,
        long duration) => new()
    {
        InventoryRevision = revision,
        SlotIndex = slot,
        ItemInstanceId = itemInstance,
        SpellCooldown = new ItemSlotSpellCooldownState
        {
            ProductSpellId = spell,
            RemainingMilliseconds = remaining,
            DurationMilliseconds = duration,
        },
    };

    private static QuestLogReplacement QuestLog(ulong revision)
    {
        var replacement = new QuestLogReplacement
        {
            Revision = revision,
            SelectedQuestId = "quest.rats",
            DailyCount = 1,
            DailyLimit = 5,
            CommandRefusal = QuestLogCommandRefusal.None,
        };
        var entry = new QuestLogEntry
        {
            QuestId = "quest.rats",
            Name = "quest.rats.title",
            State = QuestUiState.ReadyToReturn,
            Level = 3,
        };
        entry.Objectives.Add(new QuestObjectiveState
        {
            Name = "quest.rats.objective.0",
            Progress = 3,
            Required = 3,
            Type = QuestObjectiveType.Kill,
            ShowCounterValue = true,
        });
        replacement.VisibleQuests.Add(entry);
        replacement.BookmarkQuestIds.Add("quest.rats");
        return replacement;
    }

    private static QuestInfoReplacement QuestInfo(ulong revision)
    {
        var replacement = new QuestInfoReplacement
        {
            Revision = revision,
            RequestedQuestId = "quest.rats",
            Mode = QuestInfoMode.TurnIn,
            NpcEntityId = 88,
            Refusal = QuestInfoRefusal.BagFull,
            Info = new QuestInfo
            {
                Id = "quest.rats",
                Name = "quest.rats.title",
                Goal = "quest.rats.goal",
                FinishText = "quest.rats.finish",
                CanCancel = true,
            },
            Progress = new QuestProgress
            {
                Id = "quest.rats",
                State = QuestUiState.ReadyToReturn,
            },
            Reward = new QuestReward
            {
                Experience = 10,
                Money = 5,
                MandatoryItemsCount = 1,
            },
        };
        replacement.Progress.Objectives.Add(new QuestObjectiveState
        {
            Name = "quest.rats.objective.0",
            Progress = 3,
            Required = 3,
            Type = QuestObjectiveType.Kill,
            ShowCounterValue = true,
        });
        replacement.Reward.MandatoryItems.Add(new QuestRewardItem
        {
            ProductItemId = "item.reward",
            Count = 1,
        });
        return replacement;
    }

    private static ItemStackState Item(ulong instance, string product, uint count) => new()
    {
        InstanceId = instance,
        ProductItemId = product,
        StackCount = count,
    };

    private static HudEvent[] Read(SessionHudAdapter adapter, int capacity, out HudSessionRead read)
    {
        var buffer = new HudEvent[capacity];
        read = adapter.Read(buffer);
        return buffer[..read.Count];
    }
}
