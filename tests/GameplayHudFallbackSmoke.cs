using Godot;
using Sarnaut.Protocol.V1;
using SarnautCore.Gameplay;

namespace SarnautCore;

/// <summary>Instantiates every gameplay widget with the converted tree absent.</summary>
public partial class GameplayHudFallbackSmoke : Node
{
    private readonly System.Collections.Generic.List<string> _failures = [];

    public override async void _Ready()
    {
        var focus = new GameplayFocusOwner();
        var model = new GameplayHudViewModel(
            ownEntityId: 1,
            abilities: [new AbilityDefinition("ability.test", "ability.test.name", string.Empty)],
            inventoryCapacity: 4,
            stackLimit: _ => 20,
            focus: focus);
        var network = new ZoneNetworkLoop();
        var hud = new GameplayHudControl();
        hud.Initialize(model, network);
        AddChild(hud);

        model.SelectTarget(new EntityHudSnapshot(2, "creature.test.name", "creature.test", 3, 75, 100, true));
        model.Route(new ServerMessage
        {
            CombatEvent = new CombatEvent { CasterId = 1, TargetId = 2, Damage = 25 },
        });
        model.Route(new ServerMessage
        {
            DeathEvent = new DeathEvent { VictimEntityId = 2, KillerEntityId = 1 },
        });

        var offer = new LootOffer { CorpseEntityId = 2, Money = 9 };
        offer.Items.Add(new LootItem { ItemId = "item.test", Count = 2 });
        model.Route(new ServerMessage { LootOffer = offer });

        var inventory = new InventoryUpdate { Currency = 9 };
        inventory.Slots.Add(new InventorySlot { Slot = 0, ItemId = "item.test", Count = 2 });
        model.Route(new ServerMessage { InventoryUpdate = inventory });

        var accepted = new QuestUpdate(
            "quest.test",
            "quest.test.title",
            "quest.test.description",
            QuestClientState.InProgress,
            [new QuestObjectiveUpdate("quest.test.objective", 1, 3, true, false)],
            StarterEntityId: 2,
            FinisherEntityId: 2,
            CanAbandon: true);
        model.ApplyQuest(accepted);
        model.Dialogue.ShowOffer(accepted with { State = QuestClientState.Offered }, 2);
        focus.Open(GameplayWindow.Inventory);
        focus.Open(GameplayWindow.QuestLog);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Expect(Find<TargetFrameControl>(hud), "target frame fallback exists");
        Expect(Find<AbilityBarControl>(hud), "ability bar fallback exists");
        Expect(Find<DamageNumbersControl>(hud), "damage-number fallback exists");
        Expect(Find<DeathFeedbackControl>(hud), "death feedback fallback exists");
        Expect(Find<LootWindowControl>(hud), "loot fallback exists");
        Expect(Find<InventoryControl>(hud), "inventory fallback exists");
        Expect(Find<QuestLogControl>(hud), "quest log fallback exists");
        Expect(Find<QuestTrackerControl>(hud), "quest tracker fallback exists");
        Expect(Find<QuestDialogueControl>(hud), "quest dialogue fallback exists");
        Expect(hud.FindChildren("ConvertedChrome").Count == 0, "no converted chrome loaded");
        Expect(hud.FindChildren("*", "PanelContainer", true, false).Count >= 8, "fallback panels built");

        network.Free();
        bool passed = _failures.Count == 0;
        GD.Print($"GAMEPLAY_HUD_FALLBACK widgets=9 converted_chrome=0 result={(passed ? "PASS" : "FAIL")}");
        foreach (string failure in _failures)
        {
            GD.PushError($"GAMEPLAY_HUD_FALLBACK {failure}");
        }

        GetTree().Quit(passed ? 0 : 1);
    }

    private static bool Find<T>(Node root) where T : Node
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T || Find<T>(child))
            {
                return true;
            }
        }

        return false;
    }

    private void Expect(bool condition, string what)
    {
        if (!condition)
        {
            _failures.Add(what);
        }
    }
}
