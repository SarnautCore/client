using Sarnaut.Protocol.V1;
using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class GameplayHudViewModelTests
{
    [Fact]
    public void Server_messages_dispatch_into_each_gameplay_model()
    {
        var hud = new GameplayHudViewModel(
            ownEntityId: 7,
            abilities: [new AbilityDefinition("ability.m2.strike", "ability.m2.strike.name", string.Empty)],
            inventoryCapacity: 2,
            stackLimit: _ => 20);
        hud.SelectTarget(new EntityHudSnapshot(42, "mob.name", "mob.earth", 2, 120, 120, true));

        hud.Route(new ServerMessage
        {
            CombatEvent = new CombatEvent
            {
                CasterId = 7,
                TargetId = 42,
                AbilityId = "ability.m2.strike",
                Damage = 20,
                TargetHealth = 100,
                TargetMaxHealth = 120,
                Rejection = AbilityRejection.None,
            },
        });

        Assert.Equal(100, hud.Target.Health);
        Assert.True(hud.Abilities.IsOnGlobalCooldown);
        Assert.Equal(1, hud.DamageNumbers.ActiveCount);

        hud.Route(new ServerMessage
        {
            DeathEvent = new DeathEvent { VictimEntityId = 42, KillerEntityId = 7 },
        });
        Assert.False(hud.Target.Alive);
        Assert.Equal(DeathFeedbackKind.TargetDefeated, hud.DeathFeedback.Kind);

        var offer = new LootOffer { CorpseEntityId = 42 };
        offer.Items.Add(new LootItem { ItemId = "item.trash-hoof", Count = 1 });
        hud.Route(new ServerMessage { LootOffer = offer });
        Assert.True(hud.Loot.IsOpen);

        hud.Route(new ServerMessage
        {
            LootResult = new LootResult { CorpseEntityId = 42, Refusal = LootRefusal.None },
        });
        Assert.False(hud.Loot.IsOpen);

        var bag = new InventoryUpdate { Currency = 4 };
        bag.Slots.Add(new InventorySlot { Slot = 0, ItemId = "item.trash-hoof", Count = 1 });
        hud.Route(new ServerMessage { InventoryUpdate = bag });
        Assert.Equal("item.trash-hoof", hud.Inventory.Slots[0]!.ItemId);

        hud.BeginInteraction(70);
        hud.Route(new ServerMessage { QuestStateUpdate = Quest(QuestState.Offered) });
        Assert.Equal(QuestDialogueMode.Offer, hud.Dialogue.Mode);
        Assert.Equal((ulong)70, hud.Dialogue.NpcEntityId);

        hud.Route(new ServerMessage { QuestStateUpdate = Quest(QuestState.Accepted) });
        Assert.Single(hud.QuestLog.Quests);
        Assert.Single(hud.QuestTracker.Quests);
        Assert.False(hud.Dialogue.IsOpen);

        hud.Route(new ServerMessage { QuestStateUpdate = Quest(QuestState.InProgress, current: 2) });
        Assert.Equal(2, hud.QuestLog.Quests[0].Objectives[0].Current);
        Assert.Equal(2, hud.QuestTracker.Quests[0].Objectives[0].Current);

        hud.Route(new ServerMessage { QuestStateUpdate = Quest(QuestState.Completable, current: 3) });
        Assert.True(hud.QuestTracker.Quests[0].Complete);
        Assert.False(hud.Dialogue.IsOpen);

        hud.BeginInteraction(71);
        hud.Route(new ServerMessage { QuestStateUpdate = Quest(QuestState.Completable, current: 3) });
        Assert.Equal(QuestDialogueMode.TurnIn, hud.Dialogue.Mode);
        Assert.Equal((ulong)71, hud.Dialogue.NpcEntityId);

        QuestStateUpdate turnedIn = Quest(QuestState.TurnedIn, current: 3);
        turnedIn.Experience = 8;
        turnedIn.Items.Add(new LootItem { ItemId = "item.reward.sword", Count = 1 });
        hud.Route(new ServerMessage { QuestStateUpdate = turnedIn });
        Assert.Empty(hud.QuestLog.Quests);
        Assert.Empty(hud.QuestTracker.Quests);
        Assert.False(hud.Dialogue.IsOpen);
        Assert.Equal(8, hud.Dialogue.LastReward!.Experience);
        Assert.Equal("item.reward.sword", Assert.Single(hud.Dialogue.LastReward.Items).ItemId);
    }

    [Fact]
    public void SpawnAndDespawnEventsRefreshAndClearTheSelectedTarget()
    {
        var hud = new GameplayHudViewModel(7, []);
        hud.SelectTarget(new EntityHudSnapshot(42, "mob.old", "mob.earth", 2, 20, 120, true));

        hud.Route(new ServerMessage
        {
            SpawnEvent = new SpawnEvent
            {
                Entity = new EntitySnapshot
                {
                    EntityId = 42,
                    NameKey = "mob.new",
                    ContentId = "mob.earth",
                    Level = 2,
                    Health = 120,
                    MaxHealth = 120,
                    Alive = true,
                },
            },
        });
        Assert.Equal("mob.new", hud.Target.NameKey);
        Assert.Equal(120, hud.Target.Health);

        hud.Route(new ServerMessage { DespawnEvent = new DespawnEvent { EntityId = 42 } });
        Assert.Equal((ulong)0, hud.Target.EntityId);
    }

    private static QuestStateUpdate Quest(QuestState state, int current = 0)
    {
        var update = new QuestStateUpdate
        {
            QuestId = "quest.overlay.m2.slay-earth-elementals",
            State = state,
            Refusal = QuestRefusal.None,
        };
        update.Objectives.Add(new QuestObjectiveProgress
        {
            Index = 0,
            Counter = current,
            Limit = 3,
            ShowCount = true,
            CounterKey = "quest.kill-earth-elementals",
        });
        return update;
    }
}
