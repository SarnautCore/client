using Sarnaut.Protocol.V1;
using SarnautCore.Networking;

namespace SarnautCore.Gameplay;

/// <summary>Maps the in-flight quest wire message into the stable HUD quest record.</summary>
public interface IQuestStateUpdateAdapter
{
    bool TryMap(QuestStateUpdate message, out QuestUpdate update);
}

/// <summary>
/// The plain-C# gameplay HUD. It is the single dispatch destination for server
/// events and the shared model tree for thin Godot controls.
/// </summary>
public sealed class GameplayHudViewModel
{
    private readonly IQuestStateUpdateAdapter _questAdapter;
    private readonly ServerMessageRouter _router;

    public GameplayHudViewModel(
        ulong ownEntityId,
        IEnumerable<AbilityDefinition> abilities,
        int inventoryCapacity = 16,
        Func<string, int>? stackLimit = null,
        IQuestStateUpdateAdapter? questAdapter = null,
        GameplayFocusOwner? focus = null)
    {
        OwnEntityId = ownEntityId;
        Target = new TargetViewModel();
        Abilities = new AbilityBarViewModel(abilities);
        DamageNumbers = new DamageNumberPoolViewModel();
        DeathFeedback = new DeathFeedbackViewModel();
        Loot = new LootWindowViewModel();
        Inventory = new InventoryViewModel(inventoryCapacity, stackLimit);
        QuestLog = new QuestLogViewModel();
        QuestTracker = new QuestTrackerViewModel();
        Dialogue = new QuestDialogueViewModel();
        Focus = focus ?? new GameplayFocusOwner();
        _questAdapter = questAdapter ?? NoQuestStateUpdateAdapter.Instance;
        _router = new ServerMessageRouter
        {
            SnapshotBatch = Apply,
            CombatEvent = Apply,
            DeathEvent = Apply,
            LootOffer = Loot.Apply,
            LootResult = Loot.Apply,
            InventoryUpdate = Inventory.Apply,
            QuestStateUpdate = Apply,
            Error = error => LastError = $"{error.Code}: {error.Detail}".TrimEnd(),
        };
    }

    public ulong OwnEntityId { get; private set; }

    public TargetViewModel Target { get; }

    public AbilityBarViewModel Abilities { get; }

    public DamageNumberPoolViewModel DamageNumbers { get; }

    public DeathFeedbackViewModel DeathFeedback { get; }

    public LootWindowViewModel Loot { get; }

    public InventoryViewModel Inventory { get; }

    public QuestLogViewModel QuestLog { get; }

    public QuestTrackerViewModel QuestTracker { get; }

    public QuestDialogueViewModel Dialogue { get; }

    public GameplayFocusOwner Focus { get; }

    public string LastError { get; private set; } = string.Empty;

    public ServerMessage.PayloadOneofCase Route(ServerMessage message) => _router.Route(message);

    public void SetOwnEntityId(ulong entityId) => OwnEntityId = entityId;

    public void SelectTarget(EntityHudSnapshot snapshot) => Target.Select(snapshot);

    public void ClearTarget() => Target.Clear();

    public void ObserveEntity(EntityHudSnapshot snapshot)
    {
        DeathFeedback.Observe(snapshot);
        if (Target.EntityId == snapshot.EntityId)
        {
            Target.Select(snapshot);
        }
    }

    public void ApplyQuest(QuestUpdate update)
    {
        Dialogue.Apply(update);
        QuestLog.Apply(update);
        QuestTracker.Apply(update);
        if (update.State == QuestClientState.Offered && update.StarterEntityId != 0)
        {
            Dialogue.ShowOffer(update, update.StarterEntityId);
        }
    }

    public void Advance(double deltaSeconds)
    {
        Abilities.Advance(deltaSeconds);
        DamageNumbers.Advance(deltaSeconds);
        DeathFeedback.Advance(deltaSeconds);
    }

    private void Apply(SnapshotBatch batch)
    {
        foreach (EntitySnapshot entity in batch.Entities)
        {
            ObserveEntity(ToHudSnapshot(entity));
        }
    }

    private void Apply(CombatEvent combatEvent)
    {
        Target.Apply(combatEvent);
        Abilities.Apply(combatEvent, OwnEntityId);
        if (combatEvent.Rejection == AbilityRejection.None && combatEvent.Damage > 0)
        {
            DamageNumbers.Spawn(combatEvent.TargetId, combatEvent.Damage, critical: false);
        }
    }

    private void Apply(DeathEvent deathEvent)
    {
        Target.Apply(deathEvent);
        DeathFeedback.Apply(deathEvent, OwnEntityId);
    }

    private void Apply(QuestStateUpdate questState)
    {
        if (_questAdapter.TryMap(questState, out QuestUpdate update))
        {
            ApplyQuest(update);
        }
    }

    private static EntityHudSnapshot ToHudSnapshot(EntitySnapshot entity) => new(
        entity.EntityId,
        entity.NameKey,
        entity.ContentId,
        checked((int)entity.Level),
        entity.Health,
        entity.MaxHealth,
        entity.Alive);

    private sealed class NoQuestStateUpdateAdapter : IQuestStateUpdateAdapter
    {
        internal static NoQuestStateUpdateAdapter Instance { get; } = new();

        public bool TryMap(QuestStateUpdate message, out QuestUpdate update)
        {
            update = null!;
            return false;
        }
    }
}
