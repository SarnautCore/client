using Sarnaut.Protocol.V1;
using SarnautCore.Gameplay;
using Xunit;

namespace SarnautCore.Gameplay.Tests;

public sealed class GameplayHudViewModelTests
{
    [Fact]
    public void Server_messages_dispatch_into_each_gameplay_model()
    {
        var quests = new SyntheticQuestAdapter();
        var hud = new GameplayHudViewModel(
            ownEntityId: 7,
            abilities: [new AbilityDefinition("ability.m2.strike", "ability.m2.strike.name", string.Empty)],
            inventoryCapacity: 2,
            stackLimit: _ => 20,
            questAdapter: quests);
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

        quests.Current = Quest(QuestClientState.Offered);
        hud.Route(new ServerMessage { QuestStateUpdate = new QuestStateUpdate() });
        Assert.Equal(QuestDialogueMode.Offer, hud.Dialogue.Mode);

        quests.Current = Quest(QuestClientState.Accepted);
        hud.Route(new ServerMessage { QuestStateUpdate = new QuestStateUpdate() });
        Assert.Single(hud.QuestLog.Quests);
        Assert.Single(hud.QuestTracker.Quests);
        Assert.False(hud.Dialogue.IsOpen);

        quests.Current = Quest(QuestClientState.Completable);
        hud.Route(new ServerMessage { QuestStateUpdate = new QuestStateUpdate() });
        Assert.True(hud.QuestTracker.Quests[0].Complete);

        quests.Current = Quest(QuestClientState.TurnedIn);
        hud.Route(new ServerMessage { QuestStateUpdate = new QuestStateUpdate() });
        Assert.Empty(hud.QuestLog.Quests);
        Assert.Empty(hud.QuestTracker.Quests);
    }

    private static QuestUpdate Quest(QuestClientState state) => new(
        "quest.overlay.m2.slay-earth-elementals",
        "quest.overlay.m2.slay-earth-elementals.title",
        "quest.overlay.m2.slay-earth-elementals.description",
        state,
        [new QuestObjectiveUpdate("quest.kill-earth-elementals", state == QuestClientState.Completable ? 3 : 0, 3, true, false)],
        StarterEntityId: 70,
        FinisherEntityId: 71);

    private sealed class SyntheticQuestAdapter : IQuestStateUpdateAdapter
    {
        public QuestUpdate Current { get; set; } = Quest(QuestClientState.Offered);

        public bool TryMap(QuestStateUpdate message, out QuestUpdate update)
        {
            update = Current;
            return true;
        }
    }
}
